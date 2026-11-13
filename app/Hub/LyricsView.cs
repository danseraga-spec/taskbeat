using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CoolPlayer;

/// <summary>
/// Текст песни с синхронизацией, как в VK X / Apple Music: текущая строка подсвечена,
/// плавно увеличивается и держится чуть выше центра; соседние строки приглушены.
/// Прокрутка — анимация трансформации на GPU (60+ к/с), а не дёрганье ScrollViewer.
/// В игровом режиме всё то же, но без анимаций, размытого фона и с редким опросом позиции.
/// Работает и для музыки из других приложений (Spotify, Яндекс…) — тогда текст из LrcLib.
/// </summary>
sealed class LyricsView : UserControl
{
    readonly Action close;
    readonly TextBlock title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock artist = new() { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0), Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)) };
    readonly ImageBrush art = new() { Stretch = Stretch.UniformToFill };
    readonly Image backdrop = new() { Stretch = Stretch.UniformToFill, Opacity = 0.55, IsHitTestVisible = false };
    readonly StackPanel lines = new();
    readonly TranslateTransform shift = new();
    readonly Grid viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    readonly TextBlock status = new() { FontSize = 15, Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 255, 255, 255)), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    readonly TextBlock sourceInfo = new() { FontSize = 11.5, Foreground = new SolidColorBrush(Color.FromArgb(0x80, 255, 255, 255)) };
    readonly Dictionary<string, RadioButton> sources = [];
    readonly DispatcherTimer timer = new(DispatcherPriority.Normal);

    string key = "";
    Track? track;
    Lyrics? data;
    int active = -2;
    int loadVersion;
    bool userScrolled;
    DateTime userScrollAt;

    static readonly IEasingFunction Ease = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly IEasingFunction Soft = new SineEase { EasingMode = EasingMode.EaseInOut };

    public LyricsView(Action close)
    {
        this.close = close;
        Background = new SolidColorBrush(Color.FromRgb(6, 6, 8));
        Focusable = true;

        // фон: огромная размытая обложка, кэш на GPU — рисуется один раз
        backdrop.Effect = new BlurEffect { Radius = 90, RenderingBias = RenderingBias.Performance, KernelType = KernelType.Gaussian };
        backdrop.CacheMode = new BitmapCache(0.5);
        backdrop.RenderTransform = new ScaleTransform(1.4, 1.4);
        backdrop.RenderTransformOrigin = new Point(0.5, 0.5);
        var shade = new Border
        {
            IsHitTestVisible = false,
            Background = new LinearGradientBrush(Color.FromArgb(0xB8, 0, 0, 0), Color.FromArgb(0xE6, 0, 0, 0), 90),
        };

        // шапка
        var cover = new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(10), Background = art };
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(title);
        names.Children.Add(artist);
        sourceInfo.Margin = new Thickness(0, 3, 0, 0);
        names.Children.Add(sourceInfo);
        var pills = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (k, text) in new[] { ("auto", "Авто"), ("vk", "VK"), ("lrclib", "LrcLib") })
        {
            var r = new RadioButton { Content = text, GroupName = "lyrsrc", Margin = new Thickness(0, 0, 6, 0) };
            r.SetResourceReference(StyleProperty, "PillTab");
            r.Checked += (_, _) =>
            {
                if (SettingsStore.Current.LyricsSource == k) return;
                SettingsStore.Current.LyricsSource = k;
                SettingsStore.Save();
                key = "";
                Reload();
            };
            sources[k] = r;
            pills.Children.Add(r);
        }
        var closeBtn = new Button { Content = "", ToolTip = "Закрыть текст (Esc)", Margin = new Thickness(10, 0, 0, 0) };
        closeBtn.SetResourceReference(StyleProperty, "Icon");
        closeBtn.Click += (_, _) => close();
        pills.Children.Add(closeBtn);

        var head = new DockPanel { Margin = new Thickness(32, 10, 26, 0) };
        DockPanel.SetDock(pills, Dock.Right);
        head.Children.Add(pills);
        head.Children.Add(cover);
        head.Children.Add(names);

        lines.RenderTransform = shift;
        lines.VerticalAlignment = VerticalAlignment.Top;
        viewport.Children.Add(lines);
        viewport.Children.Add(status);
        // мягкое затухание текста сверху и снизу
        var fade = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.14));
        fade.GradientStops.Add(new GradientStop(Colors.Black, 0.82));
        fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        viewport.OpacityMask = fade;
        viewport.Margin = new Thickness(0, 10, 0, 0);
        viewport.MouseWheel += (_, e) =>
        {
            userScrolled = true;
            userScrollAt = DateTime.Now;
            double y = shift.Y + e.Delta * 0.6;
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = y;
            e.Handled = true;
        };
        viewport.SizeChanged += (_, _) => { active = -2; Sync(); };

        var content = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        content.Children.Add(head);
        content.Children.Add(viewport);

        var root = new Grid();
        root.Children.Add(backdrop);
        root.Children.Add(shade);
        root.Children.Add(content);
        Content = root;

        timer.Tick += (_, _) => Sync();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) close(); };
        AppHost.Player.Changed += () => { if (IsVisible) Reload(); };
        AppHost.Media.Changed += _ => { if (IsVisible) Reload(); };
        Perf.Changed += () => { ApplyMode(); key = ""; if (IsVisible) Reload(); };
        ApplyMode();
    }

    void ApplyMode()
    {
        bool game = Perf.NoBackdrop;
        backdrop.Visibility = game ? Visibility.Collapsed : Visibility.Visible;
        timer.Interval = TimeSpan.FromMilliseconds(Perf.NoLyricsAnim ? 250 : 50);
    }

    public void Show(Track? t)
    {
        track = t;
        (sources.GetValueOrDefault(SettingsStore.Current.LyricsSource) ?? sources["auto"]).IsChecked = true;
        key = "";
        Reload();
        timer.Start();
        Focus();
    }

    public void Hide() => timer.Stop();

    // ------------------------------------------------------------------ загрузка

    (string artist, string title, double dur, Track? vk) NowPlaying()
    {
        var p = AppHost.Player.Current;
        var m = AppHost.Media.State;
        bool ours = p != null && (!m.Has || m.Title == p.Title);
        if (ours) return (p!.Artist, p.Title, p.Duration > 0 ? p.Duration : AppHost.Player.Duration, p);
        if (m.Has) return (m.Artist, m.Title, m.Dur, null);
        if (track != null) return (track.Artist, track.Title, track.Duration, track);
        return ("", "", 0, null);
    }

    async void Reload()
    {
        var (a, t, dur, vk) = NowPlaying();
        string k = $"{SettingsStore.Current.LyricsSource}|{a}|{t}";
        if (k == key) return;
        key = k;
        int ver = ++loadVersion;

        title.Text = t.Length > 0 ? t : "Ничего не играет";
        artist.Text = a;
        var img = Cover(vk, 112) ?? AppHost.Media.State.Art;
        art.ImageSource = img;
        backdrop.Source = Perf.NoBackdrop ? null : Cover(vk, 300) ?? AppHost.Media.State.Art;
        lines.Children.Clear();
        data = null;
        active = -2;
        sourceInfo.Text = "";
        shift.BeginAnimation(TranslateTransform.YProperty, null);
        shift.Y = 0;
        if (t.Length == 0) { status.Text = "Включите трек — здесь появится текст"; return; }
        status.Text = "Ищу текст…";

        var src = SettingsStore.Current.LyricsSource;
        if (vk == null && src == "vk") { status.Text = "Текст из VK есть только для треков VK. Выберите «Авто» или LrcLib."; return; }
        var l = await LyricsService.GetAsync(src, vk, a, t, dur);
        if (ver != loadVersion) return;
        if (l == null || l.Lines.Count == 0) { status.Text = "Текст для этого трека не найден"; return; }
        data = l;
        status.Text = "";
        sourceInfo.Text = l.Synced ? $"Синхронизированный текст · {l.Source}" : $"Текст без синхронизации · {l.Source}";
        Build();
    }

    static ImageSource? Cover(Track? t, int px)
    {
        var u = t?.CoverBig ?? t?.Cover;
        if (u == null) return null;
        var b = new BitmapImage();
        b.BeginInit();
        b.UriSource = new Uri(u);
        b.DecodePixelWidth = px;
        b.EndInit();
        return b;
    }

    void Build()
    {
        lines.Children.Clear();
        if (data == null) return;
        bool synced = data.Synced;
        foreach (var l in data.Lines)
        {
            bool empty = string.IsNullOrWhiteSpace(l.Text);
            var tb = new TextBlock
            {
                Text = empty ? (synced ? "• • •" : " ") : l.Text,
                FontSize = synced ? 32 : 22,
                FontWeight = synced ? FontWeights.Bold : FontWeights.SemiBold,
                FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                Opacity = synced ? 0.32 : 0.92,
                Margin = new Thickness(48, synced ? 9 : 3, 64, synced ? 9 : 3),
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            if (synced && l.Time >= 0)
            {
                double at = l.Time;
                tb.Cursor = Cursors.Hand;
                tb.MouseEnter += (_, _) => { if (lines.Children.IndexOf(tb) != active) tb.Opacity = Math.Max(tb.Opacity, 0.6); };
                tb.MouseLeave += (_, _) => StyleLine(tb, lines.Children.IndexOf(tb), animate: false);
                tb.MouseLeftButtonUp += (_, _) => { SeekTo(at); userScrolled = false; active = -2; };
            }
            lines.Children.Add(tb);
        }
        if (!synced)
        {
            // обычный текст: просто прокрутка колесом
            lines.Margin = new Thickness(0, 16, 0, 60);
        }
        else lines.Margin = new Thickness(0);
        Dispatcher.BeginInvoke(() => { active = -2; Sync(); }, DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ синхронизация

    static void SeekTo(double sec)
    {
        var p = AppHost.Player.Current;
        var m = AppHost.Media.State;
        if (p != null && (!m.Has || m.Title == p.Title)) AppHost.Player.Seek(sec);
        else AppHost.Media.Seek(sec);
    }

    double CurrentPosition()
    {
        var p = AppHost.Player.Current;
        var m = AppHost.Media.State;
        return p != null && (!m.Has || m.Title == p.Title) ? AppHost.Player.Position : m.Position;
    }

    void Sync()
    {
        if (data is not { Synced: true } || lines.Children.Count == 0) return;
        double pos = CurrentPosition() + 0.3; // строка загорается чуть раньше — так естественнее
        int idx = -1;
        for (int i = 0; i < data.Lines.Count; i++)
        {
            if (data.Lines[i].Time <= pos) idx = i; else break;
        }
        if (userScrolled && (DateTime.Now - userScrollAt).TotalSeconds > 4) { userScrolled = false; active = -2; }
        if (idx == active) return;
        bool first = active == -2;
        active = idx;
        for (int i = 0; i < lines.Children.Count; i++) StyleLine((TextBlock)lines.Children[i], i, animate: !first);
        if (!userScrolled) ScrollTo(idx, animate: !first);
    }

    void StyleLine(TextBlock tb, int i, bool animate)
    {
        bool cur = i == active;
        int dist = Math.Abs(i - Math.Max(active, 0));
        double op = cur ? 1 : i < active ? 0.42 : dist <= 2 ? 0.42 : 0.26;
        double sc = cur ? 1.06 : 1;
        var st = (ScaleTransform)tb.RenderTransform;
        if (animate && !Perf.NoLyricsAnim)
        {
            var d = TimeSpan.FromMilliseconds(380);
            tb.BeginAnimation(OpacityProperty, new DoubleAnimation(op, d) { EasingFunction = Soft });
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(sc, d) { EasingFunction = Ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(sc, d) { EasingFunction = Ease });
        }
        else
        {
            tb.BeginAnimation(OpacityProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            tb.Opacity = op;
            st.ScaleX = st.ScaleY = sc;
        }
        // мягкое свечение у текущей строки (только в классическом режиме)
        tb.Effect = cur && !Perf.NoLyricsAnim ? new DropShadowEffect { Color = Colors.White, BlurRadius = 22, ShadowDepth = 0, Opacity = 0.28 } : null;
    }

    void ScrollTo(int idx, bool animate)
    {
        double y;
        if (idx < 0) y = viewport.ActualHeight * 0.38;
        else
        {
            var el = (FrameworkElement)lines.Children[idx];
            var p = el.TranslatePoint(new Point(0, 0), lines);
            y = viewport.ActualHeight * 0.38 - (p.Y + el.ActualHeight / 2);
        }
        if (animate && !Perf.NoLyricsAnim)
        {
            shift.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(y, TimeSpan.FromMilliseconds(620)) { EasingFunction = Ease }, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            shift.BeginAnimation(TranslateTransform.YProperty, null);
            shift.Y = y;
        }
    }
}
