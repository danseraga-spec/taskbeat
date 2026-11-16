using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CoolPlayer;

/// <summary>Эквалайзер на 10 полос с пресетами. Изменения слышны сразу.</summary>
sealed class EqWindow : Window
{
    public static readonly (string key, string name, double[] g)[] Presets =
    [
        ("flat", L.T("Ровно"), [0, 0, 0, 0, 0, 0, 0, 0, 0, 0]),
        ("bass", L.T("Больше баса"), [7, 6, 5, 3, 1, 0, 0, 0, 0, 0]),
        ("bassless", L.T("Меньше баса"), [-6, -5, -4, -2, 0, 0, 0, 0, 0, 0]),
        ("vocal", L.T("Вокал"), [-3, -2, -1, 1, 4, 5, 4, 2, 0, -1]),
        ("rock", L.T("Рок"), [5, 4, 2, 0, -1, -1, 1, 3, 4, 5]),
        ("pop", L.T("Поп"), [-1, 1, 3, 4, 3, 1, 0, 1, 2, 2]),
        ("electro", L.T("Электроника"), [5, 4, 1, 0, -2, 1, 0, 2, 4, 5]),
        ("hiphop", L.T("Хип-хоп"), [6, 5, 2, 3, -1, -1, 1, 0, 2, 3]),
        ("classic", L.T("Классика"), [4, 3, 2, 1, -1, -1, 0, 2, 3, 4]),
        ("treble", L.T("Больше высоких"), [0, 0, 0, 0, 0, 1, 3, 5, 6, 7]),
        ("night", L.T("Ночной"), [-2, -1, 0, 1, 2, 2, 1, -1, -3, -5]),
    ];

    readonly Slider[] sliders = new Slider[10];
    readonly TextBlock[] values = new TextBlock[10];
    readonly CheckBox on = new() { Content = L.T("Эквалайзер включён"), FontSize = 14 };
    readonly CheckBox norm = new() { Content = L.T("Выравнивать громкость треков"), FontSize = 13.5, Margin = new Thickness(0, 14, 0, 0),
        ToolTip = L.T("Тихие треки звучат громче, громкие не «бьют» по ушам. Немного нагружает процессор.") };
    readonly WrapPanel presets = new() { Margin = new Thickness(0, 14, 0, 0) };
    readonly TextBlock note = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    readonly DispatcherTimer saveLater = new() { Interval = TimeSpan.FromSeconds(1) };
    bool sync;

    static AppSettings S => SettingsStore.Current;

    public EqWindow()
    {
        Title = L.T("Taskbeat — эквалайзер");
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
        Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(h, 20, ref dark, 4);
        };

        var root = new StackPanel { Margin = new Thickness(26, 20, 26, 22) };
        var head = new DockPanel();
        var reset = new Button { Content = L.T("Сбросить"), Padding = new Thickness(14, 6, 14, 6) };
        reset.SetResourceReference(StyleProperty, "Pill");
        reset.Click += (_, _) => ApplyPreset("flat");
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(on);
        root.Children.Add(head);

        foreach (var (key, name, _) in Presets)
        {
            var r = new RadioButton { Content = name, GroupName = "eqp", Margin = new Thickness(0, 0, 6, 6), Tag = key };
            r.SetResourceReference(StyleProperty, "PillTab");
            r.Checked += (_, _) => { if (!sync) ApplyPreset(key); };
            presets.Children.Add(r);
        }
        root.Children.Add(presets);

        var bands = new UniformGrid { Columns = 10, Margin = new Thickness(0, 16, 0, 0), Height = 230 };
        for (int i = 0; i < 10; i++)
        {
            int idx = i;
            var col = new DockPanel();
            var val = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 168)) };
            var freq = new TextBlock { FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0), Text = Freq(GraphEngine.Freqs[i]) };
            var s = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, SmallChange = 0.5, LargeChange = 3,
                TickFrequency = 0.5, IsSnapToTickEnabled = true, HorizontalAlignment = HorizontalAlignment.Center,
            };
            s.ValueChanged += (_, e) =>
            {
                values[idx].Text = Db(e.NewValue);
                if (sync) return;
                S.EqGains[idx] = e.NewValue;
                S.EqPreset = "custom";
                MarkPreset();
                AppHost.Player.EqGainsChanged();
                saveLater.Stop(); saveLater.Start();
            };
            s.MouseDoubleClick += (_, _) => s.Value = 0;
            DockPanel.SetDock(val, Dock.Top);
            DockPanel.SetDock(freq, Dock.Bottom);
            col.Children.Add(val);
            col.Children.Add(freq);
            col.Children.Add(s);
            sliders[i] = s;
            values[i] = val;
            bands.Children.Add(col);
        }
        root.Children.Add(bands);
        norm.IsChecked = S.Normalize;
        norm.Click += (_, _) =>
        {
            S.Normalize = norm.IsChecked == true;
            SettingsStore.Save();
            AppHost.Player.EqChanged();
            UpdateState();
        };
        root.Children.Add(norm);
        note.SetResourceReference(TextBlock.ForegroundProperty, "Fg3");
        root.Children.Add(note);
        Content = root;

        on.Click += (_, _) =>
        {
            S.EqEnabled = on.IsChecked == true;
            SettingsStore.Save();
            AppHost.Player.EqChanged();
            UpdateState();
        };
        saveLater.Tick += (_, _) => { saveLater.Stop(); SettingsStore.Save(); };
        Closed += (_, _) => SettingsStore.Save();
        Load();
    }

    void Load()
    {
        sync = true;
        if (S.EqGains.Length != 10) S.EqGains = new double[10];
        on.IsChecked = S.EqEnabled;
        for (int i = 0; i < 10; i++) { sliders[i].Value = S.EqGains[i]; values[i].Text = Db(S.EqGains[i]); }
        MarkPreset();
        sync = false;
        UpdateState();
    }

    void MarkPreset()
    {
        sync = true;
        foreach (RadioButton r in presets.Children) r.IsChecked = (string)r.Tag == S.EqPreset;
        sync = false;
    }

    void ApplyPreset(string key)
    {
        var p = Presets.First(x => x.key == key);
        S.EqPreset = key;
        S.EqGains = [.. p.g];
        sync = true;
        for (int i = 0; i < 10; i++) sliders[i].Value = p.g[i];
        sync = false;
        MarkPreset();
        AppHost.Player.EqGainsChanged();
        SettingsStore.Save();
    }

    void UpdateState()
    {
        bool en = S.EqEnabled;
        foreach (var s in sliders) s.Opacity = en ? 1 : 0.45;
        presets.Opacity = en ? 1 : 0.55;
        note.Text = !en ? L.T("Эквалайзер выключен — звук идёт через системный проигрыватель с минимальной нагрузкой.")
            : Perf.NoAudioEq ? L.T("Сейчас игровой режим, и в его настройках эквалайзер отключён — он заработает в классическом режиме.")
            : L.T("Работает для треков VK в Taskbeat. Двойной клик по полосе — сброс в 0. Эквалайзер немного нагружает процессор.");
    }

    static string Freq(double f) => f >= 1000 ? $"{f / 1000:0.#}k" : $"{f:0}";
    static string Db(double v) => v == 0 ? "0" : $"{(v > 0 ? "+" : "")}{v:0.#}";

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
