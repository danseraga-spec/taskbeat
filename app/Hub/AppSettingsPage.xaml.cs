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
        AutoUpdate.IsChecked = SettingsStore.Current.AutoUpdateCheck;
        UpdateIntegrations();
        BuildLook();
        LastFm.Changed += () => Dispatcher.InvokeAsync(UpdateIntegrations);
        AskCrash.IsChecked = SettingsStore.Current.AskCrashReports;
        InstallBtn.Visibility = Installer.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        InstallBtn.ToolTip = L.T("Скопировать в папку программ, добавить в меню «Пуск» и в список приложений Windows");
        if (Updater.Available is { } a) UpdateStatus.Text = L.F("Доступна версия {0}", a.Version.ToString(3));
    }

    // ------------------------------------------------------------------ оформление

    bool lookSync;

    void BuildLook()
    {
        lookSync = true;
        var s = SettingsStore.Current;
        foreach (var (k, n) in new[] { ("dark", L.T("Тёмная")), ("light", L.T("Светлая")), ("system", L.T("Как в Windows")) })
            ThemeBox.Items.Add(new ComboBoxItem { Content = n, Tag = k, IsSelected = s.Theme == k });
        foreach (var (k, n) in new[] { ("auto", L.T("Как в системе") + " / System"), ("ru", L.T("Русский")), ("en", "English") })
            LangBox.Items.Add(new ComboBoxItem { Content = n, Tag = k, IsSelected = s.Language == k });
        foreach (var (key, name, hex) in Themes.Accents)
        {
            var c = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
            var b = new Button
            {
                Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 6), ToolTip = L.T(name), Tag = hex, Cursor = System.Windows.Input.Cursors.Hand,
                Template = SwatchTemplate(),
                Background = new System.Windows.Media.SolidColorBrush(c),
                BorderThickness = new Thickness(string.Equals(s.AccentColor, hex, StringComparison.OrdinalIgnoreCase) ? 3 : 0),
            };
            b.SetResourceReference(BorderBrushProperty, "Fg");
            b.Click += (_, _) =>
            {
                SettingsStore.Current.AccentColor = hex;
                SettingsStore.Touch();
                foreach (Button x in AccentPanel.Children) x.BorderThickness = new Thickness((string)x.Tag == hex ? 3 : 0);
                RestartRow.Visibility = Visibility.Visible;
            };
            AccentPanel.Children.Add(b);
        }
        StripSlider.Value = s.StripThickness;
        StripValue.Text = $"{s.StripThickness}";
        StripAccent.IsChecked = s.StripColor == "accent";
        lookSync = false;
    }

    static ControlTemplate SwatchTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(15));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        t.VisualTree = border;
        return t;
    }

    void ThemeChanged(object s, SelectionChangedEventArgs e)
    {
        if (lookSync || ThemeBox.SelectedItem is not ComboBoxItem { Tag: string k }) return;
        SettingsStore.Current.Theme = k;
        SettingsStore.Save();
        RestartRow.Visibility = Visibility.Visible;
    }

    void LangChanged(object s, SelectionChangedEventArgs e)
    {
        if (lookSync || LangBox.SelectedItem is not ComboBoxItem { Tag: string k }) return;
        SettingsStore.Current.Language = k;
        SettingsStore.Save();
        RestartRow.Visibility = Visibility.Visible;
    }

    void RestartClick(object s, RoutedEventArgs e) => AppHost.Restart();

    void StripChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StripValue == null) return;
        StripValue.Text = $"{(int)e.NewValue}";
        if (lookSync) return;
        SettingsStore.Current.StripThickness = (int)e.NewValue;
        SettingsStore.Touch();
    }

    void StripAccentClick(object s, RoutedEventArgs e)
    {
        SettingsStore.Current.StripColor = StripAccent.IsChecked == true ? "accent" : "cover";
        SettingsStore.Touch();
    }

    void UpdateIntegrations()
    {
        LastFmConnect.IsEnabled = LastFm.Configured;
        LastFmConnect.Content = LastFm.Connected ? L.T("Отключить") : L.T("Подключить");
        LastFmText.Text = !LastFm.Configured ? L.T("В этой сборке Last.fm не настроен.")
            : LastFm.Connected ? L.F("Скробблинг включён: {0}", LastFm.User)
            : L.T("Отмечать прослушанные треки в вашем профиле Last.fm.");
        Discord.IsEnabled = DiscordPresence.Configured;
        Discord.IsChecked = SettingsStore.Current.DiscordPresence;
        DiscordText.Text = DiscordPresence.Configured
            ? L.T("Название трека и исполнитель появятся в профиле, пока Discord для компьютера запущен.")
            : L.T("В этой сборке Discord не настроен.");
    }

    CancellationTokenSource? lastFmWait;

    async void LastFmConnectClick(object s, RoutedEventArgs e)
    {
        if (lastFmWait != null) { lastFmWait.Cancel(); return; } // «Отмена» во время ожидания
        if (LastFm.Connected) { LastFm.Disconnect(); return; }
        LastFmConnect.IsEnabled = false;
        var err = await LastFm.BeginConnectAsync();
        LastFmConnect.IsEnabled = true;
        if (err != null) { LastFmText.Text = err; return; }
        LastFmText.Text = L.T("В браузере открылся Last.fm — нажмите там «Разрешить». Taskbeat подключится сам.");
        LastFmConnect.Content = L.T("Отмена");
        using var cts = lastFmWait = new CancellationTokenSource();
        err = await LastFm.WaitConnectAsync(cts.Token);
        lastFmWait = null;
        UpdateIntegrations();
        if (!string.IsNullOrEmpty(err)) LastFmText.Text = err;
    }

    void DiscordClick(object s, RoutedEventArgs e)
    {
        SettingsStore.Current.DiscordPresence = Discord.IsChecked == true;
        SettingsStore.Save();
        _ = DiscordPresence.UpdateAsync(force: true);
    }

    void InstallClick(object s, RoutedEventArgs e) => Installer.Install();

    void AutoUpdateClick(object s, RoutedEventArgs e) { SettingsStore.Current.AutoUpdateCheck = AutoUpdate.IsChecked == true; SettingsStore.Save(); }
    void AskCrashClick(object s, RoutedEventArgs e) { SettingsStore.Current.AskCrashReports = AskCrash.IsChecked == true; SettingsStore.Save(); }

    async void CheckUpdatesClick(object s, RoutedEventArgs e)
    {
        CheckUpdates.IsEnabled = false;
        UpdateStatus.Text = L.T("Проверяю…");
        var r = await Updater.CheckAsync(notify: false);
        CheckUpdates.IsEnabled = true;
        if (r != null) { UpdateStatus.Text = L.F("Доступна версия {0}", r.Version.ToString(3)); UpdateWindow.Offer(manual: true); }
        else UpdateStatus.Text = L.F("У вас последняя версия ({0})", Updater.Current.ToString(3));
    }

    // ------------------------------------------------------------------ игровой режим

    void BuildGameOptions()
    {
        var o = SettingsStore.Current.Game;
        (string text, string hint, Func<bool> get, Action<bool> set)[] items =
        [
            (L.T("Отключить анимации острова"), L.T("капля и пружины — остров сразу встаёт в нужную форму"), () => o.IslandAnimations, v => o.IslandAnimations = v),
            (L.T("Остановить эквалайзер на острове"), L.T("полоски замирают — ни одной перерисовки, пока играет музыка"), () => o.IslandEqualizer, v => o.IslandEqualizer = v),
            (L.T("Отключить анимации текста песен"), L.T("строки переключаются мгновенно, позиция проверяется реже"), () => o.LyricsAnimations, v => o.LyricsAnimations = v),
            (L.T("Убрать размытую обложку за текстом"), "", () => o.LyricsBackdrop, v => o.LyricsBackdrop = v),
            (L.T("Отключить эквалайзер звука"), L.T("звук идёт через аппаратный декодер без обработки"), () => o.AudioEqualizer, v => o.AudioEqualizer = v),
            (L.T("Включить режим эффективности Windows"), L.T("EcoQoS: Taskbeat работает на энергоэффективных ядрах и не мешает игре"), () => o.EfficiencyMode, v => o.EfficiencyMode = v),
            (L.T("Понизить приоритет процесса"), L.T("«ниже среднего» — игра получает процессор первой"), () => o.LowPriority, v => o.LowPriority = v),
            (L.T("Быстро усыплять вкладки"), L.T("скрытые вкладки YouTube/SoundCloud/Яндекса засыпают через 5 с вместо 30"), () => o.FastTabSleep, v => o.FastTabSleep = v),
            (L.T("Отдавать системе лишнюю память"), L.T("Taskbeat отдаёт системе неиспользуемую память, когда окно скрыто"), () => o.TrimMemory, v => o.TrimMemory = v),
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
                ToolTip = L.T("Нажмите и зажмите новое сочетание"),
            };
            key.SetResourceReference(StyleProperty, "Pill");
            key.Click += (_, _) => StartCapture(a, key);
            key.LostKeyboardFocus += (_, _) => { if (captureBtn == key) StopCapture(); };

            var reset = new Button { Content = "", ToolTip = L.T("По умолчанию"), Margin = new Thickness(6, 0, 0, 0), Width = 32, Height = 32, FontSize = 13 };
            reset.SetResourceReference(StyleProperty, "Icon");
            reset.Click += (_, _) => Hotkeys.Reset(a);

            Grid.SetColumn(what, 1);
            Grid.SetColumn(key, 2);
            Grid.SetColumn(reset, 3);
            row.Children.Add(on);
            row.Children.Add(what);
            row.Children.Add(key);
            row.Children.Add(reset);

            string? warn = Hotkeys.Failed.Contains(a.Key) ? L.T("Сочетание занято другой программой — выберите другое")
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
        btn.Content = L.T("Нажмите сочетание…");
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
                : L.T("Нажмите сочетание…");
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
            VkName.Text = L.T("Не выполнен вход");
            VkHint.Text = L.T("Войдите, чтобы слушать свою музыку, рекомендации и плейлисты VK.");
            VkBtn.Content = L.T("Войти");
            return;
        }
        VkBtn.Content = L.T("Выйти");
        VkHint.Text = L.T("Ключ доступа хранится зашифрованным средствами Windows.");
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
