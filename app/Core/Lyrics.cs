using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CoolPlayer;

/// <summary>Строка текста; Time &lt; 0 — без синхронизации.</summary>
public sealed record LyricLine(double Time, string Text);

public sealed record Lyrics(List<LyricLine> Lines, bool Synced, string Source);

/// <summary>
/// Тексты песен: VK (официальные, часто синхронизированные) и LrcLib (открытая база LRC).
/// «Авто» — сначала синхронизированные из VK, потом из LrcLib, потом любой обычный текст.
/// </summary>
static partial class LyricsService
{
    static readonly HttpClient Http = CreateHttp();
    static readonly Dictionary<string, Lyrics?> Cache = [];

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Taskbeat/1.0 (Windows desktop music player)");
        return h;
    }

    public static async Task<Lyrics?> GetAsync(string source, Track? vk, string artist, string title, double duration)
    {
        string key = $"{source}|{vk?.Id}|{artist}|{title}";
        if (Cache.TryGetValue(key, out var hit)) return hit;

        Lyrics? result = null;
        async Task<Lyrics?> Vk()
        {
            if (vk == null || !AppHost.Vk.LoggedIn) return null;
            try { return await AppHost.Vk.LyricsAsync(vk); } catch (Exception ex) { Program.Log(ex); return null; }
        }
        async Task<Lyrics?> Lrc()
        {
            try { return await LrcLibAsync(artist, title, duration); } catch (Exception ex) { Program.Log(ex); return null; }
        }

        switch (source)
        {
            case "vk": result = await Vk(); break;
            case "lrclib": result = await Lrc(); break;
            default:
                var a = await Vk();
                if (a is { Synced: true }) { result = a; break; }
                var b = await Lrc();
                result = b is { Synced: true } ? b : a ?? b;
                break;
        }
        Cache[key] = result;
        return result;
    }

    // ------------------------------------------------------------------ LrcLib

    static async Task<Lyrics?> LrcLibAsync(string artist, string title, double duration)
    {
        artist = MainArtist(artist);
        title = CleanTitle(title);
        if (title.Length == 0) return null;

        string url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
        if (duration > 0) url += $"&duration={(int)Math.Round(duration)}";
        using (var r = await Http.GetAsync(url))
        {
            if (r.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                if (FromLrcLib(doc.RootElement) is { } l) return l;
            }
        }

        // поиск: берём ближайший по длительности, синхронизированные в приоритете
        var json = await Http.GetStringAsync(
            $"https://lrclib.net/api/search?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}");
        using var list = JsonDocument.Parse(json);
        JsonElement? best = null;
        double bestScore = double.MaxValue;
        foreach (var e in list.RootElement.EnumerateArray())
        {
            double d = e.TryGetProperty("duration", out var dd) && dd.TryGetDouble(out var dv) ? dv : 0;
            bool synced = e.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String;
            double score = (duration > 0 && d > 0 ? Math.Abs(d - duration) : 30) + (synced ? 0 : 1000);
            if (score < bestScore) { bestScore = score; best = e.Clone(); }
        }
        return best is { } b ? FromLrcLib(b) : null;
    }

    static Lyrics? FromLrcLib(JsonElement e)
    {
        if (e.TryGetProperty("syncedLyrics", out var s) && s.GetString() is { Length: > 0 } lrc)
        {
            var parsed = ParseLrc(lrc);
            if (parsed.Count > 0) return new Lyrics(parsed, true, "LrcLib");
        }
        if (e.TryGetProperty("plainLyrics", out var p) && p.GetString() is { Length: > 0 } plain)
            return new Lyrics([.. plain.Split('\n').Select(x => new LyricLine(-1, x.TrimEnd('\r')))], false, "LrcLib");
        return null;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]")]
    private static partial Regex LrcTime();

    public static List<LyricLine> ParseLrc(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Split('\n'))
        {
            var ms = LrcTime().Matches(raw);
            if (ms.Count == 0) continue;
            string text = LrcTime().Replace(raw, "").Trim();
            foreach (Match m in ms)
            {
                double sec = int.Parse(m.Groups[1].Value) * 60 +
                             double.Parse(m.Groups[2].Value.Replace(':', '.'), System.Globalization.CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(sec, text));
            }
        }
        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return lines;
    }

    [GeneratedRegex(@"\s*[\(\[](feat|ft|prod|при уч|remaster|live|explicit|bonus)[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    static string CleanTitle(string t) => Noise().Replace(t, "").Trim();

    static string MainArtist(string a)
    {
        foreach (var sep in new[] { ", ", " feat", " ft.", " & ", " x ", " и " })
        {
            int i = a.IndexOf(sep, StringComparison.OrdinalIgnoreCase);
            if (i > 0) a = a[..i];
        }
        return a.Trim();
    }
}
