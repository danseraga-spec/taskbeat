using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace CoolPlayer;

/// <summary>
/// Вкладка с официальным веб-плеером сервиса. Создаётся только при первом открытии,
/// а скрытая и молчащая — усыпляется. Держит «голый» контроллер WebView2, поэтому
/// её можно пересадить в окно картинки-в-картинке без перезагрузки видео.
/// </summary>
sealed class WebSurface(string key, string title, string home)
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Home { get; } = home;
    public CoreWebView2Controller? Controller { get; private set; }
    public CoreWebView2? Web => Controller?.CoreWebView2;
    public bool InPip { get; set; }
    public event Action? StateChanged;
    Task? creating;

    public bool PlayingAudio => Web?.IsDocumentPlayingAudio ?? false;
    public string Url => Web?.Source ?? "";

    public Task EnsureAsync(IntPtr parent) => creating ??= CreateAsync(parent);

    async Task CreateAsync(IntPtr parent)
    {
        var env = await WebHub.EnvironmentAsync();
        Controller = await env.CreateCoreWebView2ControllerAsync(parent);
        Controller.DefaultBackgroundColor = System.Drawing.Color.Black;
        var s = Controller.CoreWebView2.Settings;
        s.IsStatusBarEnabled = false;
        s.AreDefaultContextMenusEnabled = true;
        s.IsZoomControlEnabled = true;
        Web!.NewWindowRequested += (_, e) =>
        {
            // ссылки открываем здесь же, окна входа через сторонние сервисы — отдельным окном
            if (e.IsUserInitiated && !e.Uri.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                && !e.Uri.Contains("login", StringComparison.OrdinalIgnoreCase) && !e.Uri.Contains("passport", StringComparison.OrdinalIgnoreCase))
            {
                e.Handled = true;
                Web.Navigate(e.Uri);
            }
        };
        Web.IsDocumentPlayingAudioChanged += (_, _) => StateChanged?.Invoke();
        Web.SourceChanged += (_, _) => StateChanged?.Invoke();
        Web.Navigate(Home);
    }

    public void Show(IntPtr parent, System.Drawing.Rectangle bounds)
    {
        if (Controller == null) return;
        if (Controller.ParentWindow != parent) Controller.ParentWindow = parent;
        Controller.Bounds = bounds;
        if (!Controller.IsVisible)
        {
            Web?.Resume();
            Controller.IsVisible = true;
        }
    }

    public void Hide()
    {
        if (Controller == null || InPip) return;
        Controller.IsVisible = false;
    }

    /// <summary>Усыпить, если скрыта и ничего не играет — почти ноль CPU и меньше памяти.</summary>
    public async Task TrySuspendAsync()
    {
        if (Controller is { IsVisible: false } && !PlayingAudio && !InPip && SettingsStore.Current.SuspendTabs)
        {
            try { await Web!.TrySuspendAsync(); } catch { }
        }
    }

    public void Dispose() => Controller?.Close();
}

sealed class WebHub : IDisposable
{
    static Task<CoreWebView2Environment>? env;

    public static Task<CoreWebView2Environment> EnvironmentAsync() => env ??= CoreWebView2Environment.CreateAsync(
        null, Path.Combine(AppHost.LocalDir, "Web"),
        new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required --disable-features=msWebOOUI,msPdfOOUI"));

    public WebSurface YouTube { get; } = new("youtube", "YouTube", "https://www.youtube.com/");
    public WebSurface SoundCloud { get; } = new("soundcloud", "SoundCloud", "https://soundcloud.com/discover");
    public WebSurface Yandex { get; } = new("yandex", "Яндекс Музыка", "https://music.yandex.ru/");
    public IEnumerable<WebSurface> All => [YouTube, SoundCloud, Yandex];

    readonly DispatcherTimer sleeper = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(30) };
    PipWindow? pip;
    public event Action? PipStateChanged;

    public WebHub()
    {
        sleeper.Tick += async (_, _) =>
        {
            sleeper.Stop();
            foreach (var s in All) await s.TrySuspendAsync();
        };
        foreach (var s in All) s.StateChanged += () => PipStateChanged?.Invoke();
    }

    public WebSurface? Get(string key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>Запланировать усыпление скрытых вкладок.</summary>
    public void ScheduleSleep()
    {
        sleeper.Stop();
        sleeper.Interval = TimeSpan.FromSeconds(Perf.FastSleep ? 5 : 30);
        sleeper.Start();
    }

    // ------------------------------------------------------------------ картинка в картинке

    public bool CanPip => pip == null && YouTube.Controller != null &&
                          (YouTube.Url.Contains("/watch") || YouTube.Url.Contains("/shorts/") || YouTube.Url.Contains("/live/"));
    public bool InPip => pip != null;

    public void EnterPip()
    {
        if (!CanPip) return;
        var s = YouTube;
        s.InPip = true;
        pip = new PipWindow(s);
        pip.Closed += (_, _) => { pip = null; PipStateChanged?.Invoke(); };
        pip.Show();
        PipStateChanged?.Invoke();
    }

    public void ExitPip(bool openHub)
    {
        pip?.Close();
        if (openHub) AppHost.ShowHub("web:youtube");
    }

    public void Dispose()
    {
        pip?.Close();
        foreach (var s in All) s.Dispose();
    }
}
