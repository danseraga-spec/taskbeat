using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>
/// Перенос плейлиста в VK: список «Исполнитель — Название» или ссылка на плейлист/альбом
/// Spotify или Яндекс Музыки. Каждый трек ищется в VK, найденные складываются в новый плейлист.
/// </summary>
sealed class ImportWindow : Window
{
    static ImportWindow? open;
    static readonly HttpClient Http = CreateHttp();

    readonly TextBox source = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 180, FontSize = 13.5, Padding = new Thickness(10, 8, 10, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBox name = new() { FontSize = 13.5, Padding = new Thickness(10, 6, 10, 6) };
    readonly Button go = new() { Height = 38, Padding = new Thickness(24, 0, 24, 0) };
    readonly Button stop = new() { Margin = new Thickness(8, 0, 0, 0), Visibility = Visibility.Collapsed };
    readonly ProgressBar bar = new() { Height = 4, Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };
    readonly TextBlock status = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBox missed = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 140, FontSize = 12, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    CancellationTokenSource? cts;

    static HttpClient CreateHttp()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        return h;
    }

    public static void Open()
    {
        if (open != null) { open.Activate(); return; }
        open = new ImportWindow();
        if (AppHost.Hub is { IsVisible: true } hub) open.Owner = hub;
        open.Show();
    }

    ImportWindow()
    {
        Title = "Taskbeat — " + L.T("перенос плейлиста");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(12, 12, 14));
#pragma warning disable WPF0001
        ThemeMode = ThemeMode.Dark;
#pragma warning restore WPF0001
        Closed += (_, _) => { open = null; cts?.Cancel(); };

        var fg2 = new SolidColorBrush(Color.FromRgb(150, 150, 158));
        var root = new StackPanel { Margin = new Thickness(28, 22, 28, 24) };
        root.Children.Add(new TextBlock { Text = L.T("Перенести плейлист в VK"), FontSize = 21, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        root.Children.Add(new TextBlock
        {
            Text = L.T("Вставьте ссылку на плейлист или альбом Spotify / Яндекс Музыки, либо список треков — по одному в строке: «Исполнитель — Название». Taskbeat найдёт их в VK и соберёт новый плейлист."),
            TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Foreground = fg2, Margin = new Thickness(0, 6, 0, 12),
        });
        root.Children.Add(source);
        root.Children.Add(new TextBlock { Text = L.T("Название плейлиста"), FontSize = 12, Foreground = fg2, Margin = new Thickness(0, 12, 0, 4) });
        name.Text = L.T("Перенесённый плейлист");
        root.Children.Add(name);
        root.Children.Add(bar);
        root.Children.Add(status);
        root.Children.Add(missed);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        go.Content = L.T("Перенести");
        go.SetResourceReference(StyleProperty, "PillAccent");
        go.Click += async (_, _) => await RunAsync();
        stop.Content = L.T("Остановить");
        stop.SetResourceReference(StyleProperty, "Pill");
        stop.Click += (_, _) => cts?.Cancel();
        buttons.Children.Add(go);
        buttons.Children.Add(stop);
        root.Children.Add(buttons);
        Content = root;
    }

    void Status(string text, bool error = false)
    {
        status.Text = text;
        status.Foreground = error ? new SolidColorBrush(Color.FromRgb(255, 140, 120)) : new SolidColorBrush(Color.FromRgb(200, 200, 206));
    }

    async Task RunAsync()
    {
        if (!AppHost.Vk.LoggedIn) { Status(L.T("Сначала войдите в VK."), true); return; }
        string input = source.Text.Trim();
        if (input.Length == 0) { Status(L.T("Вставьте ссылку или список треков."), true); return; }
        go.IsEnabled = false;
        stop.Visibility = Visibility.Visible;
        missed.Visibility = Visibility.Collapsed;
        cts = new CancellationTokenSource();
        var ct = cts.Token;
        try
        {
            Status(L.T("Читаю список…"));
            var (title, wanted) = await ReadSourceAsync(input);
            if (wanted.Count == 0) { Status(L.T("Не удалось найти треки. Если это ссылка — проверьте, что плейлист открыт для всех, или вставьте список текстом."), true); return; }
            if (title != null && name.Text == L.T("Перенесённый плейлист")) name.Text = title;

            bar.Visibility = Visibility.Visible;
            bar.Maximum = wanted.Count;
            var found = new List<Track>();
            var notFound = new List<string>();
            for (int i = 0; i < wanted.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (artist, song) = wanted[i];
                Status(L.F("Ищу {0} из {1}: {2}", i + 1, wanted.Count, artist.Length > 0 ? $"{artist} — {song}" : song));
                var hit = await FindAsync(artist, song);
                if (hit != null && found.All(f => f.Id != hit.Id)) found.Add(hit);
                else if (hit == null) notFound.Add(artist.Length > 0 ? $"{artist} — {song}" : song);
                bar.Value = i + 1;
                await Task.Delay(350, ct); // не упираться в ограничения VK
            }
            if (found.Count == 0) { Status(L.T("Ни один трек не нашёлся в VK."), true); return; }

            Status(L.T("Создаю плейлист…"));
            var pl = await AppHost.Vk.CreatePlaylistAsync(name.Text.Trim().Length > 0 ? name.Text.Trim() : L.T("Перенесённый плейлист"));
            foreach (var chunk in found.Chunk(100))
            {
                ct.ThrowIfCancellationRequested();
                await AppHost.Vk.AddToPlaylistAsync(pl, chunk);
                await Task.Delay(350, ct);
            }
            Status(L.F("Готово: {0} из {1} треков в плейлисте «{2}».", found.Count, wanted.Count, pl.Title));
            if (notFound.Count > 0)
            {
                missed.Text = L.T("Не нашлись:") + "\n" + string.Join("\n", notFound);
                missed.Visibility = Visibility.Visible;
            }
            AppHost.Hub?.OpenPlaylist(pl);
        }
        catch (OperationCanceledException) { Status(L.T("Остановлено.")); }
        catch (VkException ex) { Status(ex.Message, true); }
        catch (Exception ex) { Program.Log(ex); Status(L.T("Ошибка") + ": " + ex.Message, true); }
        finally
        {
            go.IsEnabled = true;
            stop.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ поиск в VK

    static async Task<Track?> FindAsync(string artist, string song)
    {
        var res = await AppHost.Vk.SearchAsync((artist + " " + song).Trim(), 10);
        string a = Norm(artist), s = Norm(song);
        int Score(Track t)
        {
            string ta = Norm(t.Artist), ts = Norm(t.Title);
            int sc = 0;
            if (ts == s) sc += 4; else if (ts.Contains(s) || s.Contains(ts)) sc += 2;
            if (a.Length > 0) { if (ta == a) sc += 3; else if (ta.Contains(a) || a.Contains(ta)) sc += 1; }
            if (!t.Playable) sc -= 5;
            return sc;
        }
        var best = res.Select(t => (t, sc: Score(t))).OrderByDescending(x => x.sc).FirstOrDefault();
        return best.t != null && best.sc >= 2 ? best.t : null;
    }

    static string Norm(string s)
    {
        s = Regex.Replace(s.ToLowerInvariant(), @"\((feat|ft|prod)[^)]*\)|\[[^\]]*\]", "");
        s = Regex.Replace(s, @"[^\p{L}\p{N}]+", " ");
        return s.Replace('ё', 'е').Trim();
    }

    // ------------------------------------------------------------------ источники

    static async Task<(string? title, List<(string artist, string song)> tracks)> ReadSourceAsync(string input)
    {
        if (Regex.Match(input, @"open\.spotify\.com/(?:intl-\w+/)?(playlist|album)/([A-Za-z0-9]+)") is { Success: true } sp)
            return await SpotifyAsync(sp.Groups[1].Value, sp.Groups[2].Value);
        if (Regex.Match(input, @"music\.yandex\.\w+/users/([^/]+)/playlists/(\d+)") is { Success: true } yu)
            return await YandexAsync($"https://api.music.yandex.net/users/{Uri.EscapeDataString(yu.Groups[1].Value)}/playlists/{yu.Groups[2].Value}");
        if (Regex.Match(input, @"music\.yandex\.\w+/playlists/([\w-]+)") is { Success: true } yp)
            return await YandexAsync($"https://api.music.yandex.net/playlist/{yp.Groups[1].Value}");
        if (Regex.Match(input, @"music\.yandex\.\w+/album/(\d+)") is { Success: true } ya)
            return await YandexAsync($"https://api.music.yandex.net/albums/{ya.Groups[1].Value}/with-tracks");
        return (null, ParseLines(input));
    }

    static List<(string, string)> ParseLines(string text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = Regex.Replace(raw.Trim(), @"^\d+[\.\)]\s*", ""); // «1. …»
            line = Regex.Replace(line, @"\s+\d{1,2}:\d{2}$", "");          // длительность в конце
            if (line.Length < 2) continue;
            var parts = Regex.Split(line, @"\s+[-–—]\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
            list.Add(parts.Length >= 2 ? (parts[0].Trim(), string.Join(" - ", parts.Skip(1)).Trim()) : ("", line));
        }
        return list;
    }

    /// <summary>Spotify без входа: встраиваемый плеер содержит список треков (до 100).</summary>
    static async Task<(string?, List<(string, string)>)> SpotifyAsync(string kind, string id)
    {
        var html = await Http.GetStringAsync($"https://open.spotify.com/embed/{kind}/{id}");
        var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\" type=\"application/json\">(.+?)</script>", RegexOptions.Singleline);
        if (!m.Success) return (null, []);
        using var doc = JsonDocument.Parse(m.Groups[1].Value);
        var entity = doc.RootElement.GetProperty("props").GetProperty("pageProps").GetProperty("state").GetProperty("data").GetProperty("entity");
        string? title = entity.TryGetProperty("name", out var n) ? n.GetString() : entity.TryGetProperty("title", out var t) ? t.GetString() : null;
        var list = new List<(string, string)>();
        if (entity.TryGetProperty("trackList", out var tl))
            foreach (var x in tl.EnumerateArray())
                list.Add((x.TryGetProperty("subtitle", out var s) ? (s.GetString() ?? "").Split(',')[0].Trim() : "", x.GetProperty("title").GetString() ?? ""));
        return (title, list);
    }

    static async Task<(string?, List<(string, string)>)> YandexAsync(string url)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        var r = doc.RootElement.GetProperty("result");
        string? title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
        var list = new List<(string, string)>();
        void Add(JsonElement tr)
        {
            if (!tr.TryGetProperty("title", out var tt)) return;
            string artist = tr.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0 ? ar[0].GetProperty("name").GetString() ?? "" : "";
            list.Add((artist, tt.GetString() ?? ""));
        }
        if (r.TryGetProperty("tracks", out var tracks))
            foreach (var x in tracks.EnumerateArray()) Add(x.TryGetProperty("track", out var tr) ? tr : x);
        if (r.TryGetProperty("volumes", out var vols))
            foreach (var v in vols.EnumerateArray()) foreach (var x in v.EnumerateArray()) Add(x);
        return (title, list);
    }
}
