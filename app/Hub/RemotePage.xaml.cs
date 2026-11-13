using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GSMTC = global::Windows.Media.Control;

namespace CoolPlayer;

sealed class SessionVm
{
    public required GSMTC.GlobalSystemMediaTransportControlsSession Session { get; init; }
    public string App { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public bool Playing { get; init; }
    public string PlayGlyph => Playing ? "" : "";
    public string Letter => App.Length > 0 ? App[..1].ToUpperInvariant() : "?";
    public Brush Color => new SolidColorBrush(App switch
    {
        "Spotify" => System.Windows.Media.Color.FromRgb(30, 215, 96),
        "Яндекс Музыка" or "Яндекс Браузер" => System.Windows.Media.Color.FromRgb(255, 204, 0),
        "VK" => System.Windows.Media.Color.FromRgb(0, 119, 255),
        "Taskbeat" => System.Windows.Media.Color.FromRgb(255, 194, 74),
        "Chrome" => System.Windows.Media.Color.FromRgb(120, 180, 255),
        "Edge" => System.Windows.Media.Color.FromRgb(70, 200, 210),
        _ => System.Windows.Media.Color.FromRgb(200, 200, 210),
    });
}

/// <summary>Пульт для всех медиасессий Windows. Подписан на события только пока открыт.</summary>
internal partial class RemotePage : UserControl
{
    readonly DispatcherTimer debounce = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
    readonly List<GSMTC.GlobalSystemMediaTransportControlsSession> watched = [];
    bool hooked;

    public RemotePage()
    {
        InitializeComponent();
        debounce.Tick += async (_, _) => { debounce.Stop(); await RefreshAsync(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) OnShown(); else Unhook(); };
    }

    public void OnShown()
    {
        var mgr = AppHost.Media.Manager;
        if (mgr != null && !hooked)
        {
            mgr.SessionsChanged += OnAny;
            hooked = true;
        }
        Kick();
    }

    void Unhook()
    {
        var mgr = AppHost.Media.Manager;
        if (mgr != null && hooked) mgr.SessionsChanged -= OnAny;
        hooked = false;
        foreach (var s in watched)
        {
            s.MediaPropertiesChanged -= OnProps;
            s.PlaybackInfoChanged -= OnPlayback;
        }
        watched.Clear();
    }

    void OnAny(GSMTC.GlobalSystemMediaTransportControlsSessionManager m, GSMTC.SessionsChangedEventArgs a) => Kick();
    void OnProps(GSMTC.GlobalSystemMediaTransportControlsSession s, GSMTC.MediaPropertiesChangedEventArgs a) => Kick();
    void OnPlayback(GSMTC.GlobalSystemMediaTransportControlsSession s, GSMTC.PlaybackInfoChangedEventArgs a) => Kick();
    void Kick() => Dispatcher.InvokeAsync(() => { debounce.Stop(); debounce.Start(); });

    async Task RefreshAsync()
    {
        var mgr = AppHost.Media.Manager;
        if (mgr == null) return;
        foreach (var s in watched) { s.MediaPropertiesChanged -= OnProps; s.PlaybackInfoChanged -= OnPlayback; }
        watched.Clear();

        var list = new List<SessionVm>();
        foreach (var s in mgr.GetSessions())
        {
            try
            {
                s.MediaPropertiesChanged += OnProps;
                s.PlaybackInfoChanged += OnPlayback;
                watched.Add(s);
                var p = await s.TryGetMediaPropertiesAsync();
                list.Add(new SessionVm
                {
                    Session = s,
                    App = MediaService.FriendlyApp(s.SourceAppUserModelId),
                    Title = string.IsNullOrEmpty(p?.Title) ? "Без названия" : p!.Title,
                    Artist = p?.Artist ?? "",
                    Playing = s.GetPlaybackInfo()?.PlaybackStatus == GSMTC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                });
            }
            catch (Exception ex) { Program.Log(ex); }
        }
        Sessions.ItemsSource = list;
        Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    static GSMTC.GlobalSystemMediaTransportControlsSession? Of(object s) => ((s as FrameworkElement)?.DataContext as SessionVm)?.Session;

    void ToggleClick(object s, RoutedEventArgs e) => _ = Of(s)?.TryTogglePlayPauseAsync();
    void NextClick(object s, RoutedEventArgs e) => _ = Of(s)?.TrySkipNextAsync();
    void PrevClick(object s, RoutedEventArgs e) => _ = Of(s)?.TrySkipPreviousAsync();

    void OpenSpotify(object s, RoutedEventArgs e) => AppHost.OpenUri("spotify:");
    void OpenYandexWeb(object s, RoutedEventArgs e) => AppHost.ShowHub("web:yandex");
}
