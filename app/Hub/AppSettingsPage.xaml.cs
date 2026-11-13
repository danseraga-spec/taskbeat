using System.Windows;
using System.Windows.Controls;

namespace CoolPlayer;

internal partial class AppSettingsPage : UserControl
{
    public AppSettingsPage()
    {
        InitializeComponent();
        var v = typeof(AppSettingsPage).Assembly.GetName().Version;
        VersionText.Text = $"Версия {v?.ToString(3)}";
        AutoStart.IsChecked = Autostart.Enabled;
        StartHidden.IsChecked = SettingsStore.Current.StartMinimized;
        Suspend.IsChecked = SettingsStore.Current.SuspendTabs;
        HotkeysOn.IsChecked = SettingsStore.Current.Hotkeys;
        UpdateMode();
        BuildGameOptions();
        BuildHotkeys();
        Hotkeys.Changed += () => Dispatcher.InvokeAsync(BuildHotkeys);
        Perf.Changed += () => Dispatcher.InvokeAsync(UpdateMode);
        AppHost.Vk.AuthChanged += () => Dispatcher.InvokeAsync(UpdateVk);
        PreviewKeyDown += CaptureKey;
        UpdateVk();
    }

    // ------------------------------------------------------------------ игровой режим

    void BuildGameOptions()
    {
        var o = SettingsStore.Current.Game;
        (string text, string hint, Func<bool> get, Action<bool> set)[] items =
        [
            ("Отключить анимации острова", "капля и пружины — остров сразу встаёт в нужную форму", () => o.IslandAnimations, v => o.IslandAnimations = v),
            ("Остановить эквалайзер на острове", "полоски замирают — ни одной перерисовки, пока играет музыка", () => o.IslandEqualizer, v => o.IslandEqualizer = v),
            ("Отключить анимации текста песен", "строки переключаются мгновенно, позиция проверяется реже", () => o.LyricsAnimations, v => o.LyricsAnimations = v),
            ("Убрать размытую обложку за текстом", "", () => o.LyricsBackdrop, v => o.LyricsBackdrop = v),
            ("Отключить эквалайзер звука", "звук идёт через аппаратный декодер без обработки", () => o.AudioEqualizer, v => o.AudioEqualizer = v),
            ("Включить режим эффективности Windows", "EcoQoS: Taskbeat работает на энергоэффективных ядрах и не мешает игре", () => o.EfficiencyMode, v => o.EfficiencyMode = v),
            ("Понизить приоритет процесса", "«ниже среднего» — игра получает процессор первой", () => o.LowPriority, v => o.LowPriority = v),
            ("Быстро усыплять вкладки", "скрытые вкладки YouTube/SoundCloud/Яндекса засыпают через 5 с вместо 30", () => o.FastTabSleep, v => o.FastTabSleep = v),
            ("Отдавать системе лишнюю память", "Taskbeat отдаёт системе неиспользуемую память, когда окно скрыто", () => o.TrimMemory, v => o.TrimMemory = v),
        ];
        GameOpts.Children.Clear();
        foreach (var (text, hint, get, set) in items)
        {
            var cb = new CheckBox { IsChecked = get(), Margin = new Thickness(0, 3, 0, 3) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = text, FontSize = 13.5 });
            if (hint.Length > 0)
            {
                var h = new TextBlock { Text = hint, FontSize = 12, TextWrapping = TextWrapping.Wrap };
                h.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
                sp.Children.Add(h);
            }
            cb.Content = sp;
            cb.Click += (_, _) => { set(cb.IsChecked == true); Perf.OptionsChanged(); };
            GameOpts.Children.Add(cb);
        }
    }

    // ------------------------------------------------------------------ горячие клавиши

    Hotkeys.Command? capturing;
    Button? captureBtn;

    void BuildHotkeys()
    {
        if (capturing != null) return;
        HotkeyRows.Children.Clear();
        HotkeyBox.Opacity = SettingsStore.Current.Hotkeys ? 1 : 0.45;
        foreach (var a in Hotkeys.All)
        {
            var b = Hotkeys.Get(a);
            var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition());
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var on = new CheckBox { IsChecked = b.Enabled, VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 };
            on.Click += (_, _) => { b.Enabled = on.IsChecked == true; Hotkeys.Set(a, b); };
            var what = new TextBlock { Text = a.What, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 12, 0) };
            if (!b.Enabled) what.Opacity = 0.5;

            var key = new Button
            {
                Content = Hotkeys.Format(b), MinWidth = 190, Padding = new Thickness(12, 6, 12, 6),
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI Variable Text, Segoe UI"),
                ToolTip = "Нажмите и зажмите новое сочетание",
            };
            key.SetResourceReference(StyleProperty, "Pill");
            key.Click += (_, _) => StartCapture(a, key);
            key.LostKeyboardFocus += (_, _) => { if (captureBtn == key) StopCapture(); };

            var reset = new Button { Content = "", ToolTip = "По умолчанию", Margin = new Thickness(6, 0, 0, 0), Width = 32, Height = 32, FontSize = 13 };
            reset.SetResourceReference(StyleProperty, "Icon");
            reset.Click += (_, _) => Hotkeys.Reset(a);

            Grid.SetColumn(what, 1);
            Grid.SetColumn(key, 2);
            Grid.SetColumn(reset, 3);
            row.Children.Add(on);
            row.Children.Add(what);
            row.Children.Add(key);
            row.Children.Add(reset);

            string? warn = Hotkeys.Failed.Contains(a.Key) ? "Сочетание занято другой программой — выберите другое"
                : b.Enabled ? Hotkeys.Warning(b, a) : null;
            if (warn != null)
            {
                var w = new TextBlock
                {
                    Text = "⚠ " + warn, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 3, 0, 0),
                    Foreground = new System.Windows.Media.SolidColorBrush(Hotkeys.Failed.Contains(a.Key)
                        ? System.Windows.Media.Color.FromRgb(255, 105, 97) : System.Windows.Media.Color.FromRgb(255, 159, 10)),
                };
                Grid.SetRow(w, 1);
                Grid.SetColumnSpan(w, 4);
                row.Children.Add(w);
            }
            HotkeyRows.Children.Add(row);
        }
    }

    void StartCapture(Hotkeys.Command a, Button btn)
    {
        StopCapture();
        capturing = a;
        captureBtn = btn;
        btn.Content = "Нажмите сочетание…";
        btn.SetResourceReference(StyleProperty, "PillAccent");
        Hotkeys.Pause(); // чтобы нажатие не сработало как горячая клавиша
        btn.Focus();
    }

    void StopCapture()
    {
        if (capturing == null) return;
        capturing = null;
        captureBtn = null;
        Hotkeys.Apply(); // перестраивает список
    }

    void CaptureKey(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (capturing is not { } a || captureBtn == null) return;
        e.Handled = true;
        var k = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var mods = System.Windows.Input.Keyboard.Modifiers;
        bool win = System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.LWin) || System.Windows.Input.Keyboard.IsKeyDown(System.Windows.Input.Key.RWin);
        uint m = (mods.HasFlag(System.Windows.Input.ModifierKeys.Control) ? Hotkeys.CTRL : 0)
               | (mods.HasFlag(System.Windows.Input.ModifierKeys.Alt) ? Hotkeys.ALT : 0)
               | (mods.HasFlag(System.Windows.Input.ModifierKeys.Shift) ? Hotkeys.SHIFT : 0)
               | (win ? Hotkeys.WIN : 0);

        if (k is System.Windows.Input.Key.LeftCtrl or System.Windows.Input.Key.RightCtrl or System.Windows.Input.Key.LeftAlt
            or System.Windows.Input.Key.RightAlt or System.Windows.Input.Key.LeftShift or System.Windows.Input.Key.RightShift
            or System.Windows.Input.Key.LWin or System.Windows.Input.Key.RWin)
        {
            captureBtn.Content = Hotkeys.Format(new HotkeyBinding { Mods = m, Vk = 0 }) is "—" && m != 0
                ? string.Join(" + ", new[] { (m & Hotkeys.WIN) != 0 ? "Win" : null, (m & Hotkeys.CTRL) != 0 ? "Ctrl" : null,
                    (m & Hotkeys.ALT) != 0 ? "Alt" : null, (m & Hotkeys.SHIFT) != 0 ? "Shift" : null }.Where(x => x != null)) + " + …"
                : "Нажмите сочетание…";
            return;
        }
        if (m == 0 && k == System.Windows.Input.Key.Escape) { StopCapture(); return; }
        if (m == 0 && k == System.Windows.Input.Key.Back) { capturing = null; captureBtn = null; Hotkeys.Reset(a); return; }

        uint vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(k);
        var b = Hotkeys.Get(a);
        var nb = new HotkeyBinding { Enabled = true, Mods = m, Vk = vk };
        capturing = null;
        captureBtn = null;
        Hotkeys.Set(a, nb);
    }

    bool syncing;

    void UpdateMode()
    {
        syncing = true;
        (Perf.Game ? ModeGame : ModeClassic).IsChecked = true;
        syncing = false;
    }

    void ModeChecked(object s, RoutedEventArgs e)
    {
        if (syncing || !IsInitialized) return;
        bool game = (string)((FrameworkElement)s).Tag == "game";
        if (game != Perf.Game) Perf.Set(game);
    }

    void HotkeysClick(object s, RoutedEventArgs e)
    {
        SettingsStore.Current.Hotkeys = HotkeysOn.IsChecked == true;
        SettingsStore.Save();
        Hotkeys.Apply();
    }

    async void UpdateVk()
    {
        if (!AppHost.Vk.LoggedIn)
        {
            VkName.Text = "Не выполнен вход";
            VkHint.Text = "Войдите, чтобы слушать свою музыку, рекомендации и плейлисты VK.";
            VkBtn.Content = "Войти";
            return;
        }
        VkBtn.Content = "Выйти";
        VkHint.Text = "Ключ доступа хранится зашифрованным средствами Windows.";
        VkName.Text = $"id{AppHost.Vk.UserId}";
        try { VkName.Text = await AppHost.Vk.UserNameAsync(); } catch { }
    }

    void VkClick(object s, RoutedEventArgs e)
    {
        if (AppHost.Vk.LoggedIn) AppHost.Vk.Logout();
        else new VkLoginWindow { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    void AutoStartClick(object s, RoutedEventArgs e) => Autostart.Enabled = AutoStart.IsChecked == true;

    void StartHiddenClick(object s, RoutedEventArgs e)
    {
        SettingsStore.Current.StartMinimized = StartHidden.IsChecked == true;
        SettingsStore.Save();
    }

    void SuspendClick(object s, RoutedEventArgs e)
    {
        SettingsStore.Current.SuspendTabs = Suspend.IsChecked == true;
        SettingsStore.Save();
    }
}
