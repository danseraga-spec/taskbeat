using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CoolPlayer;

/// <summary>
/// Отзывы и баг-репорты: текст + скриншот уходят на посредник (Cloudflare Worker),
/// а он пересылает их в Telegram-бота. Токена бота в приложении нет.
/// </summary>
static class Feedback
{
    /// <summary>Адрес посредника из relay/README.md, например https://taskbeat-feedback.имя.workers.dev/feedback</summary>
    public const string Endpoint = "https://taskbeat.danseraga.workers.dev/feedback";

    public static bool Configured => Endpoint.Length > 0;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    public static async Task<(bool ok, string message)> SendAsync(string kind, string text, string contact, string? app, byte[]? jpeg)
    {
        // Cloudflare не разбирает multipart в виде «по умолчанию» из .NET (граница в кавычках,
        // имена полей без кавычек) — собираем строго по стандарту
        string boundary = "taskbeat" + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent(boundary);
        form.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=" + boundary);
        void Add(HttpContent part, string name, string? file = null)
        {
            part.Headers.ContentDisposition = new("form-data") { Name = $"\"{name}\"", FileName = file == null ? null : $"\"{file}\"" };
            form.Add(part);
        }
        Add(new StringContent(kind), "kind");
        Add(new StringContent(text), "text");
        Add(new StringContent(contact), "contact");
        if (app != null) Add(new StringContent(app), "app");
        if (jpeg != null)
        {
            var img = new ByteArrayContent(jpeg);
            img.Headers.ContentType = new("image/jpeg");
            Add(img, "image", "screenshot.jpg");
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        req.Headers.Add("X-Taskbeat", "1");
        try
        {
            using var resp = await Http.SendAsync(req);
            return (int)resp.StatusCode switch
            {
                200 => (true, L.T("Спасибо! Отправлено.")),
                429 => (false, L.T("Слишком много отправок подряд — попробуйте через час.")),
                413 => (false, L.T("Скриншот слишком большой.")),
                _ => (false, $"Не удалось отправить ({(int)resp.StatusCode}). Попробуйте позже."),
            };
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            return (false, L.T("Нет связи с сервером. Проверьте интернет."));
        }
    }

    public static string AppInfo()
    {
        var v = typeof(Feedback).Assembly.GetName().Version?.ToString(3);
        return $"Taskbeat {v} · Windows {Environment.OSVersion.Version} · {(Perf.Game ? L.T("игровой") : L.T("классический"))} режим";
    }
}

sealed class FeedbackWindow : Window
{
    static readonly Brush Fg2 = new SolidColorBrush(Color.FromRgb(150, 150, 158));

    readonly RadioButton bug = Tab(L.T("Баг")), idea = Tab(L.T("Идея")), review = Tab(L.T("Отзыв"));
    readonly TextBox text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 150, FontSize = 14, Padding = new Thickness(10, 8, 10, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxLength = 3500 };
    readonly TextBox contact = new() { Height = 36, FontSize = 14, Padding = new Thickness(10, 7, 10, 0), MaxLength = 200 };
    readonly CheckBox withInfo = new() { Content = L.T("Приложить версию Taskbeat и Windows"), IsChecked = true, Margin = new Thickness(0, 12, 0, 0) };
    readonly Image preview = new() { MaxHeight = 130, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    readonly Border previewBox = new() { Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromRgb(50, 50, 56)), Padding = new Thickness(6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    readonly TextBlock status = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button send = new() { Content = L.T("Отправить"), Height = 38, Margin = new Thickness(0, 14, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(26, 0, 26, 0) };
    byte[]? jpeg;

    public FeedbackWindow()
    {
        Title = L.T("Taskbeat — отзыв или баг");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 26) };
        root.Children.Add(new TextBlock { Text = L.T("Отзыв или баг"), FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = L.T("Сообщение придёт разработчику. Опишите, что случилось или что хотелось бы улучшить."), Foreground = Fg2, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        tabs.Children.Add(bug); tabs.Children.Add(idea); tabs.Children.Add(review);
        bug.IsChecked = true;
        root.Children.Add(tabs);

        root.Children.Add(text);
        root.Children.Add(Label(L.T("Как с вами связаться, если нужен ответ (необязательно)")));
        root.Children.Add(contact);

        root.Children.Add(Label(L.T("Скриншот (необязательно)")));
        var shots = new StackPanel { Orientation = Orientation.Horizontal };
        shots.Children.Add(Pill(L.T("Снимок экрана"), async () => await CaptureAsync()));
        shots.Children.Add(Pill(L.T("Из буфера"), PasteImage));
        shots.Children.Add(Pill(L.T("Файл…"), PickFile));
        root.Children.Add(shots);
        var pv = new StackPanel();
        pv.Children.Add(preview);
        var remove = new Button { Content = L.T("Убрать"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        remove.SetResourceReference(StyleProperty, "Pill");
        remove.Click += (_, _) => SetImage(null);
        pv.Children.Add(remove);
        previewBox.Child = pv;
        root.Children.Add(previewBox);

        root.Children.Add(withInfo);
        root.Children.Add(status);
        send.SetResourceReference(StyleProperty, "PillAccent");
        send.Click += async (_, _) => await SendAsync();
        root.Children.Add(send);

        if (!Feedback.Configured)
        {
            send.IsEnabled = false;
            Status(L.T("Отправка отзывов в этой сборке не настроена."), false);
        }
        Content = root;
        Loaded += (_, _) => text.Focus();
    }

    static RadioButton Tab(string t)
    {
        var r = new RadioButton { Content = t, GroupName = "fbkind", Margin = new Thickness(0, 0, 6, 0) };
        r.SetResourceReference(StyleProperty, "PillTab");
        return r;
    }

    static TextBlock Label(string t) => new() { Text = t, Foreground = Fg2, FontSize = 12, Margin = new Thickness(0, 12, 0, 4), TextWrapping = TextWrapping.Wrap };

    static Button Pill(string t, Action a)
    {
        var b = new Button { Content = t, Margin = new Thickness(0, 0, 8, 0) };
        b.SetResourceReference(StyleProperty, "Pill");
        b.Click += (_, _) => a();
        return b;
    }

    void Status(string s, bool ok) { status.Text = s; status.Foreground = ok ? new SolidColorBrush(Color.FromRgb(120, 220, 140)) : new SolidColorBrush(Color.FromRgb(255, 140, 120)); }

    // ------------------------------------------------------------------ скриншот

    void SetImage(BitmapSource? src)
    {
        if (src == null) { jpeg = null; preview.Source = null; previewBox.Visibility = Visibility.Collapsed; return; }
        // уменьшаем большие снимки и перекодируем в JPEG — заодно отбрасываются метаданные файла
        double k = Math.Min(1, 2560.0 / Math.Max(src.PixelWidth, src.PixelHeight));
        BitmapSource img = k < 1 ? new TransformedBitmap(src, new ScaleTransform(k, k)) : src;
        if (img.Format != PixelFormats.Bgr32 && img.Format != PixelFormats.Bgra32 && img.Format != PixelFormats.Pbgra32)
            img = new FormatConvertedBitmap(img, PixelFormats.Bgra32, null, 0);
        var enc = new JpegBitmapEncoder { QualityLevel = 88 };
        enc.Frames.Add(BitmapFrame.Create(img));
        using var ms = new MemoryStream();
        enc.Save(ms);
        jpeg = ms.ToArray();
        img.Freeze();
        preview.Source = img;
        previewBox.Visibility = Visibility.Visible;
        status.Text = "";
    }

    async Task CaptureAsync()
    {
        // снимаем монитор, на котором окно плеера; само окно отзыва на время прячем
        var owner = Owner ?? this;
        var mon = MonitorFromWindow(new WindowInteropHelper(owner).Handle, 2);
        Opacity = 0;
        await Task.Delay(250);
        try
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfoW(mon, ref mi);
            SetImage(Grab(mi.rcMonitor));
        }
        catch (Exception ex) { Program.Log(ex); Status("Не удалось сделать снимок.", false); }
        finally { Opacity = 1; Activate(); }
    }

    void PasteImage()
    {
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) SetImage(img);
            else Status(L.T("В буфере обмена нет картинки."), false);
        }
        catch { Status(L.T("Не удалось прочитать буфер обмена."), false); }
    }

    void PickFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Изображения|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp") };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(dlg.FileName);
            bi.EndInit();
            SetImage(bi);
        }
        catch { Status(L.T("Не удалось открыть картинку."), false); }
    }

    // ------------------------------------------------------------------ отправка

    async Task SendAsync()
    {
        if (text.Text.Trim().Length < 3) { Status(L.T("Напишите хотя бы пару слов."), false); text.Focus(); return; }
        send.IsEnabled = false;
        Status(L.T("Отправляю…"), true);
        string kind = bug.IsChecked == true ? "bug" : idea.IsChecked == true ? "idea" : "review";
        var (ok, msg) = await Feedback.SendAsync(kind, text.Text.Trim(), contact.Text.Trim(), withInfo.IsChecked == true ? Feedback.AppInfo() : null, jpeg);
        Status(msg, ok);
        if (ok) { await Task.Delay(1200); Close(); }
        else send.IsEnabled = true;
    }

    public static void Open()
    {
        var w = new FeedbackWindow();
        if (AppHost.Hub is { IsVisible: true } hub) w.Owner = hub;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        w.Show();
        w.Activate();
    }

    // ------------------------------------------------------------------ GDI

    static BitmapSource Grab(Native.RECT r)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, w, h, screen, r.Left, r.Top, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public Native.RECT rcMonitor, rcWork; public int dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
}
