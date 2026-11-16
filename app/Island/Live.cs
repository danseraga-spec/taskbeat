using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Devices.Enumeration;

namespace CoolPlayer;

/// <summary>Длительная активность в пилюле острова: таймер, секундомер, помодоро, загрузка.</summary>
sealed record LiveInfo(string Kind, string Glyph, string Label, string Right, double Progress, Color Color);

/// <summary>
/// «Живые» активности острова: таймеры, загрузки, громкость, батарея Bluetooth-наушников и строка текста песни.
/// Короткие события (громкость, «время вышло», «загружено») уходят в остров как обычные уведомления.
/// </summary>
static class Live
{
    static readonly Color Orange = Color.FromRgb(255, 159, 10), Green = Color.FromRgb(48, 209, 88),
        Blue = Color.FromRgb(10, 132, 255), Red = Color.FromRgb(255, 69, 58), Purple = Color.FromRgb(191, 90, 242);

    public static event Action? Changed;
    static DispatcherTimer? tick;

    public static void Start()
    {
        tick = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => OnTick();
        tick.Start();
        WatchBrowserDownloads();
        VolumeWatch.Start();
        BtBattery.Start();
        PillLyrics.Start();
    }

    static void Flash(string left, string right, Color c, double sec = 2.4) => AppHost.Island?.Notify(left, right, c, sec);

    /// <summary>То, что сейчас занимает пилюлю (или null).</summary>
    public static LiveInfo? Current
    {
        get
        {
            if (timerEnd is { } end)
            {
                var left = end - DateTime.Now;
                if (left < TimeSpan.Zero) left = TimeSpan.Zero;
                double p = timerLen.TotalSeconds > 0 ? 1 - left.TotalSeconds / timerLen.TotalSeconds : 0;
                return pomodoro switch
                {
                    PomoPhase.Work => new LiveInfo("pomo", "", L.F("Фокус · {0}", pomoRound), Fmt(left), p, Red),
                    PomoPhase.Break => new LiveInfo("pomo", "", L.T("Перерыв"), Fmt(left), p, Green),
                    _ => new LiveInfo("timer", "", L.T("Таймер"), Fmt(left), p, Orange),
                };
            }
            if (stopwatchStart is { } st)
                return new LiveInfo("stopwatch", "", L.T("Секундомер"), Fmt(DateTime.Now - st), -1, Blue);
            var dl = downloads.Values.OrderByDescending(d => d.Started).FirstOrDefault();
            if (dl != null)
            {
                string right = dl.Progress >= 0 ? $"{dl.Progress * 100:0}%" : Size(dl.Bytes);
                string label = downloads.Count > 1 ? L.F("Загрузки · {0}", downloads.Count) : dl.Name;
                return new LiveInfo("download", "", label, right, dl.Progress, Blue);
            }
            return null;
        }
    }

    static void OnTick()
    {
        if (timerEnd is { } end && DateTime.Now >= end) TimerDone();
        foreach (var d in downloads.Values.Where(d => d.Path != null).ToList())
        {
            try { d.Bytes = new FileInfo(d.Path!).Length; } catch { }
            if ((DateTime.Now - d.Touched).TotalMinutes > 10) downloads.Remove(d.Key); // зависшая загрузка браузера
        }
        bool hasLine = PillLyrics.Line != null;
        if (timerEnd != null || stopwatchStart != null || downloads.Count > 0 || hasLine != lastHasLine) Changed?.Invoke();
        lastHasLine = hasLine;
    }

    static bool lastHasLine;

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
    static string Size(long b) => b >= 1 << 30 ? $"{b / (double)(1 << 30):0.0} ГБ" : b >= 1 << 20 ? $"{b / (double)(1 << 20):0} МБ" : $"{b / 1024} КБ";

    // ------------------------------------------------------------------ таймер, помодоро, секундомер

    enum PomoPhase { None, Work, Break }
    static DateTime? timerEnd, stopwatchStart;
    static TimeSpan timerLen;
    static PomoPhase pomodoro;
    static int pomoRound;

    public static bool TimerRunning => timerEnd != null;
    public static bool StopwatchRunning => stopwatchStart != null;

    public static void StartTimer(TimeSpan len)
    {
        pomodoro = PomoPhase.None;
        timerLen = len;
        timerEnd = DateTime.Now + len;
        Changed?.Invoke();
    }

    public static void StartPomodoro()
    {
        pomoRound = 1;
        pomodoro = PomoPhase.Work;
        timerLen = TimeSpan.FromMinutes(25);
        timerEnd = DateTime.Now + timerLen;
        Changed?.Invoke();
    }

    public static void StopTimer()
    {
        timerEnd = null;
        pomodoro = PomoPhase.None;
        Changed?.Invoke();
    }

    public static void ToggleStopwatch()
    {
        if (stopwatchStart is { } st)
        {
            var total = DateTime.Now - st;
            stopwatchStart = null;
            Flash(L.T("Секундомер"), Fmt(total), Blue, 4);
        }
        else stopwatchStart = DateTime.Now;
        Changed?.Invoke();
    }

    static void TimerDone()
    {
        if (SettingsStore.Current.ReminderSound) SystemSounds.Exclamation.Play();
        switch (pomodoro)
        {
            case PomoPhase.Work:
                // каждый четвёртый перерыв — длинный
                pomodoro = PomoPhase.Break;
                timerLen = TimeSpan.FromMinutes(pomoRound % 4 == 0 ? 15 : 5);
                timerEnd = DateTime.Now + timerLen;
                Flash(L.T("Помодоро"), L.T("пора отдохнуть"), Green, 5);
                break;
            case PomoPhase.Break:
                pomoRound++;
                pomodoro = PomoPhase.Work;
                timerLen = TimeSpan.FromMinutes(25);
                timerEnd = DateTime.Now + timerLen;
                Flash(L.T("Помодоро"), L.T("снова за работу"), Red, 5);
                break;
            default:
                timerEnd = null;
                Flash(L.T("Таймер"), L.T("время вышло"), Orange, 8);
                break;
        }
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ загрузки

    sealed class Download
    {
        public string Key = "", Name = "";
        public string? Path;
        public double Progress = -1;
        public long Bytes;
        public DateTime Started = DateTime.Now, Touched = DateTime.Now;
    }

    static readonly Dictionary<string, Download> downloads = [];

    /// <summary>Загрузка трека в самом Taskbeat.</summary>
    public static IProgress<double> TrackDownload(string name)
    {
        string key = "tb:" + Guid.NewGuid();
        downloads[key] = new Download { Key = key, Name = name, Progress = 0 };
        Changed?.Invoke();
        return new Progress<double>(p =>
        {
            if (!downloads.TryGetValue(key, out var d)) return;
            d.Progress = p;
            d.Touched = DateTime.Now;
            if (p >= 1)
            {
                downloads.Remove(key);
                Flash(L.T("Загружено"), name, Green, 2.6);
            }
            Changed?.Invoke();
        });
    }

    public static void DownloadFailed(IProgress<double> p) { foreach (var k in downloads.Keys.Where(k => k.StartsWith("tb:")).ToList()) downloads.Remove(k); Changed?.Invoke(); }

    static FileSystemWatcher? fsw;
    static readonly string[] TempExt = [".crdownload", ".part", ".partial", ".download"];

    /// <summary>Загрузки браузеров: временные файлы в папке «Загрузки».</summary>
    static void WatchBrowserDownloads()
    {
        try
        {
            string dir = KnownDownloads();
            if (!Directory.Exists(dir)) return;
            fsw = new FileSystemWatcher(dir) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size };
            var ui = System.Windows.Application.Current.Dispatcher;
            fsw.Created += (_, e) => ui.BeginInvoke(() => OnTemp(e.FullPath));
            fsw.Changed += (_, e) => ui.BeginInvoke(() => OnTemp(e.FullPath));
            fsw.Renamed += (_, e) => ui.BeginInvoke(() => OnRenamed(e.OldFullPath, e.FullPath));
            fsw.Deleted += (_, e) => ui.BeginInvoke(() => { if (downloads.Remove("fs:" + e.FullPath.ToLowerInvariant())) Changed?.Invoke(); });
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Paths.Trace("загрузки: " + ex.Message); }
    }

    static bool IsTemp(string p) => TempExt.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant());

    static string CleanName(string p)
    {
        string n = System.IO.Path.GetFileName(p);
        foreach (var e in TempExt) if (n.EndsWith(e, StringComparison.OrdinalIgnoreCase)) n = n[..^e.Length];
        // Chrome: «Неподтвержденный 123456.crdownload» — имя ещё неизвестно
        return n.StartsWith("Неподтвержд", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase) ? L.T("Загрузка файла") : n;
    }

    static void OnTemp(string path)
    {
        if (!SettingsStore.Current.IslandDownloads || !IsTemp(path)) return;
        string key = "fs:" + path.ToLowerInvariant();
        if (!downloads.TryGetValue(key, out var d))
        {
            downloads[key] = d = new Download { Key = key, Path = path, Name = CleanName(path) };
            Changed?.Invoke();
        }
        d.Touched = DateTime.Now;
    }

    static void OnRenamed(string from, string to)
    {
        if (IsTemp(from) && downloads.Remove("fs:" + from.ToLowerInvariant()))
        {
            if (IsTemp(to)) OnTemp(to);
            else Flash(L.T("Загружено"), System.IO.Path.GetFileName(to), Green, 2.6);
            Changed?.Invoke();
        }
        else if (IsTemp(to)) OnTemp(to);
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static string KnownDownloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
        {
            string s = Marshal.PtrToStringUni(p)!;
            Marshal.FreeCoTaskMem(p);
            return s;
        }
        return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ------------------------------------------------------------------ громкость

    static class VolumeWatch
    {
        static IAudioEndpointVolume? ep;
        static Callback? cb;
        static float last = -1;
        static bool lastMute;

        public static void Start()
        {
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(0, 1, out var dev)); // eRender, eMultimedia
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out var o)); // CLSCTX_ALL
                ep = (IAudioEndpointVolume)o;
                ep.GetMasterVolumeLevelScalar(out last);
                ep.GetMute(out lastMute);
                cb = new Callback();
                ep.RegisterControlChangeNotify(cb);
            }
            catch (Exception ex) { Paths.Trace("громкость: " + ex.Message); }
        }

        sealed class Callback : IAudioEndpointVolumeCallback
        {
            public int OnNotify(IntPtr data)
            {
                bool mute = Marshal.ReadInt32(data, 16) != 0;
                float vol = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
                if (Math.Abs(vol - last) < 0.004 && mute == lastMute) return 0;
                last = vol;
                lastMute = mute;
                if (!SettingsStore.Current.IslandVolume) return 0;
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    AppHost.Island?.ShowActivityAll(new ActivityInfo("volume", mute ? L.T("Без звука") : L.T("Громкость"), mute ? "" : $"{Math.Round(vol * 100)}", mute ? Red : Colors.White) { Value = mute ? 0 : vol }, 1.6));
                return 0;
            }
        }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice dev);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int ctx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o);
        }

        [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolume
        {
            [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
            [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
            [PreserveSig] int GetChannelCount(out uint n);
            [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
            [PreserveSig] int SetMasterVolumeLevelScalar(float v, ref Guid ctx);
            [PreserveSig] int GetMasterVolumeLevel(out float db);
            [PreserveSig] int GetMasterVolumeLevelScalar(out float v);
            [PreserveSig] int SetChannelVolumeLevel(uint ch, float db, ref Guid ctx);
            [PreserveSig] int SetChannelVolumeLevelScalar(uint ch, float v, ref Guid ctx);
            [PreserveSig] int GetChannelVolumeLevel(uint ch, out float db);
            [PreserveSig] int GetChannelVolumeLevelScalar(uint ch, out float v);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioEndpointVolumeCallback
        {
            [PreserveSig] int OnNotify(IntPtr data);
        }
    }

    // ------------------------------------------------------------------ батарея Bluetooth

    static class BtBattery
    {
        const string Key = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
        static readonly Dictionary<string, int> known = [];
        static bool first = true;

        public static void Start()
        {
            var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
            t.Tick += async (_, _) => await PollAsync();
            t.Start();
            _ = PollAsync();
        }

        static async Task PollAsync()
        {
            if (!SettingsStore.Current.IslandBtBattery) return;
            try
            {
                var list = await DeviceInformation.FindAllAsync("System.Devices.Present:=System.StructuredQueryType.Boolean#True", [Key, "System.ItemNameDisplay"], DeviceInformationKind.Device);
                var seen = new HashSet<string>();
                foreach (var d in list)
                {
                    if (!d.Properties.TryGetValue(Key, out var v) || v is not byte b) continue;
                    int pct = b;
                    string name = d.Name;
                    seen.Add(name);
                    bool isNew = !known.TryGetValue(name, out int old);
                    known[name] = pct;
                    if (first) continue;
                    if (isNew) Flash(Short(name), $"{pct}%", pct <= 15 ? Red : Green, 3);
                    else if (pct <= 15 && old > 15) Flash(Short(name), L.F("{0}% — пора зарядить", pct), Red, 4);
                }
                foreach (var gone in known.Keys.Where(k => !seen.Contains(k)).ToList()) known.Remove(gone);
                first = false;
            }
            catch (Exception ex) { Paths.Trace("батарея BT: " + ex.Message); }
        }

        static string Short(string n)
        {
            n = n.Replace("Hands-Free AG", "").Replace("Hands-Free", "").Replace("Avrcp Transport", "").Trim();
            return n.Length > 24 ? n[..24] + "…" : n;
        }
    }

    // ------------------------------------------------------------------ текст песни в пилюле

    public static class PillLyrics
    {
        static Lyrics? lyrics;
        static string key = "";

        public static void Start()
        {
            AppHost.Media.Changed += changed => { _ = LoadAsync(); };
        }

        static async Task LoadAsync()
        {
            var m = AppHost.Media.State;
            if (!SettingsStore.Current.IslandLyrics || !m.Has) { lyrics = null; key = ""; return; }
            string k = $"{m.Artist}|{m.Title}";
            if (k == key) return;
            key = k;
            lyrics = null;
            var p = AppHost.Player.Current;
            var vk = p != null && p.Title == m.Title ? p : null;
            try
            {
                var l = await LyricsService.GetAsync(SettingsStore.Current.LyricsSource, vk, m.Artist, m.Title, m.Dur);
                if (key == k && l is { Synced: true }) lyrics = l;
            }
            catch { }
        }

        /// <summary>Текущая строка (синхронный текст), иначе null.</summary>
        public static string? Line
        {
            get
            {
                if (lyrics == null || !SettingsStore.Current.IslandLyrics) return null;
                var m = AppHost.Media.State;
                if (!m.Has || !m.Playing || $"{m.Artist}|{m.Title}" != key) return null;
                double pos = m.Position + 0.3;
                LyricLine? cur = null;
                foreach (var l in lyrics.Lines) { if (l.Time <= pos) cur = l; else break; }
                return cur is { Text.Length: > 0 } c ? c.Text : null;
            }
        }
    }
}
