using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoolPlayer;

/// <summary>Что остров может показывать в спокойном состоянии.</summary>
public enum InfoKind { Nothing, Clock, Weather, ClockWeather, Date, NextReminder, Custom }

public enum Repeat { None, Daily, Weekdays, Weekly }

public sealed class ScheduleRule
{
    public bool Enabled { get; set; } = true;
    public string From { get; set; } = "07:00";
    public string To { get; set; } = "09:00";
    public InfoKind Content { get; set; } = InfoKind.Weather;
    public string CustomText { get; set; } = "";
    public bool Mon { get; set; } = true;
    public bool Tue { get; set; } = true;
    public bool Wed { get; set; } = true;
    public bool Thu { get; set; } = true;
    public bool Fri { get; set; } = true;
    public bool Sat { get; set; } = true;
    public bool Sun { get; set; } = true;

    public bool Matches(DateTime now)
    {
        bool day = now.DayOfWeek switch
        {
            DayOfWeek.Monday => Mon, DayOfWeek.Tuesday => Tue, DayOfWeek.Wednesday => Wed,
            DayOfWeek.Thursday => Thu, DayOfWeek.Friday => Fri, DayOfWeek.Saturday => Sat, _ => Sun,
        };
        if (!Enabled || !day) return false;
        if (!TimeSpan.TryParse(From, out var a) || !TimeSpan.TryParse(To, out var b)) return false;
        var t = now.TimeOfDay;
        return a <= b ? t >= a && t < b : t >= a || t < b; // через полночь тоже работает
    }
}

public sealed class Reminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime When { get; set; }
    public Repeat Repeat { get; set; }
    public DateTime? SnoozedUntil { get; set; }

    [JsonIgnore] public DateTime DueAt => SnoozedUntil ?? When;

    [JsonIgnore]
    public string Display
    {
        get
        {
            string r = Repeat switch
            {
                Repeat.Daily => " · каждый день", Repeat.Weekdays => " · по будням",
                Repeat.Weekly => " · каждую неделю", _ => "",
            };
            return $"{When:dd.MM HH:mm}{r} — {Text}";
        }
    }

    /// <summary>Следующее срабатывание после «Готово» (null — разовое, удалить).</summary>
    public DateTime? Next(DateTime now)
    {
        if (Repeat == Repeat.None) return null;
        var c = now.Date + When.TimeOfDay;
        for (int i = 0; i < 400; i++, c = c.AddDays(1))
        {
            if (c <= now) continue;
            bool ok = Repeat switch
            {
                Repeat.Daily => true,
                Repeat.Weekdays => c.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
                Repeat.Weekly => c.DayOfWeek == When.DayOfWeek,
                _ => false,
            };
            if (ok) return c;
        }
        return null;
    }
}

public sealed class HotkeyBinding
{
    public bool Enabled { get; set; } = true;
    public uint Mods { get; set; }   // MOD_ALT=1, MOD_CONTROL=2, MOD_SHIFT=4, MOD_WIN=8
    public uint Vk { get; set; }
}

public sealed class GameOptions
{
    public bool IslandAnimations { get; set; } = true;   // true = отключать в игровом режиме
    public bool IslandEqualizer { get; set; } = true;
    public bool LyricsAnimations { get; set; } = true;
    public bool LyricsBackdrop { get; set; } = true;
    public bool EfficiencyMode { get; set; } = true;
    public bool LowPriority { get; set; } = true;
    public bool FastTabSleep { get; set; } = true;
    public bool TrimMemory { get; set; } = true;
    public bool AudioEqualizer { get; set; }             // эквалайзер звука тратит немного CPU
}

public sealed class AppSettings
{
    // вид
    public bool CollapseToStrip { get; set; } = true;
    [JsonIgnore] public bool KeepPill { get => !CollapseToStrip; set => CollapseToStrip = !value; }
    public bool StripFullWidth { get; set; } = true;
    public InfoKind IdleContent { get; set; } = InfoKind.ClockWeather;
    public double OffsetX { get; set; }
    public double IslandScale { get; set; }          // 0 — авто по разрешению монитора
    public bool AllMonitors { get; set; } = true;     // полоска на каждом мониторе с панелью задач
    public bool WinNotifications { get; set; } = true; // уведомления Windows в острове
    public bool HideWinBanners { get; set; } = true;   // прятать стандартные всплывающие окна, пока работает Taskbeat

    // события
    public bool TrackPeek { get; set; } = true;
    public bool ClipboardToast { get; set; } = true;
    public bool PowerToast { get; set; } = true;
    public bool ReminderSound { get; set; } = true;
    public bool StripProgress { get; set; } = true;

    // погода
    public string City { get; set; } = "";
    public string CityResolved { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }

    public ObservableCollection<ScheduleRule> Rules { get; set; } = [];
    public ObservableCollection<Reminder> Reminders { get; set; } = [];

    // плеер
    public double Volume { get; set; } = 0.8;
    public bool Shuffle { get; set; }
    public bool RepeatOne { get; set; }
    public bool StartMinimized { get; set; }
    public bool SuspendTabs { get; set; } = true;
    public string LastPage { get; set; } = "vk:my";
    /// <summary>Плавный переход между треками, секунды (0 — выключен).</summary>
    public int CrossfadeSec { get; set; }
    /// <summary>Источник текста песен: auto, vk, lrclib.</summary>
    public string LyricsSource { get; set; } = "auto";
    /// <summary>Треки с отметкой «Не нравится» — пропускаются.</summary>
    public List<string> Disliked { get; set; } = [];
    public string DownloadDir { get; set; } = "";
    /// <summary>classic — красота и плавность, game — минимум ресурсов.</summary>
    public string PerfMode { get; set; } = "classic";
    public bool Hotkeys { get; set; } = true;

    // эквалайзер: 10 полос, дБ
    public bool EqEnabled { get; set; }
    public double[] EqGains { get; set; } = new double[10];
    public string EqPreset { get; set; } = "flat";

    /// <summary>Переназначения горячих клавиш: действие → сочетание (null — по умолчанию).</summary>
    public Dictionary<string, HotkeyBinding> HotkeyMap { get; set; } = [];

    /// <summary>Что отключает игровой режим.</summary>
    public GameOptions Game { get; set; } = new();

    public AppSettings Clone() => JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, SettingsStore.Json), SettingsStore.Json)!;
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    static readonly string Dir = Paths.DataDir;
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>
    /// Файл настроек есть, но прочитать его не вышло (занят/недоступен). Тогда ни в коем случае
    /// не перезаписываем его умолчаниями — иначе настройки пропали бы насовсем.
    /// </summary>
    static bool protectFile;

    public static AppSettings Current { get; private set; } = Load();
    public static event Action? Changed;

    static AppSettings Load()
    {
        try
        {
            // переезд с отдельного Dynamic Island
            string old = Path.Combine(Path.GetDirectoryName(Dir)!, "DynamicIsland", "settings.json");
            if (!File.Exists(FilePath) && File.Exists(old))
            {
                Directory.CreateDirectory(Dir);
                File.Copy(old, FilePath);
            }
            foreach (var f in new[] { FilePath, FilePath + ".bak" })
            {
                if (!File.Exists(f)) continue;
                try
                {
                    var text = Paths.Retry(() => File.ReadAllText(f));
                    var s = JsonSerializer.Deserialize<AppSettings>(text, Json);
                    if (s != null)
                    {
                        Paths.Trace($"settings: загружены из {f}");
                        return s;
                    }
                }
                catch (JsonException ex)
                {
                    // битый файл — откладываем в сторону и пробуем резервную копию
                    Paths.Trace($"settings: битый {f}: {ex.Message}");
                    try { File.Copy(f, f + ".broken", true); } catch { }
                }
            }
            if (File.Exists(FilePath)) protectFile = true;
            Paths.Trace($"settings: нет файла в {Dir} — умолчания");
        }
        catch (Exception ex)
        {
            protectFile = File.Exists(FilePath);
            Paths.Trace($"settings: не прочитаны из {FilePath}: {ex.GetType().Name}: {ex.Message}");
        }
        return new AppSettings();
    }

    public static void Save()
    {
        if (protectFile)
        {
            Paths.Trace("settings: сохранение пропущено — исходный файл ещё не прочитан");
            return;
        }
        try
        {
            Paths.WriteAtomic(FilePath, JsonSerializer.SerializeToUtf8Bytes(Current, Json));
        }
        catch (Exception ex) { Paths.Trace($"settings: не сохранены: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Файл не прочитался при запуске — пробуем ещё раз через несколько секунд.</summary>
    public static async Task RecoverAsync()
    {
        foreach (var delay in new[] { 2000, 5000, 15000, 30000 })
        {
            if (!protectFile) return;
            await Task.Delay(delay);
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(FilePath), Json);
                if (s == null) continue;
                Current = s;
                protectFile = false;
                Paths.Trace("settings: прочитаны со второй попытки");
                Changed?.Invoke();
                return;
            }
            catch (Exception ex) { Paths.Trace($"settings: повтор не удался: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public static void Replace(AppSettings s)
    {
        Current = s;
        Save();
        Changed?.Invoke();
    }

    public static void Touch()
    {
        Save();
        Changed?.Invoke();
    }
}
