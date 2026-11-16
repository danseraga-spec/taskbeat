using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CoolPlayer;

internal partial class HubWindow : Window
{
    readonly Dictionary<string, FrameworkElement> pages = [];
    readonly DispatcherTimer tick = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer saveLater = new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    string current = "";
    bool seeking;
    Color accent;

    public IntPtr Handle { get; private set; }

    public HubWindow()
    {
        InitializeComponent();
#pragma warning disable WPF0001 // Fluent-стили для стандартных контролов
        ThemeMode = Themes.IsLight ? ThemeMode.Light : ThemeMode.Dark;
#pragma warning restore WPF0001
        Background = Brushes.Black;
        Volume.Value = AppHost.Player.Volume;

        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            int round = 2, dark = 1;
            DwmSetWindowAttribute(Handle, 33, ref round, 4); // скруглённые углы Windows 11
            DwmSetWindowAttribute(Handle, 20, ref dark, 4);  // тёмная рамка
        };
        AppHost.Media.Changed += _ => UpdateBar();
        AppHost.Player.Changed += UpdateBar;
        AppHost.Player.Error += ShowToast;
        AppHost.Player.SleepChanged += UpdateSleepText;
        Loaded += (_, _) => UpdateSleepText();
        AppHost.Web.PipStateChanged += UpdatePip;
        tick.Tick += (_, _) => UpdateProgress();
        saveLater.Tick += (_, _) => { saveLater.Stop(); SettingsStore.Save(); };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };

        StateChanged += (_, _) =>
        {
            bool max = WindowState == WindowState.Maximized;
            MaxBtn.Content = max ? "" : "";
            Root.Margin = max ? new Thickness(7) : new Thickness(0); // WindowChrome вылезает за экран при разворачивании
        };
        LocationChanged += (_, _) => { foreach (var s in AppHost.Web.All) s.Controller?.NotifyParentWindowPositionChanged(); };
        IsVisibleChanged += (_, _) =>
        {
            tick.IsEnabled = IsVisible;
            tick.Interval = TimeSpan.FromMilliseconds(Perf.Game ? 1000 : 500);
            if (!IsVisible)
            {
                AppHost.Web.ScheduleSleep();
                SettingsStore.Save();
                if (Perf.Game && SettingsStore.Current.Game.TrimMemory) Dispatcher.BeginInvoke(Perf.Trim, DispatcherPriority.ApplicationIdle);
            }
            else UpdateBar();
        };
        Closing += (_, e) => { e.Cancel = true; Hide(); }; // плеер и остров продолжают работать

        Navigate(SettingsStore.Current.LastPage);
        if (current == "") Navigate("vk:my");
        UpdateBar();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------------ навигация

    IEnumerable<RadioButton> NavButtons()
    {
        foreach (var r in Nav.Children.OfType<RadioButton>()) yield return r;
        foreach (var r in ((StackPanel)NavIsland.Parent).Children.OfType<RadioButton>()) yield return r;
    }

    public void Navigate(string key, int tab = 0)
    {
        var btn = NavButtons().FirstOrDefault(b => b.Uid == key);
        if (btn == null) return;
        if (btn.IsChecked == true) ShowPage(key);
        else btn.IsChecked = true;
        if (pages.GetValueOrDefault(key) is IslandSettingsPage isl) isl.SelectTab(tab);
    }

    void FeedbackClick(object sender, RoutedEventArgs e)
    {
        ((System.Windows.Controls.RadioButton)sender).IsChecked = false;
        FeedbackWindow.Open();
    }

    void NavChecked(object sender, RoutedEventArgs e) => ShowPage(((FrameworkElement)sender).Uid);

    void ShowPage(string key)
    {
        if (current == key) return;
        if (LyricsHost.Visibility == Visibility.Visible) CloseLyrics();
        if (pages.GetValueOrDefault(current) is WebPage old) old.Deactivate();
        current = key;
        SettingsStore.Current.LastPage = key;
        if (!pages.TryGetValue(key, out var page)) pages[key] = page = CreatePage(key);
        PageHost.Content = page;
        PageTitle.Text = key switch
        {
            "vk:my" => L.T("Моя музыка"), "vk:recs" => L.T("Рекомендации"), "vk:playlists" => L.T("Плейлисты"), "vk:search" => L.T("Поиск"),
            "vk:queue" => L.T("Очередь"), "vk:history" => L.T("История"), "local" => L.T("На компьютере"),
            "web:youtube" => "YouTube", "web:soundcloud" => "SoundCloud", "web:yandex" => L.T("Яндекс Музыка"),
            "remote" => L.T("Пульт"), "island" => L.T("Остров"), "settings" => L.T("Настройки"), _ => "",
        };
        if (page is WebPage w) w.Activate();
        if (page is VkPage v) v.OnShown();
        if (page is RemotePage r) r.OnShown();
        AppHost.Web.ScheduleSleep();
    }

    static FrameworkElement CreatePage(string key) => key switch
    {
        "vk:my" => new VkPage(VkMode.My),
        "vk:recs" => new VkPage(VkMode.Recs),
        "vk:playlists" => new VkPage(VkMode.Playlists),
        "vk:search" => new VkPage(VkMode.Search),
        "vk:queue" => new VkPage(VkMode.Queue),
        "vk:history" => new VkPage(VkMode.History),
        "local" => new VkPage(VkMode.Local),
        "remote" => new RemotePage(),
        "island" => new IslandSettingsPage(),
        "settings" => new AppSettingsPage(),
        _ when key.StartsWith("web:") => new WebPage(AppHost.Web.Get(key[4..])!),
        _ => new Grid(),
    };

    public void RefreshWeb()
    {
        if (pages.GetValueOrDefault(current) is WebPage w) w.Place();
    }

    // ------------------------------------------------------------------ плеер внизу

    void UpdateBar()
    {
        var m = AppHost.Media.State;
        var p = AppHost.Player;
        bool ours = p.Current != null && (!m.Has || m.Title == p.Current.Title);

        if (m.Has)
        {
            BarTitle.Text = m.Title;
            BarArtist.Text = m.Artist;
            Art.ImageSource = m.Art ?? (ours ? Cover(p.Current!) : null);
            BarSource.Text = ours ? L.T("VK Музыка") : SourceName(m.App);
        }
        else if (p.Current != null)
        {
            BarTitle.Text = p.Current.Title;
            BarArtist.Text = p.Current.Artist;
            Art.ImageSource = Cover(p.Current);
            BarSource.Text = L.T("VK Музыка");
        }
        else
        {
            BarTitle.Text = L.T("Ничего не играет");
            BarArtist.Text = L.T("Выберите трек или включите музыку где угодно");
            Art.ImageSource = null;
            BarSource.Text = "";
        }

        bool playing = m.Has ? m.Playing : p.IsPlaying;
        PlayBtn.Content = playing ? "" : "";
        ShuffleBtn.Foreground = p.Shuffle ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg2");
        RepeatBtn.Foreground = p.RepeatOne ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg2");

        var a = m.Has && m.Art != null ? Palette.Accent(m.Accent) : Color.FromRgb(255, 194, 74);
        if (a != accent)
        {
            accent = a;
            Resources["Accent"] = new SolidColorBrush(a);
        }
        UpdatePip();
        UpdateProgress();
    }

    static string SourceName(string app) => app switch
    {
        "" => "",
        "Taskbeat" => AppHost.Web.All.FirstOrDefault(s => s.PlayingAudio)?.Title ?? "Taskbeat",
        _ => app,
    };

    static ImageSource? Cover(Track t)
    {
        var u = t.CoverBig ?? t.Cover;
        if (u == null) return null;
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(u);
        b.DecodePixelWidth = 120;
        b.EndInit();
        return b;
    }

    void UpdateProgress()
    {
        var m = AppHost.Media.State;
        double pos = m.Has ? m.Position : AppHost.Player.Position;
        double dur = m.Has ? m.Dur : AppHost.Player.Duration;
        if (dur <= 0 || double.IsNaN(dur)) { Progress.IsEnabled = false; Progress.Value = 0; PosText.Text = DurText.Text = ""; return; }
        Progress.IsEnabled = true;
        Progress.Maximum = dur;
        if (!seeking) Progress.Value = pos;
        PosText.Text = Fmt(seeking ? Progress.Value : pos);
        DurText.Text = Fmt(dur);
    }

    static string Fmt(double s)
    {
        int x = Math.Max(0, (int)s);
        return x >= 3600 ? $"{x / 3600}:{x / 60 % 60:00}:{x % 60:00}" : $"{x / 60}:{x % 60:00}";
    }

    void UpdatePip() => PipBtn.Visibility = AppHost.Web.CanPip || AppHost.Web.InPip ? Visibility.Visible : Visibility.Collapsed;

    void ProgressDown(object s, System.Windows.Input.MouseButtonEventArgs e) => seeking = true;

    void ProgressUp(object s, System.Windows.Input.MouseButtonEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            seeking = false;
            if (AppHost.Media.State.Has) AppHost.Media.Seek(Progress.Value);
            else AppHost.Player.Seek(Progress.Value);
        }, DispatcherPriority.Input);
    }

    void PlayClick(object s, RoutedEventArgs e)
    {
        if (AppHost.Media.State.Has) AppHost.Media.Command("toggle"); else AppHost.Player.Toggle();
    }

    void NextClick(object s, RoutedEventArgs e)
    {
        if (AppHost.Media.State.Has) AppHost.Media.Command("next"); else AppHost.Player.Next();
    }

    void PrevClick(object s, RoutedEventArgs e)
    {
        if (AppHost.Media.State.Has) AppHost.Media.Command("prev"); else AppHost.Player.Previous();
    }

    void ShuffleClick(object s, RoutedEventArgs e) => AppHost.Player.Shuffle = !AppHost.Player.Shuffle;
    void RepeatClick(object s, RoutedEventArgs e) => AppHost.Player.RepeatOne = !AppHost.Player.RepeatOne;
    void IslandClick(object s, RoutedEventArgs e) => Navigate("island");

    void PipClick(object s, RoutedEventArgs e)
    {
        if (AppHost.Web.InPip) AppHost.Web.ExitPip(true);
        else AppHost.Web.EnterPip();
    }

    void VolumeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsInitialized) return;
        AppHost.Player.Volume = e.NewValue;
        VolIcon.Text = e.NewValue switch { 0 => "", < 0.34 => "", < 0.67 => "", _ => "" };
        saveLater.Stop();
        saveLater.Start();
    }

    // ------------------------------------------------------------------ трек: меню, текст, настройки

    /// <summary>Трек VK, который сейчас играет у нас (а не во внешнем приложении).</summary>
    Track? OurTrack()
    {
        var p = AppHost.Player.Current;
        var m = AppHost.Media.State;
        return p != null && (!m.Has || m.Title == p.Title) ? p : null;
    }

    void MoreClick(object s, RoutedEventArgs e)
    {
        if (OurTrack() is not { } t) { ShowToast(L.T("Меню доступно для треков VK в Taskbeat")); return; }
        var m = TrackMenu.Build(t);
        m.PlacementTarget = MoreBtn;
        m.Placement = PlacementMode.Top;
        m.IsOpen = true;
    }

    void LyricsClick(object s, RoutedEventArgs e)
    {
        if (LyricsHost.Visibility == Visibility.Visible) { CloseLyrics(); return; }
        ShowLyrics(OurTrack());
    }

    LyricsView? lyrics;

    /// <summary>Текст песни поверх страницы. t == null — для того, что играет во внешнем приложении.</summary>
    public void ShowLyrics(Track? t)
    {
        ShowAndActivate();
        if (pages.GetValueOrDefault(current) is WebPage w) w.Deactivate(); // окно WebView2 перекрыло бы панель
        lyrics ??= new LyricsView(CloseLyrics);
        LyricsHost.Content = lyrics;
        LyricsHost.Visibility = Visibility.Visible;
        LyricsBtn.Foreground = (Brush)FindResource("Accent");
        lyrics.Show(t);
    }

    void CloseLyrics()
    {
        LyricsHost.Visibility = Visibility.Collapsed;
        lyrics?.Hide();
        LyricsBtn.ClearValue(ForegroundProperty);
        if (pages.GetValueOrDefault(current) is WebPage w) w.Activate();
    }

    public void OpenSearch(string q)
    {
        CloseLyrics();
        Navigate("vk:search");
        if (pages.GetValueOrDefault("vk:search") is VkPage p) p.Search(q);
        ShowAndActivate();
    }

    public void OpenPlaylist(VkPlaylist pl)
    {
        CloseLyrics();
        Navigate("vk:playlists");
        if (pages.GetValueOrDefault("vk:playlists") is VkPage p) p.OpenPlaylist(pl);
        ShowAndActivate();
    }

    public void ShowTracks(string title, List<Track> list)
    {
        CloseLyrics();
        Navigate("vk:recs");
        if (pages.GetValueOrDefault("vk:recs") is VkPage p) p.ShowList(title, list);
    }

    // настройки воспроизведения

    bool settingsSync;

    void PlaySettingsClick(object s, RoutedEventArgs e)
    {
        settingsSync = true;
        int cf = AppHost.Player.Crossfade;
        CrossfadeOn.IsChecked = cf > 0;
        CrossfadeLen.Value = cf > 0 ? cf : 6;
        CrossfadeLen.IsEnabled = cf > 0;
        UpdateCrossfadeText();
        UpdateSleepText();
        var st = SettingsStore.Current;
        EqText.Text = !st.EqEnabled ? L.T("Выключен")
            : EqWindow.Presets.FirstOrDefault(p => p.key == st.EqPreset).name is { } n ? $"Включён · {n}" : L.T("Включён · свой");
        settingsSync = false;
        PlaySettings.IsOpen = !PlaySettings.IsOpen;
    }

    void CrossfadeToggle(object s, RoutedEventArgs e)
    {
        AppHost.Player.Crossfade = CrossfadeOn.IsChecked == true ? (int)CrossfadeLen.Value : 0;
        CrossfadeLen.IsEnabled = CrossfadeOn.IsChecked == true;
        UpdateCrossfadeText();
    }

    void CrossfadeLenChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (settingsSync || !IsInitialized || CrossfadeOn.IsChecked != true) return;
        AppHost.Player.Crossfade = (int)e.NewValue;
        UpdateCrossfadeText();
    }

    void UpdateCrossfadeText() => CrossfadeText.Text = AppHost.Player.Crossfade > 0
        ? $"Треки перетекают друг в друга за {AppHost.Player.Crossfade} с"
        : L.T("Сглаживает переключение музыки");

    EqWindow? eqWindow;

    void EqClick(object s, RoutedEventArgs e)
    {
        PlaySettings.IsOpen = false;
        if (eqWindow == null)
        {
            eqWindow = new EqWindow { Owner = this };
            eqWindow.Closed += (_, _) => eqWindow = null;
            eqWindow.Show();
        }
        else eqWindow.Activate();
    }

    void SleepClick(object s, RoutedEventArgs e)
    {
        int v = int.Parse((string)((Button)s).Tag);
        if (v < 0) AppHost.Player.SetSleepAfterTrack();
        else AppHost.Player.SetSleep(v);
        UpdateSleepText();
    }

    void UpdateSleepText()
    {
        var p = AppHost.Player;
        SleepText.Text = p.SleepAfterTrack ? L.T("Остановится в конце трека")
            : p.SleepAt is { } at ? $"Остановится в {at:HH:mm}"
            : L.T("Выключен");
        PlaySettingsBtn.Foreground = p.SleepAfterTrack || p.SleepAt != null || p.Crossfade > 0
            ? (Brush)FindResource("Accent") : (Brush)FindResource("Fg2");
    }

    public void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    // ------------------------------------------------------------------ окно

    void MinClick(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaxClick(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void CloseClick(object s, RoutedEventArgs e) => Hide();

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
