using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>
/// Если Taskbeat в прошлый раз закрылся не сам (сбой, зависание, снятие задачи), при запуске
/// предлагаем отправить отчёт: журнал ошибок без имени пользователя и путей. Только по кнопке.
/// </summary>
static class CrashReport
{
    static readonly string Flag = Path.Combine(Paths.DataDir, "running.flag");
    static DateTime? lastStart;
    public static bool Crashed { get; private set; }

    /// <summary>В самом начале запуска.</summary>
    public static void OnStart()
    {
        try
        {
            if (File.Exists(Flag))
            {
                Crashed = true;
                if (DateTime.TryParse(File.ReadAllText(Flag), null, System.Globalization.DateTimeStyles.RoundtripKind, out var t)) lastStart = t;
            }
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Flag, DateTime.Now.ToString("o"));
        }
        catch { }
    }

    /// <summary>Нормальный выход.</summary>
    public static void OnCleanExit()
    {
        try { File.Delete(Flag); } catch { }
    }

    public static void OfferIfCrashed()
    {
        if (!Crashed || !SettingsStore.Current.AskCrashReports || !Feedback.Configured) return;
        Crashed = false;
        var w = new CrashWindow(BuildLog());
        w.Show();
        w.Activate();
    }

    static string BuildLog()
    {
        var sb = new StringBuilder();
        try
        {
            string err = Path.Combine(Paths.DataDir, "error.log");
            if (File.Exists(err))
            {
                var text = File.ReadAllText(err);
                // только записи с прошлого запуска
                var entries = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
                    .Where(e => lastStart == null || (DateTime.TryParse(e.Length >= 19 ? e[..19] : "", out var t) && t >= lastStart))
                    .TakeLast(3);
                foreach (var e in entries) sb.AppendLine(Trim(e, 900)).AppendLine();
            }
            var events = WindowsEvents();
            if (events.Length > 0) sb.AppendLine("— Windows —").AppendLine(Trim(events, 900));
            string startup = Path.Combine(Paths.DataDir, "startup.log");
            if (File.Exists(startup))
                sb.AppendLine("— startup —").AppendLine(string.Join("\n", File.ReadAllLines(startup).TakeLast(6)));
        }
        catch { }
        return Sanitize(sb.Length > 0 ? sb.ToString() : L.T("(журнал пуст: возможно, процесс был закрыт извне)"));
    }

    /// <summary>Записи Windows о падении Taskbeat (переполнение стека и т.п. не попадают в наш журнал).</summary>
    static string WindowsEvents()
    {
        try
        {
            var psi = new ProcessStartInfo("wevtutil.exe",
                "qe Application /q:\"*[System[(EventID=1000 or EventID=1026) and TimeCreated[timediff(@SystemTime) <= 604800000]]]\" /c:6 /rd:true /f:text")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            using var p = Process.Start(psi)!;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var hits = all.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Where(e => e.Contains("Taskbeat", StringComparison.OrdinalIgnoreCase)).Take(2);
            return string.Join("\n", hits.Select(h => string.Join("\n", h.Split('\n').Where(l => l.Contains("Date:") || l.Contains("Description") || l.Contains("Exception") || l.Contains("Faulting") || l.TrimStart().StartsWith("at ")).Take(12))));
        }
        catch { return ""; }
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + " …";

    /// <summary>Убираем имя пользователя и личные пути.</summary>
    public static string Sanitize(string s)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 3) s = s.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        string user = Environment.UserName;
        if (user.Length > 2) s = s.Replace(user, "<user>", StringComparison.OrdinalIgnoreCase);
        return s;
    }
}

sealed class CrashWindow : Window
{
    public CrashWindow(string log)
    {
        Title = "Taskbeat";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001

        var fg2 = new SolidColorBrush(Color.FromRgb(150, 150, 158));
        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        root.Children.Add(new TextBlock { Text = L.T("Taskbeat закрылся с ошибкой"), FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock { Text = L.T("В прошлый раз приложение завершилось неожиданно. Отправить разработчику отчёт? В нём нет имени пользователя и личных файлов — только журнал ошибок ниже."), TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = fg2, Margin = new Thickness(0, 6, 0, 12) });

        root.Children.Add(new TextBlock { Text = L.T("Что вы делали перед этим (необязательно)"), FontSize = 12, Foreground = fg2, Margin = new Thickness(0, 0, 0, 4) });
        var what = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, FontSize = 13.5, Padding = new Thickness(10, 8, 10, 8), MaxLength = 600 };
        root.Children.Add(what);

        var details = new Expander { Header = L.T("Что будет отправлено"), Margin = new Thickness(0, 12, 0, 0), Foreground = fg2 };
        details.Content = new TextBox
        {
            Text = log, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
            MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0),
        };
        root.Children.Add(details);

        var never = new CheckBox { Content = L.T("Больше не спрашивать"), Margin = new Thickness(0, 12, 0, 0) };
        root.Children.Add(never);
        var status = new TextBlock { FontSize = 12.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        root.Children.Add(status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        var send = new Button { Content = L.T("Отправить отчёт"), Height = 38, Padding = new Thickness(22, 0, 22, 0) };
        send.SetResourceReference(StyleProperty, "PillAccent");
        var skip = new Button { Content = L.T("Не отправлять"), Margin = new Thickness(8, 0, 0, 0) };
        skip.SetResourceReference(StyleProperty, "Pill");
        buttons.Children.Add(send);
        buttons.Children.Add(skip);
        root.Children.Add(buttons);
        Content = root;

        void SaveNever() { if (never.IsChecked == true) { SettingsStore.Current.AskCrashReports = false; SettingsStore.Save(); } }
        skip.Click += (_, _) => { SaveNever(); Close(); };
        send.Click += async (_, _) =>
        {
            send.IsEnabled = false;
            string text = (what.Text.Trim().Length > 0 ? what.Text.Trim() + "\n\n" : "") + L.T("[автоотчёт о сбое]\n") + log;
            if (text.Length > 3400) text = text[..3400];
            var (ok, msg) = await Feedback.SendAsync("bug", text, "", Feedback.AppInfo(), null);
            status.Text = msg;
            status.Foreground = ok ? new SolidColorBrush(Color.FromRgb(120, 220, 140)) : new SolidColorBrush(Color.FromRgb(255, 140, 120));
            if (ok) { SaveNever(); await Task.Delay(1200); Close(); }
            else send.IsEnabled = true;
        };
    }
}
