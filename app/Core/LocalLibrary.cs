using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace CoolPlayer;

/// <summary>Музыка на компьютере: загрузки Taskbeat, «Музыка» и папки, добавленные пользователем.
/// Теги и обложки читаются через Windows и кэшируются (library.json, covers\).</summary>
static class LocalLibrary
{
    static readonly string[] Exts = [".mp3", ".m4a", ".aac", ".flac", ".ogg", ".opus", ".wav", ".wma", ".alac"];
    const int MaxFiles = 5000;
    static readonly string CacheFile = Path.Combine(Paths.LocalDir, "library.json");
    static readonly string CoverDir = Path.Combine(Paths.LocalDir, "covers");

    sealed class Cached
    {
        public long Ticks { get; set; }
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string Album { get; set; } = "";
        public double Duration { get; set; }
        public string? Cover { get; set; }
    }

    public static IEnumerable<string> Folders()
    {
        var all = new List<string> { Downloader.Folder, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic) };
        all.AddRange(SettingsStore.Current.LocalFolders);
        return all.Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<List<Track>> ScanAsync(IProgress<int>? progress = null)
    {
        var cache = LoadCache();
        var files = await Task.Run(() =>
        {
            var list = new List<string>();
            foreach (var dir in Folders())
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = System.IO.FileAttributes.System | System.IO.FileAttributes.Hidden };
                    list.AddRange(Directory.EnumerateFiles(dir, "*", opts).Where(f => Exts.Contains(Path.GetExtension(f).ToLowerInvariant())).Take(MaxFiles - list.Count));
                }
                catch { }
                if (list.Count >= MaxFiles) break;
            }
            return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });

        var result = new List<Track>(files.Count);
        var fresh = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        int n = 0;
        foreach (var f in files)
        {
            long ticks;
            try { ticks = File.GetLastWriteTimeUtc(f).Ticks; } catch { continue; }
            if (!cache.TryGetValue(f, out var c) || c.Ticks != ticks) c = await ReadAsync(f, ticks);
            fresh[f] = c;
            result.Add(new Track
            {
                Id = "local:" + f.ToLowerInvariant(), Source = "Local",
                Title = c.Title, Artist = c.Artist, AlbumTitle = c.Album.Length > 0 ? c.Album : null, Duration = c.Duration,
                Cover = c.Cover, CoverBig = c.Cover, Url = new Uri(f).AbsoluteUri,
            });
            if (++n % 25 == 0) progress?.Report(n);
        }
        SaveCache(fresh);
        return [.. result.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.AlbumTitle).ThenBy(t => t.Title)];
    }

    static async Task<Cached> ReadAsync(string path, long ticks)
    {
        var c = new Cached { Ticks = ticks, Title = Path.GetFileNameWithoutExtension(path) };
        // «Исполнитель - Название» в имени файла — запасной вариант без тегов
        var parts = c.Title.Split(" - ", 2);
        if (parts.Length == 2) { c.Artist = parts[0].Trim(); c.Title = parts[1].Trim(); }
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var mp = await file.Properties.GetMusicPropertiesAsync();
            if (!string.IsNullOrWhiteSpace(mp.Title)) c.Title = mp.Title;
            var artist = !string.IsNullOrWhiteSpace(mp.Artist) ? mp.Artist : mp.AlbumArtist;
            if (!string.IsNullOrWhiteSpace(artist)) c.Artist = artist;
            c.Album = mp.Album ?? "";
            c.Duration = mp.Duration.TotalSeconds;
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.MusicView, 96, ThumbnailOptions.UseCurrentScale);
            if (thumb != null && thumb.Type == ThumbnailType.Image)
            {
                Directory.CreateDirectory(CoverDir);
                string name = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))) [..16] + ".jpg";
                string dst = Path.Combine(CoverDir, name);
                await using (var fs = File.Create(dst))
                    await thumb.AsStreamForRead().CopyToAsync(fs);
                c.Cover = dst;
            }
        }
        catch { }
        return c;
    }

    static Dictionary<string, Cached> LoadCache()
    {
        try
        {
            if (File.Exists(CacheFile))
                return JsonSerializer.Deserialize<Dictionary<string, Cached>>(File.ReadAllText(CacheFile)) is { } d
                    ? new Dictionary<string, Cached>(d, StringComparer.OrdinalIgnoreCase) : [];
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    static void SaveCache(Dictionary<string, Cached> d)
    {
        try
        {
            Directory.CreateDirectory(Paths.LocalDir);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(d));
        }
        catch { }
    }

    public static string? PathOf(Track t) => t.Source == "Local" && Uri.TryCreate(t.Url, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;
}
