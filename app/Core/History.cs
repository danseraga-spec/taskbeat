using System.IO;
using System.Text.Json;

namespace CoolPlayer;

/// <summary>История прослушивания: последние 500 треков, history.json в папке данных.</summary>
static class History
{
    public sealed class Entry
    {
        public Track Track { get; set; } = new();
        public DateTime Played { get; set; }
    }

    const int Max = 500;
    static readonly string FilePath = Path.Combine(Paths.DataDir, "history.json");
    static List<Entry>? items;
    static readonly object gate = new();
    static System.Threading.Timer? saveTimer;
    public static event Action? Changed;

    public static IReadOnlyList<Entry> Items => items ??= Load();

    static List<Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(FilePath), SettingsStore.Json) ?? [];
        }
        catch (Exception ex) { Paths.Trace("история: " + ex.Message); }
        return [];
    }

    public static void Add(Track t)
    {
        items ??= Load();
        // повтор того же трека подряд (перемотка, повтор) не пишем
        if (items.Count > 0 && items[0].Track.Id == t.Id && (DateTime.Now - items[0].Played).TotalMinutes < 10) return;
        // ссылка VK живёт несколько часов — не храним; локальные файлы храним путь
        var copy = new Track
        {
            Id = t.Id, Title = t.Title, Artist = t.Artist, Cover = t.Cover, CoverBig = t.CoverBig, Duration = t.Duration,
            Source = t.Source, OwnerId = t.OwnerId, AudioId = t.AudioId, AccessKey = t.AccessKey,
            AlbumId = t.AlbumId, AlbumOwnerId = t.AlbumOwnerId, AlbumAccessKey = t.AlbumAccessKey, AlbumTitle = t.AlbumTitle,
            HasLyrics = t.HasLyrics, Url = t.Source == "Local" ? t.Url : "",
        };
        lock (gate)
        {
            items.Insert(0, new Entry { Track = copy, Played = DateTime.Now });
            if (items.Count > Max) items.RemoveRange(Max, items.Count - Max);
        }
        Changed?.Invoke();
        saveTimer ??= new System.Threading.Timer(_ => Save());
        saveTimer.Change(3000, Timeout.Infinite);
    }

    public static void Clear()
    {
        items = [];
        Save();
        Changed?.Invoke();
    }

    static void Save()
    {
        try
        {
            List<Entry> snapshot;
            lock (gate) snapshot = items?.ToList() ?? [];
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot, SettingsStore.Json));
        }
        catch (Exception ex) { Paths.Trace("история: " + ex.Message); }
    }
}
