using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;

namespace CoolPlayer;

/// <summary>
/// Вход в VK: по QR-коду, по логину и паролю (пользователь вводит их сам)
/// Taskbeat отправляет их только на oauth.vk.com и не сохраняет — хранится лишь выданный токен.
/// Поддерживаются код подтверждения (SMS / приложение), капча и проверка в браузере,
/// если VK её потребует.
/// </summary>
sealed class VkLoginWindow : Window
{
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(236, 236, 240));
    static readonly Brush Fg2 = new SolidColorBrush(Color.FromRgb(150, 150, 158));

    readonly TextBox login = Field();
    readonly PasswordBox password = new() { Height = 36, FontSize = 14, Padding = new Thickness(10, 7, 10, 0) };
    readonly TextBox code = Field();
    readonly TextBox captcha = Field();
    readonly Image captchaImg = new() { Height = 50, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 6) };
    readonly StackPanel codeBox = new() { Visibility = Visibility.Collapsed };
    readonly StackPanel captchaBox = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock codeHint = Label("");
    readonly TextBlock message = new() { Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    readonly Button go = new() { Content = L.T("Войти"), Height = 38, Margin = new Thickness(0, 16, 0, 0), IsDefault = true };
    readonly Button sms = new() { Content = L.T("Прислать код по SMS"), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
    string? captchaSid;
    bool busy;

    public VkLoginWindow()
    {
        Title = L.T("Taskbeat — вход в VK");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

        var outer = new StackPanel { Margin = new Thickness(28, 22, 28, 26) };
        outer.Children.Add(new TextBlock { Text = L.T("Вход в VK"), Foreground = Fg, FontSize = 22, FontWeight = FontWeights.SemiBold });
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
        tabs.Children.Add(qrTab);
        tabs.Children.Add(pwdTab);
        tabs.Children.Add(webTab);
        outer.Children.Add(tabs);
        outer.Children.Add(new TextBlock
        {
            Text = L.T("Неофициальный способ входа: VK может отключить его или ограничить аккаунт. Используйте на свой страх и риск."),
            Foreground = new SolidColorBrush(Color.FromRgb(201, 166, 74)), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        // ---- QR
        qrPanel.Children.Add(new TextBlock
        {
            Text = L.T("Отсканируйте камерой телефона, где открыт VK, и подтвердите вход."),
            Foreground = Fg2, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 12),
        });
        qrPanel.Children.Add(new Border
        {
            Background = Brushes.White, CornerRadius = new CornerRadius(14), Padding = new Thickness(10),
            Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center, Child = qrImage,
        });
        qrPanel.Children.Add(qrState);
        qrPanel.Children.Add(Label(L.T("Код, который VK показал на телефоне")));
        var codeRow = new DockPanel();
        DockPanel.SetDock(qrCodeGo, Dock.Right);
        codeRow.Children.Add(qrCodeGo);
        codeRow.Children.Add(qrCode);
        qrPanel.Children.Add(codeRow);
        qrCodeGo.SetResourceReference(StyleProperty, "PillAccent");
        qrCodeGo.Click += async (_, _) => await ValidateQrCodeAsync();
        qrCode.KeyDown += async (_, e) => { if (e.Key == Key.Enter) await ValidateQrCodeAsync(); };
        var qrButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        qrButtons.Children.Add(qrRefresh);
        qrButtons.Children.Add(qrCopy);
        qrPanel.Children.Add(qrButtons);
        qrRefresh.SetResourceReference(StyleProperty, "Pill");
        qrCopy.SetResourceReference(StyleProperty, "Pill");
        qrRefresh.Click += async (_, _) => await StartQrAsync();
        qrCopy.Click += (_, _) => { if (qr != null) { try { Clipboard.SetText(qr.Url); qrState.Text = L.T("Ссылка скопирована — откройте её на телефоне."); } catch { } } };
        outer.Children.Add(qrPanel);

        // ---- пароль
        var p = new StackPanel { Visibility = Visibility.Collapsed };
        pwdPanel = p;
        p.Children.Add(new TextBlock
        {
            Text = L.T("Логин и пароль уходят только на oauth.vk.com и нигде не сохраняются — Taskbeat хранит лишь зашифрованный ключ доступа."),
            Foreground = Fg2, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6),
        });
        p.Children.Add(Label(L.T("Телефон или почта")));
        p.Children.Add(login);
        p.Children.Add(Label(L.T("Пароль")));
        p.Children.Add(password);

        codeBox.Children.Add(codeHint);
        codeBox.Children.Add(code);
        codeBox.Children.Add(sms);
        p.Children.Add(codeBox);

        captchaBox.Children.Add(Label(L.T("Введите символы с картинки")));
        captchaBox.Children.Add(captchaImg);
        captchaBox.Children.Add(captcha);
        p.Children.Add(captchaBox);

        p.Children.Add(message);
        go.SetResourceReference(StyleProperty, "PillAccent");
        p.Children.Add(go);
        outer.Children.Add(p);

        // ---- запасной вход через страницу VK (старый способ)
        webPanel.Children.Add(new TextBlock
        {
            Text = L.T("Запасной вариант: откроется официальная страница входа VK, логин и пароль вводятся там. ") +
                   L.T("VK иногда не выдаёт таким способом доступ к музыке — тогда используйте QR-код или пароль."),
            Foreground = Fg2, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 6),
        });
        webPanel.Children.Add(webMsg);
        var webGo = new Button { Content = L.T("Открыть страницу VK"), Height = 38, Margin = new Thickness(0, 16, 0, 0) };
        webGo.SetResourceReference(StyleProperty, "PillAccent");
        webGo.Click += async (_, _) =>
        {
            webMsg.Text = "";
            var t = await BrowserCheck.RunAsync(this, VkApi.AuthUrl);
            if (t is { } ok) { await AppHost.Vk.SetTokenAsync(ok.token, ok.uid); DialogResult = true; }
            else webMsg.Text = L.T("Вход не завершён.");
        };
        webPanel.Children.Add(webGo);
        outer.Children.Add(webPanel);
        Content = outer;

        go.Click += async (_, _) => await SubmitAsync(false);
        sms.Click += async (_, _) => await SubmitAsync(true);
        qrTab.Checked += async (_, _) => { Show(qrPanel); go.IsDefault = false; await StartQrAsync(); };
        pwdTab.Checked += (_, _) => { Show(pwdPanel); go.IsDefault = true; poll.Stop(); login.Focus(); };
        webTab.Checked += (_, _) => { Show(webPanel); go.IsDefault = false; poll.Stop(); };
        poll.Tick += async (_, _) => await PollAsync();
        Loaded += (_, _) => qrTab.IsChecked = true;
        Closed += (_, _) => poll.Stop();
    }

    // ------------------------------------------------------------------ QR

    readonly RadioButton qrTab = Tab(L.T("QR-код"));
    readonly RadioButton pwdTab = Tab(L.T("Логин и пароль"));
    readonly RadioButton webTab = Tab(L.T("Через сайт"));
    readonly StackPanel webPanel = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock webMsg = new() { Foreground = new SolidColorBrush(Color.FromRgb(255, 120, 120)), FontSize = 13, TextWrapping = TextWrapping.Wrap };

    void Show(StackPanel panel)
    {
        foreach (var x in new[] { qrPanel, pwdPanel, webPanel })
            x.Visibility = x == panel ? Visibility.Visible : Visibility.Collapsed;
    }
    readonly StackPanel qrPanel = new();
    StackPanel pwdPanel = null!;
    readonly Image qrImage = new() { Stretch = Stretch.Uniform };
    readonly TextBlock qrState = new() { Foreground = Fg2, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
    readonly Button qrRefresh = new() { Content = L.T("Новый код"), Margin = new Thickness(0, 0, 8, 0) };
    readonly Button qrCopy = new() { Content = L.T("Скопировать ссылку") };
    readonly System.Windows.Threading.DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly TextBox qrCode = Field();
    readonly Button qrCodeGo = new() { Content = L.T("Подтвердить"), Margin = new Thickness(8, 0, 0, 0) };
    VkApi.QrCode? qr;

    async Task ValidateQrCodeAsync()
    {
        if (qr == null || qrCode.Text.Trim().Length == 0) return;
        qrCodeGo.IsEnabled = false;
        try
        {
            var r = await VkApi.QrValidateAsync(qr, qrCode.Text);
            Paths.Trace("vk-qr: код принят, поля=" +
                (r.ValueKind == System.Text.Json.JsonValueKind.Object ? string.Join(",", r.EnumerateObject().Select(p => p.Name)) : r.ValueKind.ToString()));
            qrState.Text = L.T("Код принят — подтвердите вход на телефоне, если VK попросит.");
            if (!poll.IsEnabled) poll.Start();
            await PollAsync();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            qrState.Text = ex is VkException ? ex.Message : L.T("Не удалось отправить код.");
        }
        finally { qrCodeGo.IsEnabled = true; }
    }
    DateTime qrUntil;
    bool polling;

    static RadioButton Tab(string text)
    {
        var r = new RadioButton { Content = text, GroupName = "vklogin", Margin = new Thickness(0, 0, 6, 0) };
        r.SetResourceReference(StyleProperty, "PillTab");
        return r;
    }

    async Task StartQrAsync()
    {
        poll.Stop();
        qr = null;
        qrImage.Source = null;
        qrState.Text = L.T("Получаю QR-код…");
        try
        {
            qr = await VkApi.QrStartAsync();
            qrUntil = DateTime.Now.AddSeconds(Math.Max(60, qr.ExpiresIn));
            using var gen = new QRCoder.QRCodeGenerator();
            using var data = gen.CreateQrCode(qr.Url, QRCoder.QRCodeGenerator.ECCLevel.M);
            var png = new QRCoder.PngByteQRCode(data).GetGraphic(10);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new System.IO.MemoryStream(png);
            bmp.EndInit();
            bmp.Freeze();
            qrImage.Source = bmp;
            RenderOptions.SetBitmapScalingMode(qrImage, BitmapScalingMode.NearestNeighbor);
            qrState.Text = L.T("Ждём сканирования…");
            poll.Start();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            qrState.Text = (ex is VkException ? ex.Message : L.T("Не удалось получить QR-код.")) + L.T(" Попробуйте вход по паролю.");
        }
    }

    async Task PollAsync()
    {
        if (polling || qr == null) return;
        if (DateTime.Now > qrUntil) { await StartQrAsync(); return; }
        polling = true;
        try
        {
            var s = await VkApi.QrCheckAsync(qr);
            switch (s.Status)
            {
                case 1: qrState.Text = L.T("Код отсканирован — подтвердите вход на телефоне."); break;
                case 2:
                    if (s.Token is { Length: > 0 } && s.UserId != 0)
                    {
                        poll.Stop();
                        await AppHost.Vk.SetTokenAsync(s.Token, s.UserId, qr.Client);
                        DialogResult = true;
                    }
                    else
                    {
                        poll.Stop();
                        Paths.Trace("vk-qr: подтверждено без ключа, поля=" + s.Keys);
                        qrState.Text = L.T("VK подтвердил вход, но не выдал ключ для музыки. Попробуйте вход по паролю позже.");
                    }
                    break;
                case 3: poll.Stop(); qrState.Text = L.T("Вход отклонён на телефоне. Нажмите «Новый код»."); break;
                case 4: await StartQrAsync(); break;
                case 5:
                    qrState.Text = L.T("Введите код, который VK показал на телефоне.");
                    if (!qrCode.IsKeyboardFocused) qrCode.Focus();
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            if (ex is VkException) { poll.Stop(); qrState.Text = ex.Message; }
        }
        finally { polling = false; }
    }

    static TextBox Field() => new() { Height = 36, FontSize = 14, Padding = new Thickness(10, 7, 10, 0) };
    static TextBlock Label(string t) => new() { Text = t, Foreground = Fg2, FontSize = 12, Margin = new Thickness(0, 10, 0, 4), TextWrapping = TextWrapping.Wrap };

    async Task SubmitAsync(bool forceSms)
    {
        if (busy) return;
        if (login.Text.Trim().Length == 0 || password.Password.Length == 0) { message.Text = L.T("Введите логин и пароль."); return; }
        busy = true;
        go.IsEnabled = sms.IsEnabled = false;
        message.Text = "";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var r = await VkApi.DirectAuthAsync(login.Text.Trim(), password.Password,
                codeBox.IsVisible && !forceSms ? code.Text : null,
                captchaBox.IsVisible ? captchaSid : null, captchaBox.IsVisible ? captcha.Text : null, forceSms);

            captchaBox.Visibility = Visibility.Collapsed;
            if (r.Token is { Length: > 0 } && r.UserId != 0)
            {
                await AppHost.Vk.SetTokenAsync(r.Token, r.UserId);
                DialogResult = true;
                return;
            }
            switch (r.Error)
            {
                case "need_validation" when r.ValidationType is { Length: > 0 }:
                    codeBox.Visibility = Visibility.Visible;
                    codeHint.Text = r.ValidationType.Contains("app")
                        ? L.T("Код подтверждения из приложения VK или генератора кодов")
                        : $"Код из SMS{(r.PhoneMask is { Length: > 0 } m ? L.T(" на ") + m : "")}";
                    message.Text = forceSms ? L.T("Код отправлен.") : "";
                    code.Focus();
                    break;
                case "need_validation" when r.ValidationUrl is { Length: > 0 } url:
                    // VK просит пройти проверку на своей странице — открываем её, токен ловим из адреса
                    var t = await BrowserCheck.RunAsync(this, url);
                    if (t is { } ok)
                    {
                        await AppHost.Vk.SetTokenAsync(ok.token, ok.uid);
                        DialogResult = true;
                        return;
                    }
                    message.Text = L.T("Проверка VK не завершена. Попробуйте ещё раз.");
                    break;
                case "need_captcha":
                    captchaSid = r.CaptchaSid;
                    captchaBox.Visibility = Visibility.Visible;
                    captcha.Text = "";
                    if (r.CaptchaImg is { Length: > 0 } img) captchaImg.Source = new BitmapImage(new Uri(img));
                    message.Text = L.T("VK просит подтвердить, что вы не робот.");
                    captcha.Focus();
                    break;
                case "invalid_client":
                case "invalid_request" when (r.Description ?? "").Contains("code", StringComparison.OrdinalIgnoreCase):
                    message.Text = codeBox.IsVisible ? L.T("Неверный код или пароль.") : L.T("Неверный логин или пароль.");
                    break;
                default:
                    message.Text = "VK: " + (r.Description ?? r.Error ?? L.T("неизвестная ошибка"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            message.Text = L.T("Не удалось связаться с VK. Проверьте интернет.");
        }
        finally
        {
            Mouse.OverrideCursor = null;
            busy = false;
            go.IsEnabled = sms.IsEnabled = true;
        }
    }
}

/// <summary>Окно проверки VK в браузере: ждём перенаправления на blank.html с токеном.</summary>
static class BrowserCheck
{
    public static async Task<(string token, long uid)?> RunAsync(Window owner, string url)
    {
        (string, long)? result = null;
        var w = new Window
        {
            Title = "Taskbeat — VK", Width = 520, Height = 720, Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes.Black,
        };
        var view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.Black };
        w.Content = view;
        void Catch(string uri)
        {
            if (result != null || !Uri.TryCreate(uri, UriKind.Absolute, out var u) || !u.AbsolutePath.EndsWith("/blank.html")) return;
            var q = HttpUtility.ParseQueryString(u.Fragment.TrimStart('#'));
            if (q["access_token"] is { Length: > 0 } t && long.TryParse(q["user_id"], out var id))
            {
                result = (t, id);
                w.Close();
            }
        }
        w.Loaded += async (_, _) =>
        {
            try
            {
                await view.EnsureCoreWebView2Async(await WebHub.EnvironmentAsync());
                view.CoreWebView2.NavigationStarting += (_, e) => Catch(e.Uri);
                view.CoreWebView2.SourceChanged += (_, _) => Catch(view.CoreWebView2.Source);
                view.CoreWebView2.Navigate(url);
            }
            catch (Exception ex) { Program.Log(ex); w.Close(); }
        };
        w.ShowDialog();
        await Task.Yield();
        return result;
    }
}
