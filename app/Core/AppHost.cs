using System.Diagnostics;
using System.IO;
using System.Windows;

namespace CoolPlayer;

/// <summary>Одно место, где живут все сервисы: остров, хаб, плеер, VK, веб-вкладки.</summary>
static class AppHost
{
    public static readonly string DataDir = Paths.DataDir;
    public static readonly string LocalDir = Paths.LocalDir;

    public static MediaService Media { get; private set; } = null!;
    public static WeatherService Weather { get; private set; } = null!;
    public static Scheduler Scheduler { get; private set; } = null!;
    public static Player Player { get; private set; } = null!;
    public static VkApi Vk { get; private set; } = null!;
    public static WebHub Web { get; private set; } = null!;
    public static IslandWindow Island { get; private set; } = null!;
    static HubWindow? hub;

    public static void Start(bool startHidden, string[] args)
    {
        Autostart.Migrate();
        var ui = Application.Current.Dispatcher;
        XamlTranslator.Register();
        var theme = new ResourceDictionary { Source = new Uri("pack://application:,,,/Hub/Theme.xaml") };
        Themes.Apply(theme);
        Application.Current.Resources.MergedDictionaries.Add(theme);
        Media = new MediaService(ui);
        Weather = new WeatherService();
        Scheduler = new Scheduler();
        Player = new Player(ui);
        Vk = new VkApi();
        Web = new WebHub();

        Island = new IslandWindow();
        Island.Show();

        ui.BeginInvoke(() =>
        {
            Media.Start();
            Weather.Start();
            Scheduler.Start();
            _ = Vk.LoadTokenAsync();
            _ = SettingsStore.RecoverAsync();
            if (!startHidden) ShowHub();
            Perf.Apply();
            Updater.Start();
            Live.Start();
            LastFm.Start();
            DiscordPresence.Start();
            if (args.Contains("--installed")) Toast(L.T("Taskbeat установлен — он есть в меню «Пуск»"));
            if (args.Contains("--updated")) Toast(L.F("Taskbeat обновлён до {0}", Updater.Current.ToString(3)));
            ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                Installer.OnStartup();
                WhenNotBusy(CrashReport.OfferIfCrashed);
            });
        });
    }

    /// <summary>Пауза/следующий/предыдущий: для того, что играет сейчас, — у нас или в другом приложении.</summary>
    public static void Transport(string cmd)
    {
        var p = Player.Current;
        var m = Media.State;
        bool ours = p != null && (!m.Has || m.Title == p.Title);
        if (!ours && m.Has) { Media.Command(cmd); return; }
        switch (cmd)
        {
            case "toggle": Player.Toggle(); break;
            case "next": Player.Next(); break;
            case "prev": Player.Previous(); break;
        }
    }

    public static void ShowHub(string? page = null, int tab = 0)
    {
        hub ??= new HubWindow();
        if (page != null) hub.Navigate(page, tab);
        hub.ShowAndActivate();
        if (!Installer.IsInstalled && !SettingsStore.Current.InstallPrompted)
            Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, Installer.OnStartup);
    }

    public static HubWindow? Hub => hub;
    public static void RefreshHubWeb() => hub?.RefreshWeb();
    public static void Toast(string text)
    {
        if (hub?.IsVisible == true) hub.ShowToast(text);
        else Island?.Notify(text, "", System.Windows.Media.Colors.White);
    }
    public static bool HubVisible => hub?.IsVisible == true;
    public static IntPtr HubHandle => hub?.Handle ?? IntPtr.Zero;

    public static void Exit()
    {
        CrashReport.OnCleanExit();
        DiscordPresence.Stop();
        try { Web.Dispose(); } catch { }
        try { Player.Dispose(); } catch { }
        Island.Cleanup();
        Application.Current.Shutdown();
    }

    /// <summary>Показать окно, когда пользователь не в полноэкранной игре/видео (проверка раз в 5 с).</summary>
    public static void WhenNotBusy(Action show)
    {
        if (Island == null || !Island.AnyFullscreen) { show(); return; }
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) =>
        {
            if (Island.AnyFullscreen) return;
            t.Stop();
            show();
        };
        t.Start();
    }

    /// <summary>Перезапуск (язык, тема): новый экземпляр ждёт, пока этот закроется.</summary>
    public static void Restart()
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--wait {Environment.ProcessId}") { UseShellExecute = true }); }
        catch (Exception ex) { Program.Log(ex); return; }
        Exit();
    }

    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Program.Log(ex); }
    }
}
