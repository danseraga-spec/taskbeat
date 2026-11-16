using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace CoolPlayer;

/// <summary>
/// Установка без отдельного установщика: Taskbeat копирует себя в %LOCALAPPDATA%\Programs\Taskbeat,
/// создаёт ярлык в «Пуске» и запись в «Приложения» Windows. Удаление оттуда возвращает
/// всплывающие окна уведомлений, убирает автозапуск и (по желанию) настройки.
/// </summary>
static class Installer
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Taskbeat";
    public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Taskbeat");
    static string Shortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Taskbeat.lnk");
    static string Here => AppContext.BaseDirectory.TrimEnd('\\');

    public static bool IsInstalled => Here.Equals(InstallDir, StringComparison.OrdinalIgnoreCase);

    /// <summary>При запуске: обновить запись в «Приложениях» или предложить установку.</summary>
    public static void OnStartup()
    {
        if (IsInstalled) { Register(); return; }
        // спрашиваем один раз и только когда окно Taskbeat открыто (не при автозапуске и не поверх игры)
        if (SettingsStore.Current.InstallPrompted || !AppHost.HubVisible || AppHost.Island?.AnyFullscreen == true) return;
        SettingsStore.Current.InstallPrompted = true;
        SettingsStore.Save();
        var r = MessageBox.Show(
            L.T("Установить Taskbeat?\n\nОн появится в меню «Пуск» и в списке приложений Windows, обновления будут ставиться в одно место. Можно и дальше запускать из этой папки."),
            "Taskbeat", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r == MessageBoxResult.Yes) Install();
    }

    public static void Install()
    {
        try
        {
            Directory.CreateDirectory(InstallDir);
            CopyDir(Here, InstallDir);
            // новый экземпляр подождёт, пока этот закроется
            Process.Start(new ProcessStartInfo(Path.Combine(InstallDir, "Taskbeat.exe"), $"--wait {Environment.ProcessId} --installed") { UseShellExecute = true });
            AppHost.Exit();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            MessageBox.Show(L.T("Не удалось установить") + ": " + ex.Message, "Taskbeat", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void CopyDir(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to), true);
    }

    /// <summary>Ярлык в «Пуске» и запись в «Приложениях» (обновляется версия при каждом запуске).</summary>
    static void Register()
    {
        try
        {
            string exe = Path.Combine(InstallDir, "Taskbeat.exe");
            if (!File.Exists(Shortcut)) CreateShortcut(Shortcut, exe);
            using var k = Registry.CurrentUser.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "Taskbeat");
            k.SetValue("DisplayVersion", Updater.Current.ToString(3));
            k.SetValue("Publisher", "Taskbeat");
            k.SetValue("DisplayIcon", exe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            k.SetValue("URLInfoAbout", "https://github.com/danseraga-spec/taskbeat");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long size = Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1024;
            k.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
        }
        catch (Exception ex) { Paths.Trace("установка: " + ex.Message); }
    }

    static void CreateShortcut(string path, string target)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        dynamic lnk = shell.CreateShortcut(path);
        lnk.TargetPath = target;
        lnk.WorkingDirectory = Path.GetDirectoryName(target);
        lnk.IconLocation = target + ",0";
        lnk.Description = "Taskbeat";
        lnk.Save();
    }

    /// <summary>Taskbeat.exe --uninstall (из «Приложений» Windows).</summary>
    public static void Uninstall()
    {
        var r = MessageBox.Show(
            L.T("Удалить Taskbeat?\n\nНажмите «Да», чтобы удалить и настройки со входом в VK, или «Нет», чтобы оставить их на случай переустановки."),
            L.T("Удаление Taskbeat"), MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return;
        bool wipe = r == MessageBoxResult.Yes;

        // закрыть работающий экземпляр
        foreach (var p in Process.GetProcessesByName("Taskbeat").Where(p => p.Id != Environment.ProcessId))
            try { p.Kill(); p.WaitForExit(5000); } catch { }

        BannerGuard.Restore();
        CrashReport.OnCleanExit();
        Autostart.Enabled = false;
        try { File.Delete(Shortcut); } catch { }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (wipe)
        {
            try { Directory.Delete(Paths.DataDir, true); } catch { }
            try { Directory.Delete(Paths.LocalDir, true); } catch { }
        }
        // папку с самим exe удаляет скрипт после нашего выхода
        string dir = Here;
        string script = Path.Combine(Path.GetTempPath(), "taskbeat-uninstall.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"for /l %%i in (1,1,20) do (tasklist /fi \"PID eq {Environment.ProcessId}\" | find \"{Environment.ProcessId}\" >nul && timeout /t 1 /nobreak >nul)\r\n" +
            $"rmdir /s /q \"{dir}\"\r\n" +
            "del \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false });
        MessageBox.Show(L.T("Taskbeat удалён."), "Taskbeat", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
