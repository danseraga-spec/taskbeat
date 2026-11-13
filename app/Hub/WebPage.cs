using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>Страница с официальным веб-плеером сервиса (YouTube, SoundCloud, Яндекс Музыка).</summary>
sealed class WebPage : UserControl
{
    readonly WebSurface surface;
    readonly Border slot;
    readonly TextBlock hint, status;
    readonly Button back;
    bool active;

    public WebPage(WebSurface surface)
    {
        this.surface = surface;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(26, 0, 26, 8) };
        bar.Children.Add(IconButton("", "Назад", () => { if (surface.Web?.CanGoBack == true) surface.Web.GoBack(); }));
        bar.Children.Add(IconButton("", "Вперёд", () => { if (surface.Web?.CanGoForward == true) surface.Web.GoForward(); }));
        bar.Children.Add(IconButton("", "Обновить", () => surface.Web?.Reload()));
        bar.Children.Add(IconButton("", "На главную", () => surface.Web?.Navigate(surface.Home)));
        if (surface.Key == "youtube")
            bar.Children.Add(IconButton("", "Видео в картинку-в-картинке", () => AppHost.Web.EnterPip()));
        hint = new TextBlock
        {
            Text = surface.Key switch
            {
                "youtube" => "Войдите в аккаунт Google прямо здесь — вход сохранится. Видео можно вынести в картинку-в-картинке.",
                "yandex" => "Войдите в Яндекс прямо здесь — вход сохранится.",
                _ => "Войдите прямо здесь — вход сохранится.",
            },
            Foreground = (Brush)Application.Current.TryFindResource("Fg3") ?? Brushes.Gray,
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
        };
        bar.Children.Add(hint);

        status = new TextBlock
        {
            Text = "Загрузка…", Foreground = Brushes.Gray, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
        };
        back = new Button { Content = "Вернуть видео сюда", Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
        back.SetResourceReference(StyleProperty, "Pill");
        back.Click += (_, _) => AppHost.Web.ExitPip(false);
        var center = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        center.Children.Add(status);
        center.Children.Add(back);
        slot = new Border
        {
            Margin = new Thickness(18, 0, 18, 18), CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromRgb(11, 11, 13)),
            Child = center,
        };
        slot.SizeChanged += (_, _) => Place();
        AppHost.Web.PipStateChanged += () =>
        {
            bool pip = surface.InPip;
            status.Text = pip ? "Видео играет в картинке-в-картинке" : "Загрузка…";
            back.Visibility = pip ? Visibility.Visible : Visibility.Collapsed;
        };

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(slot);
        Content = root;
        Loaded += (_, _) => Place();
    }

    Button IconButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip };
        b.SetResourceReference(StyleProperty, "Icon");
        b.Click += (_, _) => click();
        return b;
    }

    public async void Activate()
    {
        active = true;
        try
        {
            await surface.EnsureAsync(AppHost.HubHandle);
            Place();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            status.Text = "Не удалось запустить WebView2";
        }
    }

    public void Deactivate()
    {
        active = false;
        surface.Hide();
    }

    /// <summary>Совместить окно WebView2 с прямоугольником-заглушкой на странице.</summary>
    public void Place()
    {
        if (!active || surface.InPip || !slot.IsLoaded || surface.Controller == null) return;
        var win = Window.GetWindow(this);
        if (win == null) return;
        double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var p = slot.TranslatePoint(new Point(0, 0), win);
        surface.Show(AppHost.HubHandle, new System.Drawing.Rectangle(
            (int)Math.Round(p.X * k), (int)Math.Round(p.Y * k),
            (int)Math.Round(slot.ActualWidth * k), (int)Math.Round(slot.ActualHeight * k)));
    }
}
