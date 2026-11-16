using System.IO;
using System.Windows;

namespace CoolPlayer;

static class Program
{
    const string MutexName = @"Local\Taskbeat", ShowEventName = @"Local\Taskbeat.Show";

    [STAThread]
    static void Main(string[] args)
    {
        L.Init(SettingsStore.Current.Language);
        if (L.En)
        {
            System.Threading.Thread.CurrentThread.CurrentUICulture = new System.Globalization.CultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = new System.Globalization.CultureInfo("en-US");
        }
        if (args.Contains("--uninstall")) { Installer.Uninstall(); return; }
        // после установки/обновления новый экземпляр ждёт, пока старый закроется
        int wi = Array.IndexOf(args, "--wait");
        if (wi >= 0 && wi + 1 < args.Length && int.TryParse(args[wi + 1], out int pid))
            try { System.Diagnostics.Process.GetProcessById(pid).WaitForExit(15000); } catch { }

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            // уже запущен — просим первый экземпляр показать хаб
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
            return;
        }

        CrashReport.OnStart();
        Paths.Trace($"запуск {Environment.ProcessPath} [{string.Join(' ', args)}] папка данных={Paths.DataDir} рабочая={Environment.CurrentDirectory}");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // при выключении/перезагрузке Windows — успеть сохранить настройки
        app.SessionEnding += (_, _) => { Paths.Trace("завершение сеанса Windows"); SettingsStore.Save(); BannerGuard.Restore(); CrashReport.OnCleanExit(); };
        // ошибка в одном месте не должна ронять плеер — пишем в лог и живём дальше
        app.DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(showEvent,
            (_, _) => app.Dispatcher.BeginInvoke(() => AppHost.ShowHub()), null, -1, false);

        AppHost.Start(startHidden: args.Contains("--tray") || SettingsStore.Current.StartMinimized, args);
        app.Run();
    }

    public static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppHost.DataDir);
            File.AppendAllText(Path.Combine(AppHost.DataDir, "error.log"), $"{DateTime.Now:s} {ex}\n\n");
        }
        catch { }
    }
}
