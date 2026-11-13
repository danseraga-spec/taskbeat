using System.Collections.ObjectModel;
using System.Windows.Threading;
using global::Windows.Media.Core;
using global::Windows.Media.Streaming.Adaptive;

namespace CoolPlayer;

public sealed class Track
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Url { get; set; } = "";
    public string? Cover { get; init; }
    public string? CoverBig { get; init; }
    public double Duration { get; init; }
    public string Source { get; init; } = "VK";
    public long OwnerId { get; init; }
    public long AudioId { get; init; }
    public string? AccessKey { get; init; }
    public long AlbumId { get; init; }
    public long AlbumOwnerId { get; init; }
    public string? AlbumAccessKey { get; init; }
    public string? AlbumTitle { get; init; }
    public bool HasLyrics { get; init; }

    public string DurationText => Duration <= 0 ? "" : $"{(int)Duration / 60}:{(int)Duration % 60:00}";
    public bool Playable => Url.Length > 0;
}

/// <summary>
/// Воспроизведение. Два движка:
///   • системный MediaPlayer — аппаратные декодеры, минимум CPU (по умолчанию);
///   • AudioGraph — когда включён эквалайзер (10 полос).
/// В каждом по две «деки» для плавного перехода между треками. Карточку в медиа-панели
/// Windows (её видит остров) ведёт только активная дека.
/// </summary>
sealed class Player : IDisposable
{
    readonly MpDeck[] mp;
    readonly GraphDeck[] gd;
    readonly GraphEngine graph = new();
    readonly SmtcBridge smtc;
    readonly Dispatcher ui;
    readonly Random rnd = new();
    readonly DispatcherTimer tick;
    IDeck[] decks;
    List<Track> order = [];
    int index = -1;
    int loadVersion;
    int cur;
    bool fading, preparing, graphFailed;
    DateTime fadeStart;
    double fadeLen;

    IDeck Deck => decks[cur];
    IDeck Other => decks[1 - cur];
    bool UseGraph => SettingsStore.Current.EqEnabled && !Perf.NoAudioEq && !graphFailed;

    public ObservableCollection<Track> Queue { get; } = [];
    public Track? Current { get; private set; }
    public bool IsPlaying => Deck.Playing;
    public double Position => Deck.Position;
    public double Duration => Deck.Duration;
    public event Action? Changed;
    public event Action<string>? Error;
    public event Action? SleepChanged;

    // ------------------------------------------------------------------ настройки

    public double Volume
    {
        get => SettingsStore.Current.Volume;
        set
        {
            SettingsStore.Current.Volume = Math.Clamp(value, 0, 1);
            if (!fading) Deck.Gain = SettingsStore.Current.Volume;
        }
    }

    public bool Shuffle
    {
        get => SettingsStore.Current.Shuffle;
        set { SettingsStore.Current.Shuffle = value; RebuildOrder(); SettingsStore.Save(); Changed?.Invoke(); }
    }

    public bool RepeatOne
    {
        get => SettingsStore.Current.RepeatOne;
        set { SettingsStore.Current.RepeatOne = value; SettingsStore.Save(); Changed?.Invoke(); }
    }

    public int Crossfade
    {
        get => SettingsStore.Current.CrossfadeSec;
        set { SettingsStore.Current.CrossfadeSec = Math.Clamp(value, 0, 12); SettingsStore.Save(); UpdateTick(); }
    }

    // ------------------------------------------------------------------ эквалайзер

    public bool EqActive => ReferenceEquals(decks, gd);

    /// <summary>Эквалайзер включили/выключили: текущий трек продолжается на другом движке с той же позиции.</summary>
    public void EqChanged()
    {
        if (SettingsStore.Current.EqEnabled) graphFailed = false; // даём графу новый шанс
        if (UseGraph == EqActive) { EqGainsChanged(); return; }
        double pos = Position;
        bool was = IsPlaying;
        StopFade();
        Deck.Unload();
        SelectEngine();
        if (Current != null && index >= 0) _ = LoadAsync(pos, was);
    }

    /// <summary>Полосы поменялись — применяем на лету.</summary>
    public void EqGainsChanged()
    {
        foreach (var d in gd) d.ApplyEq();
    }

    void SelectEngine()
    {
        decks = UseGraph ? gd : mp;
        cur = 0;
        bool g = EqActive;
        foreach (var m in mp) m.Smtc = false;
        if (!g) mp[0].Smtc = true;
        smtc.Enabled = g;
    }

    // ------------------------------------------------------------------ таймер сна

    public DateTime? SleepAt { get; private set; }
    public bool SleepAfterTrack { get; private set; }

    public void SetSleep(int minutes)
    {
        SleepAt = minutes > 0 ? DateTime.Now.AddMinutes(minutes) : null;
        SleepAfterTrack = false;
        UpdateTick();
        SleepChanged?.Invoke();
    }

    public void SetSleepAfterTrack()
    {
        SleepAt = null;
        SleepAfterTrack = true;
        SleepChanged?.Invoke();
    }

    void SleepNow()
    {
        SleepAt = null;
        SleepAfterTrack = false;
        Deck.Pause();
        Deck.Gain = Volume;
        SleepChanged?.Invoke();
        Error?.Invoke("Таймер сна: музыка на паузе");
    }

    // ------------------------------------------------------------------

    public Player(Dispatcher ui)
    {
        this.ui = ui;
        void N() => ui.InvokeAsync(Next);
        void P() => ui.InvokeAsync(Previous);
        mp = [new MpDeck(N, P), new MpDeck(N, P)];
        gd = [new GraphDeck(graph), new GraphDeck(graph)];
        smtc = new SmtcBridge(() => ui.InvokeAsync(Toggle), N, P, s => ui.InvokeAsync(() => Seek(s)));
        foreach (var d in mp) Wire(d);
        foreach (var d in gd) Wire(d);
        graph.Broken += () => ui.InvokeAsync(() =>
        {
            // устройство вывода сменилось — пересоздаём граф и продолжаем с того же места
            double pos = Position;
            bool was = IsPlaying;
            graph.Reset();
            if (EqActive && Current != null) _ = LoadAsync(pos, was);
        });
        decks = mp;
        tick = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(200) };
        tick.Tick += (_, _) => OnTick();
        SelectEngine();
        Deck.Gain = Volume;
        Perf.Changed += () => ui.InvokeAsync(EqChanged); // игровой режим может выключать эквалайзер
    }

    void Wire(IDeck d)
    {
        d.Ended += s => ui.InvokeAsync(() =>
        {
            if (s != Deck) return; // закончилась уходящая дека во время перехода
            if (SleepAfterTrack) { SleepNow(); return; }
            if (RepeatOne) Seek(0); else Next();
        });
        d.Failed += (s, msg) => ui.InvokeAsync(() =>
        {
            if (s != Deck) return;
            Error?.Invoke("Не удалось воспроизвести: " + msg);
            Next();
        });
        d.StateChanged += s => ui.InvokeAsync(() =>
        {
            if (s != Deck) return;
            UpdateTick();
            PushSmtc();
            Changed?.Invoke();
        });
    }

    void PushSmtc()
    {
        if (EqActive) smtc.State(IsPlaying, Position, Duration);
    }

    public void Play(IEnumerable<Track> tracks, Track start)
    {
        Queue.Clear();
        foreach (var t in tracks.Where(t => t.Playable)) Queue.Add(t);
        RebuildOrder(start);
        StopFade();
        _ = LoadAsync();
    }

    void RebuildOrder(Track? first = null)
    {
        first ??= Current;
        order = [.. Queue];
        if (Shuffle)
        {
            for (int i = order.Count - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }
            if (first != null && order.Remove(first)) order.Insert(0, first);
        }
        index = first == null ? 0 : Math.Max(0, order.IndexOf(first));
    }

    async Task<MediaSource?> CreateSourceAsync(Track t, int ver)
    {
        var uri = new Uri(t.Url);
        if (!t.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return MediaSource.CreateFromUri(uri);
        var r = await AdaptiveMediaSource.CreateFromUriAsync(uri);
        if (ver != loadVersion) return null;
        if (r.Status != AdaptiveMediaSourceCreationStatus.Success)
        {
            Error?.Invoke($"Поток трека недоступен ({r.Status})");
            return null;
        }
        return MediaSource.CreateFromAdaptiveMediaSource(r.MediaSource);
    }

    /// <summary>Загрузить трек в деку. Если граф не справился с потоком — откат на MediaPlayer.</summary>
    async Task<IDeck?> LoadIntoAsync(IDeck deck, Track t, int ver)
    {
        if (deck is GraphDeck && !EqActive) deck = Deck;
        var src = await CreateSourceAsync(t, ver);
        if (src == null || ver != loadVersion) return null;
        if (await deck.LoadAsync(src, t)) return deck;
        if (deck is GraphDeck)
        {
            graphFailed = true;
            Error?.Invoke("Эквалайзер не поддерживает этот поток — играю без него");
            SelectEngine();
            src = await CreateSourceAsync(t, ver);
            if (src == null || ver != loadVersion) return null;
            return await Deck.LoadAsync(src, t) ? Deck : null;
        }
        return null;
    }

    async Task LoadAsync(double startAt = 0, bool play = true)
    {
        if (index < 0 || index >= order.Count) return;
        int ver = ++loadVersion;
        var t = order[index];
        Current = t;
        if (EqActive) smtc.Enabled = true;
        Changed?.Invoke();
        try
        {
            if (await LoadIntoAsync(Deck, t, ver) is not { } d || ver != loadVersion) return;
            d.Gain = Volume;
            if (startAt > 0) d.Seek(startAt);
            if (EqActive) smtc.Show(t);
            if (play) d.Play();
            PushSmtc();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            Error?.Invoke("Ошибка воспроизведения");
        }
    }

    // ------------------------------------------------------------------ плавный переход

    void UpdateTick()
    {
        bool need = fading || (IsPlaying && (Crossfade > 0 || SleepAt != null));
        if (need != tick.IsEnabled) tick.IsEnabled = need;
    }

    void OnTick()
    {
        if (preparing) return;
        if (SleepAt is { } at && DateTime.Now >= at.AddSeconds(-6))
        {
            double left = (at - DateTime.Now).TotalSeconds;
            if (left <= 0) { SleepNow(); UpdateTick(); return; }
            if (!fading) Deck.Gain = Volume * Math.Clamp(left / 6, 0, 1);
        }

        if (fading)
        {
            double k = Math.Clamp((DateTime.Now - fadeStart).TotalSeconds / fadeLen, 0, 1);
            // равная мощность: синус/косинус — без провала громкости посередине
            Deck.Gain = Volume * Math.Sin(k * Math.PI / 2);
            Other.Gain = Volume * Math.Cos(k * Math.PI / 2);
            if (k >= 1) StopFade();
            return;
        }

        int cf = Crossfade;
        if (cf <= 0 || RepeatOne || SleepAfterTrack || order.Count < 2 || !IsPlaying) return;
        double dur = Duration, pos = Position;
        if (dur < cf * 3 || double.IsNaN(dur)) return;
        if (dur - pos <= cf) _ = StartCrossfadeAsync(cf, dur - pos);
    }

    async Task StartCrossfadeAsync(int cf, double remaining)
    {
        if (fading || preparing) return;
        int next = NextIndex(index);
        if (next < 0) return;
        preparing = true;
        int ver = ++loadVersion;
        var t = order[next];
        try
        {
            var incoming = Other;
            var loaded = await LoadIntoAsync(incoming, t, ver);
            if (loaded != incoming || ver != loadVersion || incoming != Other) { preparing = false; return; }
            incoming.Gain = 0;
            incoming.Play();

            // меняем активную деку: системная карточка и остров сразу показывают новый трек
            if (Deck is MpDeck a) a.Smtc = false;
            cur = 1 - cur;
            if (Deck is MpDeck b) b.Smtc = true;
            if (EqActive) smtc.Show(t);
            index = next;
            Current = t;
            fadeStart = DateTime.Now;
            fadeLen = Math.Max(1, Math.Min(cf, remaining));
            fading = true;
            preparing = false;
            UpdateTick();
            PushSmtc();
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Program.Log(ex);
            preparing = false;
        }
    }

    void StopFade()
    {
        if (!fading && !Other.Loaded) return;
        fading = false;
        Other.Unload();
        Deck.Gain = Volume;
        UpdateTick();
    }

    // ------------------------------------------------------------------ управление

    int NextIndex(int from)
    {
        var bad = SettingsStore.Current.Disliked;
        for (int step = 1; step <= order.Count; step++)
        {
            int i = (from + step) % order.Count;
            if (!bad.Contains(order[i].Id)) return i;
        }
        return -1;
    }

    public void Toggle()
    {
        if (Current == null) return;
        if (IsPlaying) { Deck.Pause(); StopFade(); }
        else if (Deck.Loaded) Deck.Play();
        else _ = LoadAsync();
        PushSmtc();
    }

    public void Next()
    {
        if (order.Count == 0) return;
        StopFade();
        int n = NextIndex(index);
        if (n < 0) return;
        index = n;
        _ = LoadAsync();
    }

    public void Previous()
    {
        if (order.Count == 0) return;
        StopFade();
        if (Position > 3) { Seek(0); return; }
        index = (index - 1 + order.Count) % order.Count;
        _ = LoadAsync();
    }

    public void Seek(double seconds)
    {
        StopFade();
        Deck.Seek(seconds);
        PushSmtc();
    }

    /// <summary>Поставить трек следующим в очереди.</summary>
    public void PlayNext(Track t)
    {
        if (!t.Playable) return;
        if (Current == null) { Play([t], t); return; }
        order.Remove(t);
        order.Insert(Math.Min(index + 1, order.Count), t);
        if (!Queue.Contains(t)) Queue.Add(t);
    }

    /// <summary>«Не нравится»: запомнить и пропустить, если играет.</summary>
    public void Dislike(Track t)
    {
        var bad = SettingsStore.Current.Disliked;
        if (!bad.Contains(t.Id)) bad.Add(t.Id);
        SettingsStore.Save();
        if (Current?.Id == t.Id) Next();
    }

    public void Dispose()
    {
        tick.Stop();
        foreach (var d in mp) d.Dispose();
        foreach (var d in gd) d.Dispose();
        graph.Dispose();
    }
}
