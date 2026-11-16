using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace CoolPlayer;

/// <summary>
/// Обновления из GitHub Releases. Проверка при запуске и раз в 12 часов; новая версия
/// скачивается по кнопке, затем маленький скрипт подменяет файлы после выхода и запускает Taskbeat снова.
/// </summary>
static class Updater
{
    const string Repo = "danseraga-spec/taskbeat";
    public const string ReleasesPage = "https://github.com/" + Repo + "/releases";
    static readonly HttpClient Http = CreateHttp();
    static DispatcherTimer? timer;

    public sealed record Release(Version Version, string Tag, string Notes, string ZipUrl, long Size, string Page);

    public static Version Current { get; } = typeof(Updater).Assembly.GetName().Version is { } v ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(1, 0, 0);
    public static Release? Available { get; private set; }
    public static event Action? Changed;
    public static bool Busy { get; private set; }
    public static double Progress { get; private set; }

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Taskbeat-Updater");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    public static void Start()
    {
        CleanupOld();
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        timer.Tick += async (_, _) =>
        {
            timer!.Interval = TimeSpan.FromHours(12);
            if (SettingsStore.Current.AutoUpdateCheck) await CheckAsync(notify: true);
        };
        timer.Start();
    }

    /// <summary>Есть ли версия новее. notify — показать на острове.</summary>
    public static async Task<Release?> CheckAsync(bool notify)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            string tag = r.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var ver)) return null;
            ver = new Version(ver.Major, ver.Minor, Math.Max(0, ver.Build));
            string zip = "", page = r.TryGetProperty("html_url", out var hu) ? hu.GetString() ?? ReleasesPage : ReleasesPage;
            long size = 0;
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip = a.GetProperty("browser_download_url").GetString() ?? "";
                    size = a.GetProperty("size").GetInt64();
                }
            }
            string notes = r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            Available = ver > Current && zip.Length > 0 ? new Release(ver, tag, notes, zip, size, page) : null;
            Changed?.Invoke();
            if (Available != null && notify && SettingsStore.Current.SkippedVersion != Available.Tag)
            {
                AppHost.Island?.Notify($"Taskbeat {Available.Version.ToString(3)}", L.T("доступно обновление"), System.Windows.Media.Color.FromRgb(255, 194, 74));
                AppHost.WhenNotBusy(() => UpdateWindow.Offer(manual: false)); // только предложение — ставится по кнопке «Обновить»
            }
            return Available;
        }
        catch (Exception ex)
        {
            Paths.Trace("обновления: " + ex.Message);
            return null;
        }
    }

    /// <summary>Скачать, распаковать и перезапуститься новой версией.</summary>
    public static async Task<string?> InstallAsync()
    {
        if (Available is not { } rel || Busy) return null;
        Busy = true;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Taskbeat-update");
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            string zipPath = Path.Combine(root, "update.zip"), dir = Path.Combine(root, "new");

            using (var resp = await Http.GetAsync(rel.ZipUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? rel.Size;
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(zipPath);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if (total > 0) { Progress = done / (double)total; Changed?.Invoke(); }
                }
            }
            if (rel.Size > 0 && new FileInfo(zipPath).Length != rel.Size) throw new IOException("размер архива не совпадает");

            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, dir));
            // архив может содержать файлы в корне или в одной вложенной папке
            if (!File.Exists(Path.Combine(dir, "Taskbeat.exe")) && Directory.GetDirectories(dir) is [var only] && File.Exists(Path.Combine(only, "Taskbeat.exe")))
                dir = only;
            if (!File.Exists(Path.Combine(dir, "Taskbeat.exe"))) throw new IOException("в архиве нет Taskbeat.exe");

            string target = AppContext.BaseDirectory.TrimEnd('\\');
            string script = Path.Combine(root, "apply.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                $"for /l %%i in (1,1,30) do (tasklist /fi \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && timeout /t 1 /nobreak >nul)\r\n" +
                $"robocopy \"{dir}\" \"{target}\" /E /R:3 /W:1 /NFL /NDL /NJH /NJS /NP >nul\r\n" +
                $"start \"\" \"{Path.Combine(target, "Taskbeat.exe")}\" --updated\r\n");
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
            Paths.Trace($"обновление до {rel.Tag}: файлы подготовлены, перезапуск");
            AppHost.Exit();
            return null;
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            Busy = false;
            Changed?.Invoke();
            return L.T("Не удалось обновиться") + ": " + ex.Message;
        }
    }

    static void CleanupOld()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Taskbeat-update");
            if (Directory.Exists(root) && Directory.GetLastWriteTime(root) < DateTime.Now.AddMinutes(-5)) Directory.Delete(root, true);
        }
        catch { }
    }
}
