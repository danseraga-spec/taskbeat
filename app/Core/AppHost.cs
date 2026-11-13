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

    public static void Start(bool startHidden)
    {
        Autostart.Migrate();
        var ui = Application.Current.Dispatcher;
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("pack://application:,,,/Hub/Theme.xaml") });
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
        try { Web.Dispose(); } catch { }
        try { Player.Dispose(); } catch { }
        Island.Cleanup();
        Application.Current.Shutdown();
    }

    public static void OpenUri(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
        catch (Exception ex) { Program.Log(ex); }
    }
}
