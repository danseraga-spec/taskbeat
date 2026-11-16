using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using static CoolPlayer.Native;

namespace CoolPlayer;

/// <summary>
/// Прозрачное окно над панелью задач. Отвечает за Win32-часть: позиция у кромки панели,
/// цвет панели, полноэкранные приложения, буфер обмена, питание, трей и меню.
/// Всё работает на системных уведомлениях, без постоянного опроса.
/// Основное окно (на главном мониторе) держит трей, горячие клавиши и уведомления
/// и создаёт дополнительные окна-полоски на остальных мониторах с панелью задач.
/// </summary>
sealed class IslandWindow : Window
{
    const double TallDip = 196;           // раскрытая карточка над панелью + перелёт пружины
    bool tall = true;
    const int WM_APPBAR = WM_APP + 1, WM_TRAY = WM_APP + 2;

    readonly IslandView view;
    IntPtr hwnd, fgHook, trayIcon;
    WinEventProc? fgProc;
    uint taskbarCreatedMsg;
    bool fullscreen;
    IntPtr fsWnd;          // окно, ради которого остров спрятан
    RECT taskbar;
    IntPtr monitor;
    readonly bool primary;
    readonly List<IslandWindow> others = [];

    public bool Primary => primary;
    bool standIn;
    /// <summary>Окно сейчас «главное»: показывает пилюлю, трек, уведомления.
    /// Основной монитор — пока на нём нет полноэкранного приложения; иначе его подменяет второй.</summary>
    public bool IsMain => primary ? !fullscreen : standIn;
    static IslandWindow? Root => AppHost.Island;


    /// <summary>Только у основного окна: распределить роли и спрятать полоски, если где-то раскрыт остров.</summary>
    // Пересчёты ролей и полосок идут отложенно и по одному — так изменения острова
    // не могут вызывать друг друга по кругу (раньше это могло переполнить стек).
    bool rolesQueued, yieldQueued, inRelayout;

    void UpdateRoles()
    {
        if (!primary || rolesQueued) return;
        rolesQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { rolesQueued = false; ApplyRoles(); });
    }

    void ApplyRoles()
    {
        var stand = fullscreen ? others.FirstOrDefault(o => !o.fullscreen) : null;
        foreach (var o in others)
        {
            bool st = o == stand;
            if (o.standIn == st) continue;
            o.standIn = st;
            o.view.RoleChanged();
        }
        view.RoleChanged();
        ApplyYield();
    }

    void UpdateYield()
    {
        if (!primary || yieldQueued) return;
        yieldQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () => { yieldQueued = false; ApplyYield(); });
    }

    /// <summary>
    /// У острова один владелец: окно, где он раскрыт, иначе — главное окно.
    /// Если владелец показывает остров, на остальных мониторах полоски нет.
    /// </summary>
    void ApplyYield()
    {
        var all = All.ToList();
        var owner = all.FirstOrDefault(w => w.view.Expanded) ?? all.FirstOrDefault(w => w.IsMain);
        bool show = owner != null && owner.view.WantsIsland;
        foreach (var w in all) w.view.SetYield(show && w != owner);
    }

    public void ReportBig() => Root?.UpdateYield();
    IslandWindow MainWindow => All.FirstOrDefault(w => w.IsMain) ?? this;

    public IslandWindow(IntPtr secondaryMonitor = default)
    {
        primary = secondaryMonitor == IntPtr.Zero;
        monitor = secondaryMonitor;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = L.T("Taskbeat — остров");
        Left = Top = -32000;
        Width = Height = 10;

        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);

        view = new IslandView(this, AppHost.Media, AppHost.Weather, AppHost.Scheduler);
        Content = view;
        SourceInitialized += (_, _) => Init();
        Closed += (_, _) => view.Detach();
    }

    void Init()
    {
        hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        if (!primary) { Relayout(); return; }

        taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");
        RegisterAppBar();
        AddClipboardFormatListener(hwnd);
        AddTray();
        fgProc = OnForeground;
        fgHook = SetWinEventHook(3, 3, IntPtr.Zero, fgProc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND

        Relayout();
        Hotkeys.Register(hwnd);
        SettingsStore.Changed += () => { Relayout(); SyncMonitors(); };
        Later(800, SyncMonitors);
        Notifications.Start(this);

        // после прогрева отдаём системе память, нужную только при старте
        Later(5000, () =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
        });
    }

    void Later(int ms, Action a)
    {
        var t = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    void RegisterAppBar()
    {
        // регистрируемся как appbar без резервирования места — ради уведомлений
        // о полноэкранных приложениях и перемещении панели задач
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd, uCallbackMessage = WM_APPBAR };
        SHAppBarMessage(ABM_NEW, ref d);
    }

    void Relayout()
    {
        if (hwnd == IntPtr.Zero || inRelayout) return;
        inRelayout = true;
        try { DoRelayout(); }
        finally { inRelayout = false; }
    }

    void DoRelayout()
    {
        if (primary)
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            taskbar = SHAppBarMessage(ABM_GETTASKBARPOS, ref d) != IntPtr.Zero && d.uEdge == ABE_BOTTOM
                ? d.rc
                : new RECT { Left = 0, Top = sh, Right = sw, Bottom = sh };
            monitor = MonitorFromPoint(new POINT(), 1); // MONITOR_DEFAULTTOPRIMARY
        }
        else if (!FindTaskbar(monitor, out taskbar))
        {
            ShowWindow(hwnd, 0);
            return;
        }

        double scale = MonitorDpi(monitor) / 96.0;
        int tbPx = Math.Max(taskbar.Bottom - taskbar.Top, (int)Math.Round(40 * scale));
        double tbDip = tbPx / scale;

        // 2K/4K: остров крупнее, чтобы не выглядел крошечным на большом экране
        double k = SettingsStore.Current.IslandScale;
        if (k <= 0)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            double dipH = GetMonitorInfoW(monitor, ref mi) ? (mi.rcMonitor.Bottom - mi.rcMonitor.Top) / scale : 1080;
            k = dipH >= 2000 ? 1.4 : dipH >= 1400 ? 1.2 : 1;
            k = Math.Max(1, Math.Min(k, (tbDip - 6) / 32)); // пилюля должна влезать в панель
        }
        view.SetScale(k);
        view.TaskbarHeight = tbDip / k;

        // окно стоит на нижнем крае панели: в компактном виде — высотой с панель (или с пилюлю, если она крупнее),
        // для раскрытой карточки вытягивается вверх
        int compact = Math.Max(tbPx, (int)Math.Ceiling((tbDip / 2 + 16 * k + 4) * scale));
        int h = tall ? Math.Max(compact, (int)Math.Ceiling(TallDip * k * scale)) : compact;
        SetWindowPos(hwnd, HWND_TOPMOST, taskbar.Left, taskbar.Bottom - h, taskbar.Right - taskbar.Left, h, SWP_NOACTIVATE);
        if (!primary && !fullscreen) ShowWindow(hwnd, 4);
    }

    // ------------------------------------------------------------------ мониторы

    static uint MonitorDpi(IntPtr mon) =>
        GetDpiForMonitor(mon, 0, out uint x, out _) == 0 && x > 0 ? x : 96;

    /// <summary>Нижняя панель задач на дополнительном мониторе.</summary>
    static bool FindTaskbar(IntPtr mon, out RECT r)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(mon, ref mi))
        {
            IntPtr w = IntPtr.Zero;
            while ((w = FindWindowExW(IntPtr.Zero, w, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            {
                if (!GetWindowRect(w, out r) || MonitorFromRect(ref r, 0) != mon) continue;
                var m = mi.rcMonitor;
                if (Math.Abs(m.Bottom - r.Bottom) <= 2 && r.Right - r.Left > (m.Right - m.Left) / 2) return true;
            }
        }
        r = default;
        return false;
    }

    static List<IntPtr> Monitors()
    {
        var list = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (m, _, _, _) => { list.Add(m); return true; }, IntPtr.Zero);
        return list;
    }

    /// <summary>Полоски на остальных мониторах: создаём/убираем под текущую конфигурацию экранов.</summary>
    void SyncMonitors()
    {
        if (!primary || hwnd == IntPtr.Zero) return;
        var want = SettingsStore.Current.AllMonitors
            ? Monitors().Where(m => m != monitor && FindTaskbar(m, out _)).ToList()
            : [];
        foreach (var o in others.Where(o => !want.Contains(o.monitor)).ToList())
        {
            others.Remove(o);
            o.Close();
        }
        foreach (var m in want.Where(m => others.All(o => o.monitor != m)))
        {
            var w = new IslandWindow(m);
            w.Show();
            others.Add(w);
        }
        foreach (var o in others) o.Relayout();
        UpdateRoles();
    }

    IEnumerable<IslandWindow> All => others.Prepend(this);

    public void OpenSettings(int tab = 0) => AppHost.ShowHub("island", tab);
    public IntPtr Handle => hwnd;

    /// <summary>Высокое окно нужно только под раскрытый остров.</summary>
    public void SetTall(bool t)
    {
        if (t == tall || hwnd == IntPtr.Zero) return;
        tall = t;
        Relayout();
    }

    /// <summary>
    /// Показать остров по горячей клавише. Поверх полноэкранной игры остров временно
    /// выходит наверх (работает с оконным/безрамочным полноэкранным режимом; эксклюзивный
    /// полноэкранный DirectX никакие окна поверх себя не пускает).
    /// </summary>
    public void Peek(double seconds = 4)
    {
        if (hwnd == IntPtr.Zero) return;
        if (fullscreen)
        {
            ShowWindow(hwnd, 4); // SW_SHOWNOACTIVATE
            SetTall(true);
        }
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        view.Peek(seconds, () =>
        {
            if (fullscreen) { view.SetHidden(true); ShowWindow(hwnd, 0); }
        });
    }

    public void Notify(string left, string right, Color color, double seconds = 2) => MainWindow.view.Notify(left, right, color, seconds);
    public void ShowActivityAll(ActivityInfo a, double seconds) { if (!MainWindow.view.Expanded) MainWindow.view.ShowActivity(a, seconds); }
    public void ShowNotification(string app, string title, string body) => MainWindow.view.ShowNotification(app, title, body);

    /// <summary>Где-то открыто полноэкранное окно (игра, видео) — не лезем с окнами.</summary>
    public bool AnyFullscreen => fullscreen || others.Any(o => o.fullscreen);

    void SetFullscreen(bool fs)
    {
        if (fs == fullscreen) return;
        fullscreen = fs;
        view.SetHidden(fs);
        ShowWindow(hwnd, fs ? 0 : 4); // SW_HIDE / SW_SHOWNOACTIVATE
        if (!fs)
        {
            // панель задач, появляясь после полноэкранного режима, встаёт поверх нас — поднимаемся обратно
            Raise();
            Later(250, Raise);
            Later(900, Raise);
        }
        Root?.UpdateRoles();
        Root?.WatchFullscreen();
    }

    void Raise()
    {
        if (hwnd == IntPtr.Zero || fullscreen) return;
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    // Выход из полноэкранного видео не меняет активное окно, а уведомление панели задач
    // приходит раньше, чем браузер уменьшит окно. Поэтому, пока где-то полноэкранный режим,
    // раз в 0,4 с проверяем, что то окно всё ещё на весь экран.
    DispatcherTimer? fsWatch;

    void WatchFullscreen()
    {
        bool any = All.Any(w => w.fullscreen);
        if (any && fsWatch == null)
        {
            fsWatch = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            fsWatch.Tick += (_, _) => CheckFullscreen();
            fsWatch.Start();
        }
        else if (!any && fsWatch != null)
        {
            fsWatch.Stop();
            fsWatch = null;
        }
    }

    void CheckFullscreen()
    {
        var fg = GetForegroundWindow();
        var all = All.ToList();
        bool ours = all.Any(o => o.hwnd == fg);
        foreach (var w in all)
        {
            if (!w.fullscreen) continue;
            bool alive = w.fsWnd != IntPtr.Zero && IsWindow(w.fsWnd) && !IsIconic(w.fsWnd) && IsFullscreen(w.fsWnd)
                         && MonitorFromWindow(w.fsWnd, 2) == w.monitor;
            bool otherHere = fg != IntPtr.Zero && fg != w.fsWnd && !ours && MonitorFromWindow(fg, 2) == w.monitor;
            if (!alive || otherHere) w.SetFullscreen(false);
        }
        WatchFullscreen();
    }

    void OnForeground(IntPtr hook, uint ev, IntPtr fg, int obj, int child, uint thread, uint time)
    {
        if (All.Any(w => w.hwnd == fg)) return;
        // Пуск, поиск, Alt+Tab — прячем каплю, чтобы не перекрывать их
        var cls = ClassName(fg);
        bool shell = cls is "Windows.UI.Core.CoreWindow" or "XamlExplorerHostIslandWindow";
        foreach (var w in All) w.OnForegroundChanged(fg, shell);
    }

    void OnForegroundChanged(IntPtr fg, bool shell)
    {
        view.SetSuppressed(shell);
        IntPtr fgMon = MonitorFromWindow(fg, 2);
        if (IsFullscreen(fg) && fgMon == monitor) { fsWnd = fg; SetFullscreen(true); }
        else if (fullscreen)
        {
            // на этом мониторе активировали другое окно — или полноэкранное окно закрылось/свернулось
            bool fsAlive = fsWnd != IntPtr.Zero && IsWindow(fsWnd) && !IsIconic(fsWnd) && IsFullscreen(fsWnd)
                           && MonitorFromWindow(fsWnd, 2) == monitor;
            if (fgMon == monitor || !fsAlive) SetFullscreen(false);
        }
        // панель задач при клике поднимается выше нас — возвращаемся наверх
        if (!fullscreen)
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    IntPtr WndProc(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        switch (msg)
        {
            case WM_APPBAR:
                if ((int)wp == ABN_FULLSCREENAPP)
                {
                    var fg = GetForegroundWindow();
                    if (lp != IntPtr.Zero) { if (IsFullscreen(fg)) foreach (var w in All) w.OnForegroundChanged(fg, false); }
                    else { CheckFullscreen(); Later(150, CheckFullscreen); }
                }
                else if ((int)wp == ABN_POSCHANGED) Later(300, Relayout);
                break;
            case WM_TRAY:
                int m = (int)(lp.ToInt64() & 0xFFFF);
                if (m == WM_LBUTTONUP) AppHost.ShowHub();
                else if (m == WM_RBUTTONUP) ShowMenu();
                break;
            case 0x0312: // WM_HOTKEY
                Hotkeys.Handle(wp.ToInt32());
                handled = true;
                break;
            case WM_CLIPBOARDUPDATE:
                view.OnClipboard();
                break;
            case WM_POWERBROADCAST:
                if ((int)wp == PBT_APMPOWERSTATUSCHANGE) view.OnPowerChanged();
                break;
            case WM_DISPLAYCHANGE:
            case WM_SETTINGCHANGE:
            case WM_DPICHANGED:
                Later(500, Relayout);
                if (primary && msg != WM_DPICHANGED) Later(1500, SyncMonitors);
                break;
            case WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(MA_NOACTIVATE);
        }
        if (taskbarCreatedMsg != 0 && msg == taskbarCreatedMsg) // перезапуск проводника
        {
            AddTray();
            RegisterAppBar();
            Later(500, Relayout);
            Later(1500, SyncMonitors);
        }
        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------ трей и меню

    void AddTray()
    {
        if (trayIcon == IntPtr.Zero) trayIcon = CreateTrayIcon();
        var d = new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = hwnd, uID = 1, uFlags = 1 | 2 | 4,
            uCallbackMessage = WM_TRAY, hIcon = trayIcon, szTip = "Taskbeat", szInfo = "", szInfoTitle = "",
        };
        Shell_NotifyIconW(0, ref d); // NIM_ADD
    }

    public void ShowMenu()
    {
        IntPtr m = CreatePopupMenu();
        AppendMenuW(m, 0, 6, L.T("Открыть Taskbeat"));
        if (AppHost.Web.CanPip) AppendMenuW(m, 0, 7, L.T("Видео в картинку-в-картинке"));
        AppendMenuW(m, 0x800, 0, null); // MF_SEPARATOR
        AppendMenuW(m, 0, 3, L.T("Настройки острова…"));
        AppendMenuW(m, 0, 4, L.T("Новое напоминание…"));
        IntPtr tm = CreatePopupMenu();
        if (Live.TimerRunning) AppendMenuW(tm, 0, 30, L.T("Остановить таймер"));
        else
        {
            foreach (int min in new[] { 1, 3, 5, 10, 15, 20, 30, 45, 60 }) AppendMenuW(tm, 0, (nuint)(100 + min), L.F("{0} мин", min));
            AppendMenuW(tm, 0x800, 0, null);
            AppendMenuW(tm, 0, 31, L.T("Помодоро 25 + 5"));
        }
        AppendMenuW(tm, 0x800, 0, null);
        AppendMenuW(tm, Live.StopwatchRunning ? 0x8u : 0u, 32, Live.StopwatchRunning ? L.T("Остановить секундомер") : L.T("Секундомер"));
        AppendMenuW(m, 0x10, (nuint)(nint)tm, L.T("Таймер")); // MF_POPUP
        AppendMenuW(m, 0, 8, L.T("Отзыв или баг…"));
        AppendMenuW(m, 0x800, 0, null);
        AppendMenuW(m, SettingsStore.Current.CollapseToStrip ? 0x8u : 0u, 5, L.T("Уходить в полоску в покое"));
        AppendMenuW(m, Autostart.Enabled ? 0x8u : 0u, 1, L.T("Запускать вместе с Windows")); // MF_CHECKED
        AppendMenuW(m, 0x800, 0, null);
        AppendMenuW(m, 0, 2, L.T("Выход"));
        GetCursorPos(out var pt);
        SetForegroundWindow(hwnd);
        int cmd = TrackPopupMenuEx(m, 0x100 | 0x2 | 0x20, pt.X, pt.Y, hwnd, IntPtr.Zero); // RETURNCMD|RIGHTBUTTON|BOTTOMALIGN
        PostMessageW(hwnd, 0, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(m);
        if (cmd == 1) Autostart.Enabled = !Autostart.Enabled;
        else if (cmd == 2) AppHost.Exit();
        else if (cmd == 6) AppHost.ShowHub();
        else if (cmd == 7) AppHost.Web.EnterPip();
        else if (cmd == 8) FeedbackWindow.Open();
        else if (cmd == 30) Live.StopTimer();
        else if (cmd == 31) Live.StartPomodoro();
        else if (cmd == 32) Live.ToggleStopwatch();
        else if (cmd > 100 && cmd <= 160) Live.StartTimer(TimeSpan.FromMinutes(cmd - 100));
        else if (cmd == 3) OpenSettings();
        else if (cmd == 4) OpenSettings(2);
        else if (cmd == 5)
        {
            SettingsStore.Current.CollapseToStrip = !SettingsStore.Current.CollapseToStrip;
            SettingsStore.Touch();
        }
    }

    public void Cleanup()
    {
        foreach (var o in others) o.Close();
        others.Clear();
        fsWatch?.Stop();
        Notifications.Stop();
        var d = new NOTIFYICONDATAW { cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(), hWnd = hwnd, uID = 1, szTip = "", szInfo = "", szInfoTitle = "" };
        Shell_NotifyIconW(2, ref d); // NIM_DELETE
        var ab = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = hwnd };
        SHAppBarMessage(ABM_REMOVE, ref ab);
        RemoveClipboardFormatListener(hwnd);
        if (fgHook != IntPtr.Zero) UnhookWinEvent(fgHook);
        if (trayIcon != IntPtr.Zero) DestroyIcon(trayIcon);
    }

    static IntPtr CreateTrayIcon()
    {
        // иконка Taskbeat из ресурсов exe (маленький размер для трея)
        var small = new IntPtr[1];
        ExtractIconExW(Environment.ProcessPath ?? "", 0, null, small, 1);
        return small[0];
    }

    delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, IntPtr rect, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT p, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr mon, int type, out uint x, out uint y);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconExW(string file, int index, IntPtr[]? large, IntPtr[]? small, uint count);
}

static class Autostart
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "Taskbeat", OldName = "CoolPlayer";

    public static bool Enabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key);
            return k?.GetValue(Name) != null;
        }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            if (value) k.SetValue(Name, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(Name, false);
            k.DeleteValue(OldName, false);
        }
    }

    /// <summary>После переименования/переноса: старая запись CoolPlayer → Taskbeat, путь — к текущему exe.</summary>
    public static void Migrate()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            bool want = k.GetValue(Name) != null || k.GetValue(OldName) != null;
            string cmd = $"\"{Environment.ProcessPath}\" --tray";
            k.DeleteValue(OldName, false);
            if (want && k.GetValue(Name) as string != cmd) k.SetValue(Name, cmd);
        }
        catch (Exception ex) { Paths.Trace("автозапуск: " + ex.Message); }
    }
}
