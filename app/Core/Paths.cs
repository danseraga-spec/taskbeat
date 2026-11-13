using System.IO;

namespace CoolPlayer;

/// <summary>
/// Папки данных. Путь всегда абсолютный: если Windows по какой-то причине не отдала папку
/// AppData (бывает сразу после входа в систему), берём её из переменных окружения/профиля —
/// иначе получился бы относительный путь от рабочей папки и настройки «пропали» бы.
/// </summary>
static class Paths
{
    public static readonly string DataDir = Resolve(Environment.SpecialFolder.ApplicationData, "APPDATA", @"AppData\Roaming");
    public static readonly string LocalDir = Resolve(Environment.SpecialFolder.LocalApplicationData, "LOCALAPPDATA", @"AppData\Local");

    static string Resolve(Environment.SpecialFolder f, string env, string rel)
    {
        string? root = null;
        try { root = Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify); } catch { }
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) root = Environment.GetEnvironmentVariable(env);
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root))
        {
            var profile = Environment.GetEnvironmentVariable("USERPROFILE");
            if (string.IsNullOrWhiteSpace(profile)) profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = Path.Combine(profile, rel);
        }
        // переименование CoolPlayer → Taskbeat: старую папку переносим, чтобы не потерять вход и настройки
        string dir = Path.Combine(root, "Taskbeat"), old = Path.Combine(root, "CoolPlayer");
        if (!Directory.Exists(dir) && Directory.Exists(old))
        {
            try { Directory.Move(old, dir); }
            catch { return old; }
        }
        return dir;
    }

    /// <summary>Запасной журнал запуска — пишется даже если папка данных недоступна.</summary>
    public static void Trace(string msg)
    {
        string line = $"{DateTime.Now:yyyy-MM-ddTHH:mm:ss} {msg}{Environment.NewLine}";
        foreach (var dir in new[] { DataDir, Path.Combine(Path.GetTempPath(), "Taskbeat") })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var f = Path.Combine(dir, "startup.log");
                if (File.Exists(f) && new FileInfo(f).Length > 256 * 1024) File.Delete(f);
                File.AppendAllText(f, line);
                return;
            }
            catch { }
        }
    }

    /// <summary>Чтение с повторами: при входе в Windows файл может ненадолго держать антивирус/индексатор.</summary>
    public static T Retry<T>(Func<T> read, int attempts = 6)
    {
        for (int i = 1; ; i++)
        {
            try { return read(); }
            catch (IOException) when (i < attempts) { Thread.Sleep(250 * i); }
            catch (UnauthorizedAccessException) when (i < attempts) { Thread.Sleep(250 * i); }
        }
    }

    /// <summary>Атомарная запись: сначала во временный файл, потом подмена — файл не обрежется при выключении ПК.</summary>
    public static void WriteAtomic(string path, byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            fs.Write(data);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(tmp, path);
    }
}
