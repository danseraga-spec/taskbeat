using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace CoolPlayer;

/// <summary>
/// Меню по правому клику на острове и значку в трее: тёмная карточка в стиле острова
/// с плавным появлением, переключателями и встроенными таймерами.
/// </summary>
sealed class IslandMenu : Window
{
    static IslandMenu? open;

    static readonly Brush Bg = Frozen(Color.FromArgb(246, 18, 18, 21));
    static readonly Brush Hover = Frozen(Color.FromArgb(255, 36, 36, 42));
    static readonly Brush Fg = Frozen(Color.FromRgb(240, 240, 244));
    static readonly Brush Fg2 = Frozen(Color.FromRgb(150, 150, 158));
    static readonly Brush Line = Frozen(Color.FromArgb(255, 40, 40, 46));
    static readonly Brush Danger = Frozen(Color.FromRgb(255, 105, 97));
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly StackPanel list = new();
    readonly StackPanel timerPanel = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(10, 2, 10, 6) };
    readonly Border card;
    bool closing;

    public static void Open(IslandWindow host)
    {
        open?.Close();
        open = new IslandMenu(host);
        open.Show();
        open.Activate();
    }

    IslandMenu(IslandWindow host)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        Left = -10000; Top = -10000; // реальное место — после измерения, в пикселях монитора
        Opacity = 0;

        card = new Border
        {
            Background = Bg, CornerRadius = new CornerRadius(18), BorderBrush = Line, BorderThickness = new Thickness(1),
            Padding = new Thickness(6), Margin = new Thickness(18), Width = 312,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.55, Color = Colors.Black, Direction = 270 },
            Child = list,
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new ScaleTransform(0.94, 0.94),
        };
        Content = card;

        // шапка: логотип, название, что играет
        var head = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        var logo = new Image { Width = 28, Height = 28, Margin = new Thickness(0, 0, 10, 0) };
        logo.SetResourceReference(Image.SourceProperty, "Logo");
        head.Children.Add(logo);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "Taskbeat", FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Fg });
        var m = AppHost.Media.State;
        titles.Children.Add(new TextBlock
        {
            Text = m.Has ? (m.Artist.Length > 0 ? $"{m.Title} — {m.Artist}" : m.Title) : $"{L.T("Версия")} {Updater.Current.ToString(3)}",
            FontSize = 11.5, Foreground = Fg2, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 236,
        });
        head.Children.Add(titles);
        list.Children.Add(head);
        Sep();

        Item("", L.T("Открыть Taskbeat"), () => AppHost.ShowHub());
        if (AppHost.Web.CanPip) Item("", L.T("Видео в картинку-в-картинке"), AppHost.Web.EnterPip);
        Sep();

        // таймеры раскрываются прямо в меню
        string timerText = Live.TimerRunning ? L.T("Таймер идёт") : Live.StopwatchRunning ? L.T("Секундомер идёт") : L.T("Таймер");
        var timerRow = Item("", timerText, null, chevron: true);
        timerRow.MouseLeftButtonUp += (_, _) =>
        {
            timerPanel.Visibility = timerPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Dispatcher.BeginInvoke(Place, System.Windows.Threading.DispatcherPriority.Loaded);
        };
        BuildTimers();
        list.Children.Add(timerPanel);
        Item("", L.T("Новое напоминание…"), () => host.OpenSettings(2));
        Item("", L.T("Настройки острова…"), () => host.OpenSettings());
        Item("", L.T("Отзыв или баг…"), FeedbackWindow.Open);
        Sep();
        Toggle(L.T("Уходить в полоску в покое"), SettingsStore.Current.CollapseToStrip, on =>
        {
            SettingsStore.Current.CollapseToStrip = on;
            SettingsStore.Touch();
        });
        Toggle(L.T("Запускать вместе с Windows"), Autostart.Enabled, on => Autostart.Enabled = on);
        Sep();
        Item("", L.T("Выход"), AppHost.Exit, danger: true);

        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        host.SetMenuOpen(true);
        Closed += (_, _) => { if (open == this) open = null; host.SetMenuOpen(false); };
        Loaded += (_, _) =>
        {
            Place();
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            var st = (ScaleTransform)card.RenderTransform;
            st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        };
    }

    void BuildTimers()
    {
        timerPanel.Children.Clear();
        if (Live.TimerRunning)
        {
            timerPanel.Children.Add(Chip(L.T("Остановить таймер"), Live.StopTimer, wide: true));
        }
        else
        {
            var row = new WrapPanel();
            foreach (int min in new[] { 1, 5, 10, 15, 25, 30, 45, 60 })
                row.Children.Add(Chip(L.F("{0} мин", min), () => Live.StartTimer(TimeSpan.FromMinutes(min))));
            timerPanel.Children.Add(row);
            timerPanel.Children.Add(Chip(L.T("Помодоро 25 + 5"), Live.StartPomodoro, wide: true));
        }
        timerPanel.Children.Add(Chip(Live.StopwatchRunning ? L.T("Остановить секундомер") : L.T("Секундомер"), Live.ToggleStopwatch, wide: true));
    }

    // ------------------------------------------------------------------ элементы

    Border Item(string glyph, string text, Action? click, bool danger = false, bool chevron = false)
    {
        var row = new DockPanel();
        row.Children.Add(new TextBlock { Text = glyph, FontFamily = Icons, FontSize = 15, Width = 30, Foreground = danger ? Danger : Fg2, VerticalAlignment = VerticalAlignment.Center });
        if (chevron)
        {
            var ch = new TextBlock { Text = "", FontFamily = Icons, FontSize = 11, Foreground = Fg2, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(ch, Dock.Right);
            row.Children.Insert(0, ch);
        }
        row.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = danger ? Danger : Fg, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = Row(row);
        if (click != null) b.MouseLeftButtonUp += (_, _) => { Dismiss(); click(); };
        list.Children.Add(b);
        return b;
    }

    void Toggle(string text, bool value, Action<bool> set)
    {
        var knob = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(3, 0, 3, 0) };
        var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Child = knob, VerticalAlignment = VerticalAlignment.Center };
        void Paint(bool on)
        {
            track.Background = on ? Frozen(Themes.Accent) : Frozen(Color.FromRgb(62, 62, 70));
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Background = on ? Frozen(Color.FromRgb(20, 20, 22)) : Brushes.White;
        }
        Paint(value);
        var row = new DockPanel();
        DockPanel.SetDock(track, Dock.Right);
        row.Children.Add(track);
        row.Children.Add(new TextBlock { Text = text, FontSize = 13.5, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(30, 0, 8, 0), TextWrapping = TextWrapping.Wrap });
        var b = Row(row);
        b.MouseLeftButtonUp += (_, _) => { value = !value; Paint(value); set(value); };
        list.Children.Add(b);
    }

    Border Chip(string text, Action click, bool wide = false)
    {
        var b = new Border
        {
            CornerRadius = new CornerRadius(10), Background = Frozen(Color.FromRgb(32, 32, 38)), Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            HorizontalAlignment = wide ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 12.5, Foreground = Fg, HorizontalAlignment = HorizontalAlignment.Center },
        };
        b.MouseEnter += (_, _) => b.Background = Frozen(Color.FromRgb(48, 48, 56));
        b.MouseLeave += (_, _) => b.Background = Frozen(Color.FromRgb(32, 32, 38));
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Dismiss(); click(); };
        return b;
    }

    static Border Row(UIElement content)
    {
        var b = new Border { CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 8, 10, 8), Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = content };
        b.MouseEnter += (_, _) => b.Background = Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        return b;
    }

    void Sep() => list.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(10, 5, 10, 5) });

    void Dismiss()
    {
        if (closing) return;
        closing = true;
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(110));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }

    // ------------------------------------------------------------------ положение: над курсором, в пределах монитора

    void Place()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        GetCursorPos(out var pt);
        var mon = MonitorFromPoint(pt, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfoW(mon, ref mi);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int w = (int)Math.Ceiling(ActualWidth * dpi), h = (int)Math.Ceiling(ActualHeight * dpi);
        int margin = (int)(18 * dpi); // поле под тень
        var wa = mi.rcWork;
        int x = Math.Clamp(pt.X - w / 2, wa.Left - margin, wa.Right - w + margin);
        int y = pt.Y - h + margin - (int)(6 * dpi);
        if (y < wa.Top - margin) y = pt.Y - margin + (int)(6 * dpi);
        y = Math.Min(y, wa.Bottom - h + margin);
        SetWindowPos(hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE
    }

    static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
