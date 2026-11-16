using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>Предложение обновиться: что нового и кнопки. Обновляется только по нажатию «Обновить».</summary>
sealed class UpdateWindow : Window
{
    static UpdateWindow? open;

    readonly Updater.Release rel;
    readonly Button update = new() { Height = 38, Padding = new Thickness(24, 0, 24, 0) };
    readonly ProgressBar bar = new() { Height = 4, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed, Maximum = 1 };
    readonly TextBlock status = new() { FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(255, 140, 120)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public static void Offer(bool manual)
    {
        if (Updater.Available is not { } r) return;
        if (!manual && SettingsStore.Current.SkippedVersion == r.Tag) return;
        if (open != null) { open.Activate(); return; }
        open = new UpdateWindow(r);
        if (AppHost.Hub is { IsVisible: true } hub) open.Owner = hub;
        else open.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        open.Show();
        open.Activate();
    }

    UpdateWindow(Updater.Release r)
    {
        rel = r;
        Title = "Taskbeat — " + L.T("обновление");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Closed += (_, _) => { open = null; Updater.Changed -= OnProgress; };

        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        root.Children.Add(new TextBlock { Text = L.F("Доступна версия {0}", r.Version.ToString(3)), FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = L.F("У вас {0}. Что нового:", Updater.Current.ToString(3)), FontSize = 12.5, Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)), Margin = new Thickness(0, 4, 0, 10) });

        var notes = new TextBlock { Text = Clean(r.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 13.5, LineHeight = 20, Foreground = new SolidColorBrush(Color.FromRgb(225, 225, 230)) };
        root.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(22, 22, 26)), CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 12),
            Child = new ScrollViewer { MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = notes },
        });

        root.Children.Add(bar);
        root.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        update.Content = L.T("Обновить");
        update.SetResourceReference(StyleProperty, "PillAccent");
        update.Click += async (_, _) => await DoUpdate();
        var later = new Button { Content = L.T("Позже"), Margin = new Thickness(8, 0, 0, 0) };
        later.SetResourceReference(StyleProperty, "Pill");
        later.Click += (_, _) => Close();
        var skip = new Button { Content = L.T("Пропустить эту версию"), Margin = new Thickness(8, 0, 0, 0) };
        skip.SetResourceReference(StyleProperty, "Pill");
        skip.Click += (_, _) => { SettingsStore.Current.SkippedVersion = rel.Tag; SettingsStore.Save(); Close(); };
        buttons.Children.Add(update);
        buttons.Children.Add(later);
        buttons.Children.Add(skip);
        root.Children.Add(buttons);

        var link = new TextBlock { Margin = new Thickness(0, 12, 0, 0), FontSize = 12 };
        var h = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(L.T("Открыть страницу релиза"))) { Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)) };
        h.Click += (_, _) => AppHost.OpenUri(rel.Page);
        link.Inlines.Add(h);
        root.Children.Add(link);
        Content = root;
        Updater.Changed += OnProgress;
    }

    void OnProgress() => Dispatcher.InvokeAsync(() =>
    {
        if (!Updater.Busy) return;
        bar.Visibility = Visibility.Visible;
        bar.Value = Updater.Progress;
        update.Content = L.F("Скачиваю… {0:0}%", Updater.Progress * 100);
    });

    async Task DoUpdate()
    {
        update.IsEnabled = false;
        status.Text = "";
        var err = await Updater.InstallAsync();
        if (err != null) { status.Text = err; update.IsEnabled = true; update.Content = L.T("Обновить"); }
    }

    /// <summary>Описание релиза в Markdown → простой текст.</summary>
    static string Clean(string md)
    {
        if (string.IsNullOrWhiteSpace(md)) return L.T("Исправления и улучшения.");
        var s = md.Replace("\r", "");
        s = Regex.Replace(s, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^>\s?", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "$1");
        s = Regex.Replace(s, @"`([^`]+)`", "$1");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }
}
