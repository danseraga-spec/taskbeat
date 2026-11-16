using System.Diagnostics;
using System.Globalization;
using System.Media;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoolPlayer;

enum Mode { Strip, Info, Media, Activity, Live, ExpMedia, ExpIdle, Alert }

sealed record ActivityInfo(string Kind, string Left, string Right, Color Color) { public double Value { get; init; } }

/// <summary>Пружина: «живое» движение с лёгким перелётом, как у Dynamic Island.</summary>
sealed class Spring(double v)
{
    public double Value = v, Velocity, Target = v;
    public bool Settled => Math.Abs(Target - Value) < 0.2 && Math.Abs(Velocity) < 0.5;

    public void Step(double dt, double k, double zeta)
    {
        double c = 2 * Math.Sqrt(k) * zeta;
        int n = Math.Max(1, (int)Math.Ceiling(dt / 0.004));
        double h = dt / n;
        for (int i = 0; i < n; i++)
        {
            Velocity += (k * (Target - Value) - c * Velocity) * h;
            Value += Velocity * h;
        }
        if (Settled) { Value = Target; Velocity = 0; }
    }
}

/// <summary>
/// Чёрный остров внутри панели задач. В покое поднимается и превращается в тонкую
/// полоску на верхнем крае панели (или остаётся пилюлей — по настройке); раскрывается
/// вверх, над панелью. Перерисовка только пока что-то движется.
/// </summary>
sealed class IslandView : FrameworkElement
{
    static readonly FontFamily Font = new("Segoe UI Variable Display, Segoe UI");
    static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly CultureInfo Ru = new(global::CoolPlayer.L.En ? "en-US" : "ru-RU");
    static readonly double[] BarPhase = [0.3, 2.1, 4.0, 5.2];

    static readonly Brush HitBrush = Freeze(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)));
    static readonly Brush Black = Brushes.Black;
    static readonly Brush White = Brushes.White;
    static readonly Brush Gray = Freeze(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)));
    static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)));
    static readonly Brush ButtonBg = Freeze(new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)));
    static readonly Pen Outline = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(22, 255, 255, 255))), 1));
    static readonly Pen IconPen = Freeze(new Pen(White, 2.4) { LineJoin = PenLineJoin.Round });
    static readonly Pen BatteryPen = Freeze(new Pen(Gray, 1.2));
    static readonly Color Orange = Color.FromRgb(255, 159, 10);
    static readonly Color Green = Color.FromRgb(52, 199, 89);
    static readonly Color Red = Color.FromRgb(255, 69, 58);
    static readonly Color Blue = Color.FromRgb(10, 132, 255);

    readonly IslandWindow host;
    readonly MediaService media;
    readonly WeatherService weather;
    readonly Scheduler scheduler;
    readonly Spring sw = new(0), sh = new(0), sb = new(0); // ширина, высота, центр от низа окна
    readonly Stopwatch clock = Stopwatch.StartNew();
    readonly DispatcherTimer eqTimer, slowTimer, hoverTimer, leaveTimer, activityTimer;
    readonly Dictionary<string, FormattedText> textCache = [];
    readonly Dictionary<string, Rect> buttons = [];
    readonly Queue<Reminder> alerts = new();
    Rect seekRect = Rect.Empty;
    Rect volRect = Rect.Empty;
    bool draggingVol;

    double W = 1, H = 1, lastFrame, ppd = 1, lastClip = -10;
    public double TaskbarHeight { get; set; } = 48;
    /// <summary>Масштаб острова (2K/4K). Вся геометрия считается в «виртуальных» единицах, рисуется с этим множителем.</summary>
    public double Scale { get; private set; } = 1;
    Point Pos(MouseEventArgs e) { var p = e.GetPosition(this); return new Point(p.X / Scale, p.Y / Scale); }

    public void SetScale(double k)
    {
        if (Math.Abs(k - Scale) < 0.001) return;
        Scale = k;
        textCache.Clear();
        W = ActualWidth / k;
        H = ActualHeight / k;
        if (sized) { var (w, h, b) = TargetFor(CurrentMode()); sw.Value = sw.Target = w; sh.Value = sh.Target = h; sb.Value = sb.Target = b; pending = null; }
        Retarget();
    }
    double TbTop => H - TaskbarHeight;
    bool sized, expanded, suppressed, hidden, renderingHooked;
    ActivityInfo? activity;
    Reminder? alert;
    InfoKind info;
    string infoText = "";
    (bool has, bool ac, int pct) power = Native.Power();
    Color accent = Color.FromRgb(235, 235, 240);
    Brush accentBrush = White;

    MediaState M => media.State;
    static AppSettings S => SettingsStore.Current;

    public IslandView(IslandWindow host, MediaService media, WeatherService weather, Scheduler scheduler)
    {
        this.host = host;
        this.media = media;
        this.weather = weather;
        this.scheduler = scheduler;

        eqTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        eqTimer.Tick += (_, _) => InvalidateVisual();
        slowTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        slowTimer.Tick += (_, _) => InvalidateVisual();
        hoverTimer = new DispatcherTimer();
        hoverTimer.Tick += (_, _) => { hoverTimer.Stop(); if (IsMouseOver) SetExpanded(true); };
        leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        leaveTimer.Tick += (_, _) => { leaveTimer.Stop(); if (!IsMouseOver) SetExpanded(false); };
        activityTimer = new DispatcherTimer();
        activityTimer.Tick += (_, _) => { activityTimer.Stop(); activity = null; Retarget(); };

        media.Changed += OnMedia;
        weather.Changed += Retarget;
        scheduler.MinuteTick += Retarget;
        scheduler.ReminderDue += OnReminder;
        settingsChanged = () => { textCache.Clear(); UpdateAccent(); Retarget(); };
        SettingsStore.Changed += settingsChanged;
        AppHost.Web.PipStateChanged += InvalidateVisual;
        Perf.Changed += Retarget;
        Live.Changed += Retarget;
    }

    readonly Action settingsChanged;

    /// <summary>Отписка от событий — для закрытых окон на дополнительных мониторах.</summary>
    public void Detach()
    {
        media.Changed -= OnMedia;
        weather.Changed -= Retarget;
        scheduler.MinuteTick -= Retarget;
        scheduler.ReminderDue -= OnReminder;
        SettingsStore.Changed -= settingsChanged;
        AppHost.Web.PipStateChanged -= InvalidateVisual;
        Perf.Changed -= Retarget;
        Live.Changed -= Retarget;
        hidden = true;
        eqTimer.Stop(); slowTimer.Stop(); hoverTimer.Stop(); leaveTimer.Stop(); activityTimer.Stop();
        if (renderingHooked) { CompositionTarget.Rendering -= OnFrame; renderingHooked = false; }
    }

    // ------------------------------------------------------------------ состояние

    Mode CompactMode(bool ignoreYield = false)
    {
        // второй монитор в обычном режиме — только полоска; пока остров на другом мониторе — и её нет
        if ((yielded && !ignoreYield) || !host.IsMain) return Mode.Strip;
        if (activity != null) return Mode.Activity;
        if (Live.Current != null) return Mode.Live;
        if (M.Has && M.Playing) return Mode.Media;
        var rule = Scheduler.ActiveRule();
        if (rule != null)
        {
            info = rule.Content;
            infoText = rule.CustomText;
            return rule.Content == InfoKind.Nothing ? Mode.Strip : Mode.Info;
        }
        if (!S.CollapseToStrip)
        {
            info = S.IdleContent;
            infoText = "";
            return Mode.Info;
        }
        return Mode.Strip;
    }

    Mode CurrentMode()
    {
        if (alert != null) return Mode.Alert;
        if (expanded) return M.Has && M.Playing ? Mode.ExpMedia : Mode.ExpIdle;
        return CompactMode();
    }

    /// <summary>(ширина, высота, расстояние от низа окна до центра)</summary>
    (double w, double h, double b) TargetFor(Mode m)
    {
        double mid = TaskbarHeight / 2;
        return m switch
        {
            Mode.Media => Live.PillLyrics.Line != null ? (400, 32, mid) : (200, 32, mid),
            Mode.Activity => activity?.Kind switch { "track" => (360, 32, mid), "notif" => (400, 46, mid), "volume" => (300, 32, mid), _ => (286, 32, mid) },
            Mode.Live => (300, 32, mid),
            Mode.Info => (InfoWidth(), 32, mid),
            Mode.ExpMedia => (424, 190, 6 + 95),
            Mode.ExpIdle => M.Has ? (384, 164, 6 + 82) : (384, 118, 6 + 59),
            Mode.Alert => (384, 128, 6 + 64),
            _ => (yielded ? 0 : StripWidth, StripH, TaskbarHeight - StripH / 2),
        };
    }

    bool yielded, isBig;
    /// <summary>Здесь виден остров, а не полоска — на других мониторах полоски нет.</summary>
    public bool IsBig => isBig && !hidden;
    /// <summary>Остров раскрыт здесь (карточка или напоминание).</summary>
    public bool Expanded => !hidden && (expanded || alert != null);
    /// <summary>Здесь хотят показать остров (без учёта того, что полоску попросили спрятать).</summary>
    public bool WantsIsland => !hidden && (expanded || alert != null || CompactMode(ignoreYield: true) != Mode.Strip);

    public void SetYield(bool y)
    {
        if (yielded == y) return;
        yielded = y;
        Retarget();
    }

    /// <summary>Сменилась роль окна (второй монитор стал главным на время полноэкранного режима на основном).</summary>
    public void RoleChanged()
    {
        if (!host.IsMain) activity = null;
        Retarget();
    }

    static double StripH => Math.Clamp(S.StripThickness, 3, 8); // толщина полоски в покое
    double StripWidth => S.StripFullWidth ? W + 2 * Math.Abs(S.OffsetX) + 10 : 200;

    Mode lastMode = Mode.Strip;

    // Переход полоска ↔ остров идёт в две фазы:
    //   из полоски: полоска стягивается к центру до ширины острова, затем «наливается» вниз
    //   и вспыхивает мягким контуром цвета обложки;
    //   в полоску: остров сплющивается, поднимается к краю панели и растекается в стороны.
    (double w, double h, double b)? pending; // вторая фаза
    bool pendingByWidth;                     // ждём ширину (из полоски) или высоту (в полоску)
    double pendingSince;
    double glowStart = -10;
    const double GlowLen = 0.75;

    void Retarget()
    {
        var mode = CurrentMode();
        bool big = mode != Mode.Strip; // любой остров (пилюля, уведомление, карточка) — другие мониторы прячут полоску
        if (big != isBig) { isBig = big; host.ReportBig(); }
        bool fromStrip = lastMode == Mode.Strip && mode != Mode.Strip;
        bool toStrip = lastMode != Mode.Strip && mode == Mode.Strip;
        lastMode = mode;
        var (w, h, b) = TargetFor(mode);
        if (h > 40) host.SetTall(true);
        pending = null;

        if (Perf.NoIslandAnim)
        {
            // игровой режим: без анимации — сразу в конечное состояние, ни одного лишнего кадра
            sw.Value = sw.Target = w; sh.Value = sh.Target = h; sb.Value = sb.Target = b;
            sw.Velocity = sh.Velocity = sb.Velocity = 0;
        }
        else if (fromStrip && sh.Value < StripH + 2)
        {
            sw.Target = w;
            sh.Target = StripH;
            sb.Target = TaskbarHeight - StripH / 2;
            pending = (w, h, b);
            pendingByWidth = true;
            pendingSince = clock.Elapsed.TotalSeconds;
        }
        else if (toStrip && sh.Value > StripH + 2)
        {
            sw.Target = Math.Min(sw.Value, 220);
            sh.Target = StripH;
            sb.Target = b;
            pending = (w, h, b);
            pendingByWidth = false;
            pendingSince = clock.Elapsed.TotalSeconds;
        }
        else
        {
            sw.Target = w;
            sh.Target = h;
            sb.Target = b;
        }
        UpdateTicking();
        InvalidateVisual();
    }

    /// <summary>Первая фаза почти закончилась — запускаем вторую.</summary>
    void AdvancePhase()
    {
        if (pending is not { } p) return;
        bool ready = pendingByWidth
            ? Math.Abs(sw.Value - sw.Target) < Math.Max(14, sw.Target * 0.1)
            : Math.Abs(sh.Value - sh.Target) < 1.5 && Math.Abs(sb.Value - sb.Target) < 2;
        ready |= clock.Elapsed.TotalSeconds - pendingSince > 0.6; // страховка: фаза не должна зависнуть
        if (!ready) return;
        pending = null;
        sw.Target = p.w;
        sh.Target = p.h;
        sb.Target = p.b;
        if (pendingByWidth) glowStart = clock.Elapsed.TotalSeconds + 0.12;
    }

    bool Glowing => !Perf.NoIslandAnim && clock.Elapsed.TotalSeconds - glowStart < GlowLen;

    bool Animating => !(sw.Settled && sh.Settled && sb.Settled) || pending != null || Glowing;

    void UpdateTicking()
    {
        bool anim = !hidden && Animating;
        if (anim && !renderingHooked)
        {
            lastFrame = clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
            renderingHooked = true;
        }
        else if (!anim && renderingHooked)
        {
            CompositionTarget.Rendering -= OnFrame;
            renderingHooked = false;
        }
        if (!anim && sh.Value < 40) host.SetTall(false);

        var m = CurrentMode();
        bool eq = !hidden && !anim && M.Playing &&
                  (m is Mode.Media or Mode.ExpMedia || (m == Mode.Activity && activity?.Kind == "track"));
        eqTimer.IsEnabled = eq && !Perf.NoIslandEq; // в игровом режиме эквалайзер замирает — 0 перерисовок
        // секундный тик нужен только для прогресса трека на виду
        slowTimer.IsEnabled = !hidden && !anim && !eq && M.Playing && (m == Mode.Strip && S.StripProgress);
    }

    void OnFrame(object? s, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Min(now - lastFrame, 1 / 30.0);
        lastFrame = now;
        if (dt <= 0) return;
        if (pending != null && pendingByWidth)
        {
            // стягивание полоски — быстрое и без перелёта
            sw.Step(dt, 320, 0.95);
            sh.Step(dt, 320, 0.95);
            sb.Step(dt, 320, 0.95);
        }
        else
        {
            sw.Step(dt, 150, 0.8);
            sh.Step(dt, 210, 0.7);
            sb.Step(dt, 210, 0.75);
        }
        AdvancePhase();
        InvalidateVisual();
        if (!Animating) UpdateTicking();
    }

    void SetExpanded(bool on)
    {
        if (on && (suppressed || menuOpen || alert != null)) return;
        if (expanded == on) return;
        expanded = on;
        Retarget();
    }

    public void ShowActivity(ActivityInfo a, double seconds)
    {
        activity = a;
        activityTimer.Stop();
        activityTimer.Interval = TimeSpan.FromSeconds(seconds);
        activityTimer.Start();
        Retarget();
    }

    DispatcherTimer? peekTimer;

    /// <summary>Показать раскрытый остров на несколько секунд (горячая клавиша, в т.ч. поверх игры).</summary>
    public void Peek(double seconds, Action? done = null)
    {
        if (alert != null) return;
        hidden = false;
        expanded = true;
        Retarget();
        peekTimer?.Stop();
        peekTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        peekTimer.Tick += (_, _) =>
        {
            peekTimer!.Stop();
            if (IsMouseOver) { peekTimer.Start(); return; }
            expanded = false;
            Retarget();
            done?.Invoke();
        };
        peekTimer.Start();
    }

    /// <summary>Уведомление Windows: остров выезжает из полоски двухстрочной пилюлей.</summary>
    public void ShowNotification(string app, string title, string body)
    {
        if (!host.IsMain || hidden || alert != null || expanded) return;
        if (title.Length == 0) { title = app; app = ""; }
        if (title.Length == 0) return;
        ShowActivity(new ActivityInfo("notif", title, body.Length > 0 ? body : app, Blue), 4.5);
    }

    public void Notify(string left, string right, Color color, double seconds = 2) =>
        ShowActivity(new ActivityInfo("note", left, right, color), seconds);

    bool menuOpen;

    /// <summary>Открыто меню острова — карточка сворачивается, чтобы не закрывать его.</summary>
    public void SetMenuOpen(bool on)
    {
        menuOpen = on;
        hoverTimer.Stop();
        if (on && expanded) { expanded = false; Retarget(); }
    }

    public void SetSuppressed(bool s)
    {
        if (suppressed == s) return;
        suppressed = s;
        if (s && expanded) { expanded = false; Retarget(); }
    }

    public void SetHidden(bool h)
    {
        hidden = h;
        if (h && isBig) { isBig = false; host.ReportBig(); }
        if (h) expanded = false;
        UpdateTicking();
        if (!h) Retarget();
    }

    void UpdateAccent()
    {
        if (S.StripColor == "accent") accent = Themes.Accent;
        else if (M.Art == null) accent = Color.FromRgb(235, 235, 240);
        else
        {
            var (h, s, l) = ToHsl(M.Accent);
            accent = FromHsl(h, Math.Max(s, 0.5), Math.Clamp(l, 0.6, 0.76));
        }
        accentBrush = Freeze(new SolidColorBrush(accent));
    }

    // ------------------------------------------------------------------ события

    void OnMedia(bool trackChanged)
    {
        UpdateAccent();
        if (trackChanged && S.TrackPeek && M.Playing && !expanded && alert == null && !hidden && host.IsMain)
        {
            string text = string.IsNullOrEmpty(M.Artist) ? M.Title : $"{M.Title} — {M.Artist}";
            ShowActivity(new ActivityInfo("track", text, "", accent), 3.2);
            return;
        }
        Retarget();
    }

    void OnReminder(Reminder r)
    {
        if (!host.Primary) return;
        if (alert != null || hidden) { alerts.Enqueue(r); return; }
        alert = r;
        expanded = false;
        if (S.ReminderSound) SystemSounds.Asterisk.Play();
        Retarget();
    }

    void CloseAlert(bool done)
    {
        if (alert == null) return;
        var r = alert;
        alert = null;
        if (done) scheduler.Done(r);
        else scheduler.Snooze(r, TimeSpan.FromMinutes(10));
        if (alerts.Count > 0) OnReminder(alerts.Dequeue());
        else Retarget();
    }

    public void OnClipboard()
    {
        if (!host.IsMain) return;
        double now = clock.Elapsed.TotalSeconds;
        if (S.ClipboardToast && now - lastClip > 0.6 && !hidden && alert == null)
            ShowActivity(new ActivityInfo("copy", global::CoolPlayer.L.T("Скопировано"), "", Blue), 1.8);
        lastClip = now;
    }

    public void OnPowerChanged()
    {
        var old = power;
        power = Native.Power();
        if (S.PowerToast && power.has && old.has && power.ac != old.ac)
        {
            int p = power.pct;
            ShowActivity(power.ac
                ? new ActivityInfo("charge", global::CoolPlayer.L.T("Зарядка"), $"{p}%", Green)
                : new ActivityInfo("battery", global::CoolPlayer.L.T("От батареи"), $"{p}%", p <= 20 ? Red : Colors.White), 2.6);
        }
        InvalidateVisual();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        W = ActualWidth / Scale;
        H = ActualHeight / Scale;
        if (!sized)
        {
            sized = true;
            var (w, h, b) = TargetFor(Mode.Strip);
            sw.Value = sw.Target = w;
            sh.Value = sh.Target = h;
            sb.Value = sb.Target = b;
            UpdateAccent();
        }
        Retarget();
    }

    void SetVolFrom(Point p)
    {
        if (volRect.IsEmpty) return;
        double inner = volRect.Width - 12;
        Live.SystemVolume = (float)Math.Clamp((p.X - volRect.X - 6) / inner, 0, 1);
        InvalidateVisual();
    }

    /// <summary>Колесо мыши над островом — громкость Windows (по 2%).</summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        float v = Live.SystemVolume + (e.Delta > 0 ? 0.02f : -0.02f) * Math.Max(1, Math.Abs(e.Delta) / 120);
        Live.SystemVolume = Math.Clamp(v, 0, 1);
        if (expanded) InvalidateVisual();
        else ShowActivity(new ActivityInfo("volume", global::CoolPlayer.L.T("Громкость"), $"{Math.Round(Math.Clamp(v, 0, 1) * 100)}", Colors.White) { Value = Math.Clamp(v, 0, 1) }, 1.4);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (draggingVol) { SetVolFrom(Pos(e)); return; }
        leaveTimer.Stop();
        if (expanded || hoverTimer.IsEnabled || suppressed || alert != null) return;
        bool onStrip = sh.Value < 8;
        hoverTimer.Interval = TimeSpan.FromMilliseconds(onStrip ? 420 : 160);
        hoverTimer.Start();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (draggingVol) return;
        hoverTimer.Stop();
        if (expanded) leaveTimer.Start();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var p = Pos(e);
        if (draggingVol)
        {
            draggingVol = false;
            ReleaseMouseCapture();
            SetVolFrom(p);
            return;
        }
        foreach (var (name, r) in buttons)
        {
            if (!r.Contains(p)) continue;
            switch (name)
            {
                case "done": CloseAlert(true); break;
                case "snooze": CloseAlert(false); break;
                case "settings": host.OpenSettings(); break;
                case "pip": if (AppHost.Web.InPip) AppHost.Web.ExitPip(true); else AppHost.Web.EnterPip(); break;
                case "hub": AppHost.ShowHub(); break;
                case "mute": Live.ToggleMute(); InvalidateVisual(); break;
                default: media.Command(name); break;
            }
            return;
        }
        if (alert != null) return;
        if (!expanded)
        {
            hoverTimer.Stop();
            SetExpanded(true);
            return;
        }
        if (seekRect.Contains(p) && M.Dur > 0)
            media.Seek(Math.Clamp((p.X - seekRect.X) / seekRect.Width, 0, 1) * M.Dur);
    }

    // двойной клик по раскрытому острову (мимо кнопок и полосы перемотки) — открыть сам плеер
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (expanded && alert == null && e.ClickCount == 1 && volRect.Contains(Pos(e)))
        {
            draggingVol = true;
            CaptureMouse();
            SetVolFrom(Pos(e));
            e.Handled = true;
            return;
        }
        if (e.ClickCount != 2 || !expanded || alert != null) return;
        var p = Pos(e);
        if (volRect.Contains(p)) return;
        if (seekRect.Contains(p) || buttons.Values.Any(r => r.Contains(p))) return;
        e.Handled = true;
        AppHost.ShowHub();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        host.ShowMenu();
    }

    // ------------------------------------------------------------------ отрисовка

    protected override void OnRender(DrawingContext dc)
    {
        if (Scale == 1) { Render(dc); return; }
        dc.PushTransform(new ScaleTransform(Scale, Scale));
        Render(dc);
        dc.Pop();
    }

    void Render(DrawingContext dc)
    {
        ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip * Scale;
        buttons.Clear();
        seekRect = Rect.Empty;
        volRect = Rect.Empty;

        // невидимая кромка по верху панели — ловит наведение
        dc.DrawRectangle(HitBrush, null, new Rect(0, TbTop, W, 5));

        double w = sw.Value, h = Math.Max(0, sh.Value), cy = H - sb.Value;
        double cx = W / 2 + S.OffsetX;
        if (h < 0.5 || w < 1) return;

        var rect = new Rect(cx - w / 2, cy - h / 2, w, h);
        double rad = Math.Min(h / 2, 28);

        // мягкое свечение цвета обложки, когда остров «налился» из полоски
        if (Glowing && h > 8)
        {
            double k = Clamp01((clock.Elapsed.TotalSeconds - glowStart) / GlowLen);
            if (k > 0)
            {
                double fade = (1 - k) * (1 - k);
                for (int i = 3; i >= 1; i--)
                {
                    double grow = (2 + 7 * k) * i / 3;
                    var gr = new Rect(rect.X - grow, rect.Y - grow, rect.Width + 2 * grow, rect.Height + 2 * grow);
                    var pen = new Pen(Freeze(new SolidColorBrush(WithA(accent, (byte)(90 * fade / i)))), 2.2);
                    pen.Freeze();
                    dc.DrawRoundedRectangle(null, pen, gr, rad + grow, rad + grow);
                }
            }
        }

        dc.DrawRoundedRectangle(Black, h > 8 ? Outline : null, rect, rad, rad);
        if (h < 8)
        {
            if (S.StripProgress && M.Has) DrawStripProgress(dc, rect);
            return;
        }

        var clip = new RectangleGeometry(rect, rad, rad);
        clip.Freeze();
        dc.PushClip(clip);
        double L = rect.Left, R = rect.Right, T = rect.Top;
        double f = Clamp01((h - 44) / (100 - 44)); // 0 — компакт, 1 — раскрыт

        var cm = CompactMode();
        if (cm != Mode.Strip)
        {
            var (tw, th, _) = TargetFor(cm);
            double a = Clamp01(1 - f * 2.5) * Clamp01(1 - Math.Abs(w - tw) / 90) * Clamp01(1 - Math.Abs(h - th) / 16);
            if (a > 0.01)
            {
                dc.PushOpacity(a);
                switch (cm)
                {
                    case Mode.Media: DrawCompactMedia(dc, L, R, cy); break;
                    case Mode.Activity: DrawActivity(dc, L, R, cy); break;
                    case Mode.Live: DrawLive(dc, L, R, cy, rect); break;
                    case Mode.Info: DrawInfo(dc, cx, cy); break;
                }
                dc.Pop();
            }
        }

        double ea = Clamp01((f - 0.55) / 0.45);
        if (ea > 0.01)
        {
            dc.PushOpacity(ea);
            var mode = CurrentMode();
            if (mode == Mode.Alert || alert != null) DrawAlert(dc, L, R, T, h, ea);
            else if (mode == Mode.ExpMedia || (mode != Mode.ExpIdle && M.Has && M.Playing)) DrawExpandedMedia(dc, L, R, T, ea);
            else DrawExpandedIdle(dc, L, R, T, h, ea);
            dc.Pop();
        }
        dc.Pop();
    }

    /// <summary>Прогресс трека на полоске: дорожка, яркая заливка и светящаяся «головка».</summary>
    void DrawStripProgress(DrawingContext dc, Rect strip)
    {
        double alpha = M.Playing ? 1 : 0.5;
        double p = M.Dur > 0 ? Clamp01(M.Position / M.Dur) : 1;
        double r = strip.Height / 2;
        // дорожка
        dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromArgb((byte)(34 * alpha), 255, 255, 255))), null, strip, r, r);
        double x = strip.Left + strip.Width * p;
        if (x - strip.Left < 1) return;
        var fill = new LinearGradientBrush(WithA(accent, (byte)(150 * alpha)), WithA(accent, (byte)(255 * alpha)),
            new Point(strip.Left, 0), new Point(x, 0)) { MappingMode = BrushMappingMode.Absolute };
        fill.Freeze();
        dc.DrawRoundedRectangle(fill, null, new Rect(strip.Left, strip.Top, x - strip.Left, strip.Height), r, r);
        // головка с мягким свечением
        var glow = new RadialGradientBrush(WithA(accent, (byte)(150 * alpha)), WithA(accent, 0));
        glow.Freeze();
        dc.DrawEllipse(glow, null, new Point(x, strip.Top + r), 9, 6);
        dc.DrawEllipse(Freeze(new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), 255, 255, 255))), null, new Point(x - 1, strip.Top + r), r * 0.75, r * 0.75);
    }

    // ---------------- компактные состояния

    void DrawCompactMedia(DrawingContext dc, double L, double R, double cy)
    {
        DrawArt(dc, new Rect(L + 6, cy - 10, 20, 20), 6);
        DrawBars(dc, R - 27, cy, 1);
        if (Live.PillLyrics.Line is { } line && R - L > 260)
        {
            var t = Text(line, 12.5, FontWeights.SemiBold, accentBrush, R - L - 36 - 42);
            dc.DrawText(t, new Point(L + 35, cy - t.Height / 2));
        }
    }

    /// <summary>Таймер, секундомер, помодоро, загрузка: значок, подпись, время/проценты и тонкий прогресс снизу.</summary>
    void DrawLive(DrawingContext dc, double L, double R, double cy, Rect pill)
    {
        if (Live.Current is not { } a) return;
        var color = Freeze(new SolidColorBrush(a.Color));
        DrawGlyph(dc, a.Glyph, 14, color, new Point(L + 24, cy));
        var right = Text(a.Right, 13, FontWeights.SemiBold, color);
        dc.DrawText(right, new Point(R - 18 - right.Width, cy - right.Height / 2));
        var left = Text(a.Label, 12.5, FontWeights.SemiBold, White, R - L - 40 - right.Width - 30);
        dc.DrawText(left, new Point(L + 40, cy - left.Height / 2));
        if (a.Progress >= 0)
        {
            double x0 = L + 16, x1 = R - 16, y = pill.Bottom - 3.5;
            dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255))), null, new Rect(x0, y, x1 - x0, 2), 1, 1);
            dc.DrawRoundedRectangle(color, null, new Rect(x0, y, (x1 - x0) * Math.Clamp(a.Progress, 0, 1), 2), 1, 1);
        }
    }

    void DrawActivity(DrawingContext dc, double L, double R, double cy)
    {
        var a = activity;
        if (a == null) return;
        if (a.Kind == "track")
        {
            DrawArt(dc, new Rect(L + 6, cy - 10, 20, 20), 6);
            var t = Text(a.Left, 12.5, FontWeights.SemiBold, White, R - L - 36 - 42);
            dc.DrawText(t, new Point(L + 35, cy - t.Height / 2));
            DrawBars(dc, R - 27, cy, 1);
            return;
        }
        if (a.Kind == "notif")
        {
            var ic = new Point(L + 25, cy);
            dc.DrawEllipse(Freeze(new SolidColorBrush(a.Color)), null, ic, 13, 13);
            DrawGlyph(dc, "", 13, White, ic);
            double x = L + 46, maxW = R - x - 18;
            var t1 = Text(a.Left, 12.5, FontWeights.SemiBold, White, maxW);
            if (a.Right.Length == 0) { dc.DrawText(t1, new Point(x, cy - t1.Height / 2)); return; }
            var t2 = Text(a.Right, 11.5, FontWeights.Normal, Freeze(new SolidColorBrush(Color.FromRgb(170, 170, 178))), maxW);
            double top = cy - (t1.Height + t2.Height) / 2;
            dc.DrawText(t1, new Point(x, top));
            dc.DrawText(t2, new Point(x, top + t1.Height));
            return;
        }
        if (a.Kind == "volume")
        {
            var vc = Freeze(new SolidColorBrush(a.Color));
            DrawGlyph(dc, a.Value <= 0 ? "\uE74F" : a.Value < 0.34 ? "\uE993" : a.Value < 0.67 ? "\uE994" : "\uE995", 14, vc, new Point(L + 24, cy));
            var num = Text(a.Right, 13, FontWeights.SemiBold, vc);
            dc.DrawText(num, new Point(R - 18 - num.Width, cy - num.Height / 2));
            double bx0 = L + 44, bx1 = R - 18 - 28;
            dc.DrawRoundedRectangle(Freeze(new SolidColorBrush(Color.FromArgb(46, 255, 255, 255))), null, new Rect(bx0, cy - 2.5, bx1 - bx0, 5), 2.5, 2.5);
            if (a.Value > 0) dc.DrawRoundedRectangle(vc, null, new Rect(bx0, cy - 2.5, (bx1 - bx0) * Math.Clamp(a.Value, 0, 1), 5), 2.5, 2.5);
            return;
        }
        var left = Text(a.Left, 13, FontWeights.SemiBold, White, R - L - 140);
        dc.DrawText(left, new Point(L + 18, cy - left.Height / 2));
        var color = Freeze(new SolidColorBrush(a.Color));
        if (a.Kind is "charge" or "battery")
        {
            DrawBattery(dc, R - 18 - 27, cy, power.pct, color);
            var right = Text(a.Right, 13, FontWeights.SemiBold, color);
            dc.DrawText(right, new Point(R - 18 - 27 - 8 - right.Width, cy - right.Height / 2));
        }
        else if (a.Kind == "copy")
        {
            var c = new Point(R - 26, cy);
            dc.DrawEllipse(color, null, c, 9, 9);
            var g = new StreamGeometry();
            using (var s = g.Open())
            {
                s.BeginFigure(new Point(c.X - 4, c.Y + 0.2), false, false);
                s.LineTo(new Point(c.X - 1, c.Y + 3.2), true, true);
                s.LineTo(new Point(c.X + 4.5, c.Y - 3), true, true);
            }
            dc.DrawGeometry(null, new Pen(White, 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
        }
        else if (a.Right.Length > 0)
        {
            var right = Text(a.Right, 13, FontWeights.SemiBold, color);
            dc.DrawText(right, new Point(R - 18 - right.Width, cy - right.Height / 2));
        }
    }

    // Пилюля с информацией: сегменты «иконка + текст»
    List<(string icon, string text)> InfoSegments()
    {
        var list = new List<(string, string)>();
        var now = DateTime.Now;
        void Clock() => list.Add(("", now.ToString("HH:mm")));
        void Weather() => list.Add(weather.Current is { } w ? ("wx", WeatherInfo.Deg(w.Temp)) : ("", "—°"));
        switch (info)
        {
            case InfoKind.Clock: Clock(); break;
            case InfoKind.Weather: Weather(); break;
            case InfoKind.ClockWeather: Clock(); Weather(); break;
            case InfoKind.Date: list.Add(("", now.ToString("ddd, d MMMM", Ru))); break;
            case InfoKind.NextReminder:
                var r = Scheduler.NextReminder();
                list.Add(("bell", r == null ? global::CoolPlayer.L.T("Нет напоминаний") : $"{r.DueAt:HH:mm}  {r.Text}"));
                break;
            case InfoKind.Custom: list.Add(("", string.IsNullOrWhiteSpace(infoText) ? "…" : infoText)); break;
        }
        return list;
    }

    const double InfoMaxText = 240, InfoGap = 14, IconW = 18, IconGap = 6;

    double SegmentsWidth(List<(string icon, string text)> segs)
    {
        double w = 0;
        foreach (var (icon, text) in segs)
            w += (icon.Length > 0 ? IconW + IconGap : 0) + Text(text, 13, FontWeights.SemiBold, White, InfoMaxText).WidthIncludingTrailingWhitespace;
        return w + InfoGap * Math.Max(0, segs.Count - 1);
    }

    double InfoWidth() => Math.Max(96, SegmentsWidth(InfoSegments()) + 36);

    void DrawInfo(DrawingContext dc, double cx, double cy)
    {
        var segs = InfoSegments();
        double x = cx - SegmentsWidth(segs) / 2;
        foreach (var (icon, text) in segs)
        {
            if (icon == "wx" && weather.Current is { } w) { DrawWeatherIcon(dc, new Point(x + IconW / 2, cy), IconW, w.Code, w.IsDay); x += IconW + IconGap; }
            else if (icon == "bell") { DrawGlyph(dc, "", 14, Freeze(new SolidColorBrush(Orange)), new Point(x + IconW / 2, cy)); x += IconW + IconGap; }
            var t = Text(text, 13, FontWeights.SemiBold, White, InfoMaxText);
            dc.DrawText(t, new Point(x, cy - t.Height / 2));
            x += t.WidthIncludingTrailingWhitespace + InfoGap;
        }
    }

    // ---------------- раскрытые состояния

    void DrawExpandedMedia(DrawingContext dc, double L, double R, double T, double alpha)
    {
        const double pad = 18;
        var art = new Rect(L + pad, T + pad, 60, 60);
        DrawArt(dc, art, 13);

        double x0 = art.Right + 14, tw = R - pad - 30 - x0;
        dc.DrawText(Text(M.Title, 15, FontWeights.SemiBold, White, tw), new Point(x0, T + pad + 7));
        dc.DrawText(Text(M.Artist, 13, FontWeights.Normal, Gray, tw), new Point(x0, T + pad + 31));
        DrawBars(dc, R - pad - 19, T + pad + 18, 0.85);

        double y = T + pad + 60 + 19, pos = M.Position, dur = M.Dur;
        double bx = L + pad + 42, bw = R - L - 2 * pad - 84;
        if (dur > 0)
        {
            var a = Text(Fmt(pos), 11, FontWeights.Medium, Gray);
            dc.DrawText(a, new Point(L + pad, y - a.Height / 2));
            var b = Text("-" + Fmt(dur - pos), 11, FontWeights.Medium, Gray);
            dc.DrawText(b, new Point(R - pad - b.Width, y - b.Height / 2));
        }
        dc.DrawRoundedRectangle(Track, null, new Rect(bx, y - 2, bw, 4), 2, 2);
        if (dur > 0)
            dc.DrawRoundedRectangle(White, null, new Rect(bx, y - 2, Math.Max(4, bw * Clamp01(pos / dur)), 4), 2, 2);

        double cy = y + 30, cx = (L + R) / 2;
        DrawSkip(dc, cx - 68, cy, false);
        if (M.Playing)
        {
            dc.DrawRoundedRectangle(White, null, new Rect(cx - 8.5, cy - 10.5, 6, 21), 2, 2);
            dc.DrawRoundedRectangle(White, null, new Rect(cx + 2.5, cy - 10.5, 6, 21), 2, 2);
        }
        else Triangle(dc, cx - 6.5, cy, 17, 20, true);
        DrawSkip(dc, cx + 68, cy, true);

        bool pip = AppHost.Web.CanPip || AppHost.Web.InPip;
        if (pip) DrawPipIcon(dc, R - pad - 14, cy, AppHost.Web.InPip);

        // громкость Windows: значок (клик — без звука), полоса (клик/перетаскивание), проценты; колесо — где угодно на острове
        double vy = cy + 34;
        float vol = Live.SystemVolume;
        bool muted = Live.SystemMuted;
        double v = muted ? 0 : vol;
        string vg = v <= 0 ? "\uE74F" : v < 0.34 ? "\uE993" : v < 0.67 ? "\uE994" : "\uE995";
        DrawGlyph(dc, vg, 14, muted ? Freeze(new SolidColorBrush(Color.FromRgb(255, 105, 97))) : Gray, new Point(L + pad + 9, vy));
        double vx = L + pad + 30, vw = R - L - 2 * pad - 30 - 40;
        dc.DrawRoundedRectangle(Track, null, new Rect(vx, vy - 2, vw, 4), 2, 2);
        dc.DrawRoundedRectangle(accentBrush, null, new Rect(vx, vy - 2, Math.Max(4, vw * v), 4), 2, 2);
        dc.DrawEllipse(White, null, new Point(vx + vw * v, vy), draggingVol ? 7 : 5.5, draggingVol ? 7 : 5.5);
        var pct = Text($"{Math.Round(v * 100)}", 11, FontWeights.Medium, Gray);
        dc.DrawText(pct, new Point(R - pad - pct.Width, vy - pct.Height / 2));

        if (alpha > 0.9)
        {
            buttons["hub"] = art;
            if (pip) buttons["pip"] = new Rect(R - pad - 14 - 22, cy - 20, 44, 40);
            buttons["prev"] = new Rect(cx - 68 - 24, cy - 20, 48, 40);
            buttons["toggle"] = new Rect(cx - 24, cy - 20, 48, 40);
            buttons["next"] = new Rect(cx + 68 - 24, cy - 20, 48, 40);
            if (dur > 0) seekRect = new Rect(bx, y - 9, bw, 18);
            volRect = new Rect(vx - 6, vy - 10, vw + 12, 20);
            buttons["mute"] = new Rect(L + pad - 4, vy - 12, 26, 24);
        }
    }

    void DrawExpandedIdle(DrawingContext dc, double L, double R, double T, double h, double alpha)
    {
        var now = DateTime.Now;
        dc.DrawText(Text(now.ToString("HH:mm"), 34, FontWeights.SemiBold, White), new Point(L + 22, T + 12));
        var ds = now.ToString("dddd, d MMMM", Ru);
        dc.DrawText(Text(char.ToUpper(ds[0]) + ds[1..], 13, FontWeights.Normal, Gray), new Point(L + 24, T + 56));

        // погода справа
        if (weather.Current is { } w)
        {
            double iconC = R - 22 - 17;
            DrawWeatherIcon(dc, new Point(iconC, T + 33), 34, w.Code, w.IsDay);
            var temp = Text(WeatherInfo.Deg(w.Temp), 26, FontWeights.SemiBold, White);
            dc.DrawText(temp, new Point(R - 22 - 34 - 10 - temp.Width, T + 15));
            var desc = Text($"{w.Desc}  ↑{WeatherInfo.Deg(w.Max)} ↓{WeatherInfo.Deg(w.Min)}", 12, FontWeights.Normal, Gray, 200);
            dc.DrawText(desc, new Point(R - 22 - desc.Width, T + 58));
        }
        else
        {
            var t = Text(weather.Status == global::CoolPlayer.L.T("Город не указан") ? global::CoolPlayer.L.T("Погода: укажите город") : weather.Status, 12, FontWeights.Normal, Gray, 190);
            dc.DrawText(t, new Point(R - 22 - 22 - t.Width, T + 30 - t.Height / 2));
            DrawGlyph(dc, "", 13, Gray, new Point(R - 22 - 8, T + 30)); // шестерёнка
            if (alpha > 0.9) buttons["settings"] = new Rect(R - 22 - 230, T + 16, 230, 28);
        }

        // мини-плеер, если что-то стоит на паузе
        if (M.Has)
        {
            double my = T + 136;
            dc.DrawRectangle(Track, null, new Rect(L + 22, T + 112, R - L - 44, 1));
            DrawArt(dc, new Rect(L + 22, my - 14, 28, 28), 7);
            var mt = Text(M.Title, 13, FontWeights.SemiBold, White, R - L - 22 - 38 - 60);
            dc.DrawText(mt, new Point(L + 60, my - mt.Height / 2));
            double px = R - 22 - 14;
            if (M.Playing)
            {
                dc.DrawRoundedRectangle(White, null, new Rect(px - 6, my - 7.5, 4.5, 15), 1.5, 1.5);
                dc.DrawRoundedRectangle(White, null, new Rect(px + 1.5, my - 7.5, 4.5, 15), 1.5, 1.5);
            }
            else Triangle(dc, px - 5, my, 12, 14, true);
            if (alpha > 0.9) buttons["toggle"] = new Rect(px - 22, my - 20, 44, 40);
        }

        // ближайшее напоминание + батарея
        double by = T + 94;
        var next = Scheduler.NextReminder();
        DrawGlyph(dc, "", 12, Freeze(new SolidColorBrush(next == null ? Color.FromArgb(150, 255, 255, 255) : Orange)), new Point(L + 30, by));
        string rt = next == null ? global::CoolPlayer.L.T("Напоминаний нет") : $"{RelDay(next.DueAt)} {next.DueAt:HH:mm} · {next.Text}";
        var rtx = Text(rt, 12, FontWeights.Normal, Gray, R - L - 44 - (power.has ? 90 : 22));
        dc.DrawText(rtx, new Point(L + 42, by - rtx.Height / 2));
        if (power.has)
        {
            var c = power.ac ? Freeze(new SolidColorBrush(Green)) : power.pct <= 20 ? Freeze(new SolidColorBrush(Red)) : White;
            DrawBattery(dc, R - 22 - 27, by, power.pct, c);
            var p = Text($"{power.pct}%", 12, FontWeights.SemiBold, c);
            dc.DrawText(p, new Point(R - 22 - 27 - 6 - p.Width, by - p.Height / 2));
        }
    }

    void DrawAlert(DrawingContext dc, double L, double R, double T, double h, double alpha)
    {
        var r = alert;
        if (r == null) return;
        const double pad = 18;
        var orange = Freeze(new SolidColorBrush(Orange));
        dc.DrawEllipse(orange, null, new Point(L + pad + 17, T + pad + 17), 17, 17);
        DrawGlyph(dc, "", 16, White, new Point(L + pad + 17, T + pad + 17));

        double x0 = L + pad + 34 + 12;
        dc.DrawText(Text(global::CoolPlayer.L.T("Напоминание"), 12, FontWeights.Normal, Gray), new Point(x0, T + pad - 1));
        var when = Text(r.DueAt.ToString("HH:mm"), 12, FontWeights.Normal, Gray);
        dc.DrawText(when, new Point(R - pad - when.Width, T + pad - 1));
        dc.DrawText(Text(r.Text, 15, FontWeights.SemiBold, White, R - pad - x0, 2), new Point(x0, T + pad + 16));

        double by = T + h - pad - 34, bw = (R - L - 2 * pad - 10) / 2;
        var snooze = new Rect(L + pad, by, bw, 34);
        var done = new Rect(L + pad + bw + 10, by, bw, 34);
        dc.DrawRoundedRectangle(ButtonBg, null, snooze, 17, 17);
        dc.DrawRoundedRectangle(orange, null, done, 17, 17);
        var st = Text(global::CoolPlayer.L.T("Отложить на 10 мин"), 13, FontWeights.SemiBold, White);
        dc.DrawText(st, new Point(snooze.X + (snooze.Width - st.Width) / 2, snooze.Y + (34 - st.Height) / 2));
        var dt = Text(global::CoolPlayer.L.T("Готово"), 13, FontWeights.SemiBold, Black);
        dc.DrawText(dt, new Point(done.X + (done.Width - dt.Width) / 2, done.Y + (34 - dt.Height) / 2));
        if (alpha > 0.9) { buttons["snooze"] = snooze; buttons["done"] = done; }
    }

    // ---------------- элементы

    void DrawArt(DrawingContext dc, Rect r, double radius)
    {
        if (M.Art != null)
        {
            dc.PushClip(new RectangleGeometry(r, radius, radius));
            dc.DrawImage(M.Art, r);
            dc.Pop();
            return;
        }
        dc.DrawRoundedRectangle(Track, null, r, radius, radius);
        var t = Text("♪", r.Height * 0.5, FontWeights.Normal, Gray);
        dc.DrawText(t, new Point(r.X + (r.Width - t.Width) / 2, r.Y + (r.Height - t.Height) / 2));
    }

    void DrawBars(DrawingContext dc, double x, double cy, double scale)
    {
        double t = clock.Elapsed.TotalSeconds, bw = 3 * scale, gap = 2.6 * scale;
        for (int i = 0; i < 4; i++)
        {
            double bh = 3 * scale;
            if (M.Playing)
            {
                double v = 0.5 + 0.5 * Math.Sin(t * (6 + i * 1.7) + BarPhase[i]) * Math.Sin(t * (2.3 + i) + BarPhase[i] * 2);
                bh = (4 + 12 * Math.Abs(v)) * scale;
            }
            dc.DrawRoundedRectangle(accentBrush, null, new Rect(x + i * (bw + gap), cy - bh / 2, bw, bh), bw / 2, bw / 2);
        }
    }

    void DrawBattery(DrawingContext dc, double x, double cy, int pct, Brush fill)
    {
        dc.DrawRoundedRectangle(null, BatteryPen, new Rect(x, cy - 6.5, 24, 13), 3.5, 3.5);
        dc.DrawRoundedRectangle(Gray, null, new Rect(x + 25, cy - 2.5, 2, 5), 1, 1);
        dc.DrawRoundedRectangle(fill, null, new Rect(x + 2, cy - 4.5, Math.Max(1, 20 * Clamp01(pct / 100.0)), 9), 2, 2);
    }

    /// <summary>Значок «картинка в картинке»: рамка экрана и маленькое окно в углу.</summary>
    void DrawPipIcon(DrawingContext dc, double cx, double cy, bool active)
    {
        var pen = new Pen(active ? accentBrush : Gray, 1.6);
        dc.DrawRoundedRectangle(null, pen, new Rect(cx - 10, cy - 7, 20, 14), 3, 3);
        dc.DrawRoundedRectangle(active ? accentBrush : White, null, new Rect(cx + 0.5, cy + 0.5, 7.5, 5), 1.5, 1.5);
    }

    void DrawGlyph(DrawingContext dc, string glyph, double size, Brush brush, Point center)
    {
        var t = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(Icons, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, brush, ppd);
        dc.DrawText(t, new Point(center.X - t.Width / 2, center.Y - t.Height / 2));
    }

    static readonly Brush SunBrush = Freeze(new SolidColorBrush(Color.FromRgb(255, 204, 0)));
    static readonly Brush MoonBrush = Freeze(new SolidColorBrush(Color.FromRgb(230, 230, 210)));
    static readonly Brush CloudBrush = Freeze(new SolidColorBrush(Color.FromRgb(214, 220, 230)));
    static readonly Brush RainBrush = Freeze(new SolidColorBrush(Color.FromRgb(90, 170, 255)));

    /// <summary>Векторные значки погоды: солнце, луна, облака, дождь, снег, гроза, туман.</summary>
    static void DrawWeatherIcon(DrawingContext dc, Point c, double s, int code, bool day)
    {
        double u = s / 24.0; // всё рисуем в сетке 24×24
        Point P(double x, double y) => new(c.X + (x - 12) * u, c.Y + (y - 12) * u);

        void Sun(double x, double y, double r)
        {
            var pen = new Pen(SunBrush, 1.8 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                dc.DrawLine(pen, P(x + Math.Cos(a) * (r + 2.2), y + Math.Sin(a) * (r + 2.2)), P(x + Math.Cos(a) * (r + 4.4), y + Math.Sin(a) * (r + 4.4)));
            }
            dc.DrawEllipse(SunBrush, null, P(x, y), r * u, r * u);
        }
        void Moon(double x, double y, double r)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(P(x, y), r * u, r * u), new EllipseGeometry(P(x + r * 0.55, y - r * 0.4), r * 0.85 * u, r * 0.85 * u));
            dc.DrawGeometry(MoonBrush, null, g);
        }
        void Cloud(double dy)
        {
            dc.DrawEllipse(CloudBrush, null, P(9, 13 + dy), 4.5 * u, 4.5 * u);
            dc.DrawEllipse(CloudBrush, null, P(14, 11 + dy), 5.8 * u, 5.8 * u);
            dc.DrawRoundedRectangle(CloudBrush, null, new Rect(P(4.5, 12.5 + dy), P(20.5, 17.5 + dy)), 2.5 * u, 2.5 * u);
        }

        switch (code)
        {
            case 0:
                if (day) Sun(12, 12, 4.5); else Moon(12, 12, 7);
                break;
            case 1 or 2:
                if (day) Sun(16, 8, 3.2); else Moon(16, 8, 4.5);
                Cloud(2);
                break;
            case 3:
                Cloud(0);
                break;
            case 45 or 48:
            {
                var pen = new Pen(CloudBrush, 2 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, P(4, 8), P(20, 8));
                dc.DrawLine(pen, P(6, 12.5), P(18, 12.5));
                dc.DrawLine(pen, P(4, 17), P(20, 17));
                break;
            }
            case (>= 71 and <= 77) or 85 or 86:
                Cloud(-3);
                foreach (var x in new[] { 8.0, 12, 16 }) dc.DrawEllipse(White, null, P(x, 20.5 - (x == 12 ? 1.5 : 0)), 1.4 * u, 1.4 * u);
                break;
            case >= 95:
            {
                Cloud(-3);
                var bolt = new StreamGeometry();
                using (var g = bolt.Open())
                {
                    g.BeginFigure(P(13, 15), true, true);
                    g.LineTo(P(9.5, 20), true, true);
                    g.LineTo(P(12, 20), true, true);
                    g.LineTo(P(10.5, 24), true, true);
                    g.LineTo(P(15, 18.5), true, true);
                    g.LineTo(P(12.5, 18.5), true, true);
                }
                dc.DrawGeometry(SunBrush, null, bolt);
                break;
            }
            case >= 51:
            {
                Cloud(-3);
                var pen = new Pen(RainBrush, 1.8 * u) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                foreach (var x in new[] { 8.5, 12.5, 16.5 }) dc.DrawLine(pen, P(x, 18), P(x - 1.5, 22));
                break;
            }
            default:
                Cloud(0);
                break;
        }
    }

    void Triangle(DrawingContext dc, double x, double cy, double w, double h, bool fwd)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            if (fwd)
            {
                c.BeginFigure(new Point(x, cy - h / 2), true, true);
                c.LineTo(new Point(x + w, cy), true, true);
                c.LineTo(new Point(x, cy + h / 2), true, true);
            }
            else
            {
                c.BeginFigure(new Point(x + w, cy - h / 2), true, true);
                c.LineTo(new Point(x, cy), true, true);
                c.LineTo(new Point(x + w, cy + h / 2), true, true);
            }
        }
        g.Freeze();
        dc.DrawGeometry(White, IconPen, g);
    }

    void DrawSkip(DrawingContext dc, double cx, double cy, bool fwd)
    {
        Triangle(dc, cx - 11, cy, 10.5, 13, fwd);
        Triangle(dc, cx + 0.5, cy, 10.5, 13, fwd);
    }

    FormattedText Text(string s, double size, FontWeight weight, Brush brush, double maxW = 0, int lines = 1)
    {
        string key = $"{s}\u0001{size}\u0001{weight}\u0001{RuntimeHelpers.GetHashCode(brush)}\u0001{Math.Round(maxW)}\u0001{lines}\u0001{ppd}";
        if (textCache.TryGetValue(key, out var ft)) return ft;
        if (textCache.Count > 128) textCache.Clear();
        ft = new FormattedText(s, Ru, FlowDirection.LeftToRight,
            new Typeface(Font, FontStyles.Normal, weight, FontStretches.Normal), size, brush, ppd);
        if (maxW > 0)
        {
            ft.MaxTextWidth = Math.Max(1, maxW);
            ft.MaxLineCount = lines;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        textCache[key] = ft;
        return ft;
    }

    // ------------------------------------------------------------------ утилиты

    static string RelDay(DateTime d) =>
        d.Date == DateTime.Today ? global::CoolPlayer.L.T("Сегодня") : d.Date == DateTime.Today.AddDays(1) ? global::CoolPlayer.L.T("Завтра") : d.ToString("d MMM", Ru);

    static string Fmt(double sec)
    {
        int s = Math.Max(0, (int)sec);
        return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
    }

    static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2;
        if (mx - mn < 1e-6) return (0, 0, l);
        double d = mx - mn, s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
        double h = mx == r ? (g - b) / d + (g < b ? 6 : 0) : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)(Hue(p, q, h + 1 / 3.0) * 255), (byte)(Hue(p, q, h) * 255), (byte)(Hue(p, q, h - 1 / 3.0) * 255));
    }
}
