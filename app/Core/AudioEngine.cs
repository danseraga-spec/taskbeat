using global::Windows.Media;
using global::Windows.Media.Audio;
using global::Windows.Media.Core;
using global::Windows.Media.Playback;
using global::Windows.Media.Render;
using global::Windows.Storage.Streams;

namespace CoolPlayer;

/// <summary>Одна «дека» воспроизведения: системный MediaPlayer или узел AudioGraph с эквалайзером.</summary>
interface IDeck : IDisposable
{
    Task<bool> LoadAsync(MediaSource src, Track t);
    void Play();
    void Pause();
    void Unload();
    bool Playing { get; }
    bool Loaded { get; }
    double Position { get; }
    double Duration { get; }
    double Gain { set; }
    void Seek(double sec);
    event Action<IDeck>? Ended;
    event Action<IDeck, string>? Failed;
    event Action<IDeck>? StateChanged;
}

// ====================================================================== MediaPlayer

/// <summary>Системный MediaPlayer: аппаратные декодеры, сам ведёт карточку в медиа-панели Windows.</summary>
sealed class MpDeck : IDeck
{
    public readonly MediaPlayer Mp = new() { AudioCategory = MediaPlayerAudioCategory.Media };
    public event Action<IDeck>? Ended;
    public event Action<IDeck, string>? Failed;
    public event Action<IDeck>? StateChanged;

    public MpDeck(Action next, Action prev)
    {
        Mp.CommandManager.IsEnabled = false;
        var cm = Mp.CommandManager;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; next(); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; prev(); };
        Mp.MediaEnded += (_, _) => Ended?.Invoke(this);
        Mp.MediaFailed += (_, e) => Failed?.Invoke(this, e.ErrorMessage);
        Mp.PlaybackSession.PlaybackStateChanged += (_, _) => StateChanged?.Invoke(this);
    }

    /// <summary>Управлять ли системной карточкой (её ведёт только активная дека).</summary>
    public bool Smtc { set => Mp.CommandManager.IsEnabled = value; }

    public Task<bool> LoadAsync(MediaSource src, Track t)
    {
        var item = new MediaPlaybackItem(src);
        var p = item.GetDisplayProperties();
        p.Type = MediaPlaybackType.Music;
        p.MusicProperties.Title = t.Title;
        p.MusicProperties.Artist = t.Artist;
        if (t.AlbumTitle != null) p.MusicProperties.AlbumTitle = t.AlbumTitle;
        var cover = t.CoverBig ?? t.Cover;
        if (cover != null) p.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(cover));
        item.ApplyDisplayProperties(p);
        Mp.Source = item;
        return Task.FromResult(true);
    }

    public void Play() => Mp.Play();
    public void Pause() => Mp.Pause();
    public void Unload() { Mp.Pause(); Mp.Source = null; }
    public bool Playing => Mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
    public bool Loaded => Mp.Source != null;
    public double Position => Mp.PlaybackSession.Position.TotalSeconds;
    public double Duration => Mp.PlaybackSession.NaturalDuration.TotalSeconds;
    public double Gain { set => Mp.Volume = Math.Clamp(value, 0, 1); }
    public void Seek(double sec) => Mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, sec));
    public void Dispose() { Mp.Pause(); Mp.Dispose(); }
}

// ====================================================================== AudioGraph + эквалайзер

/// <summary>
/// Общий граф: деки → устройство вывода. На каждой деке — три встроенных эквалайзера Windows
/// (по 4 полосы), из них собраны 10 полос. Граф работает только пока что-то играет.
/// </summary>
sealed class GraphEngine : IDisposable
{
    public static readonly double[] Freqs = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    public AudioGraph? Graph { get; private set; }
    public AudioDeviceOutputNode? Output { get; private set; }
    bool running;
    public event Action? Broken;
    public readonly List<GraphDeck> Decks = [];

    public async Task<bool> EnsureAsync()
    {
        if (Graph != null) return true;
        var r = await AudioGraph.CreateAsync(new AudioGraphSettings(AudioRenderCategory.Media));
        if (r.Status != AudioGraphCreationStatus.Success) return false;
        var o = await r.Graph.CreateDeviceOutputNodeAsync();
        if (o.Status != AudioDeviceNodeCreationStatus.Success) { r.Graph.Dispose(); return false; }
        Graph = r.Graph;
        Output = o.DeviceOutputNode;
        Graph.UnrecoverableErrorOccurred += (_, _) => Broken?.Invoke(); // например, сменили устройство вывода
        return true;
    }

    public void UpdateRunning()
    {
        if (Graph == null) return;
        bool need = Decks.Any(d => d.Playing);
        if (need == running) return;
        running = need;
        if (need) Graph.Start(); else Graph.Stop();
    }

    /// <summary>дБ → линейное усиление в пределах встроенного эквалайзера (−18…+18 дБ).</summary>
    public static double Linear(double db) => Math.Clamp(Math.Pow(10, db / 20), 0.126, 7.94);

    public void Reset()
    {
        running = false;
        foreach (var d in Decks) d.Drop();
        try { Graph?.Dispose(); } catch { }
        Graph = null;
        Output = null;
    }

    public void Dispose() => Reset();
}

sealed class GraphDeck : IDeck
{
    readonly GraphEngine engine;
    MediaSourceAudioInputNode? node;
    readonly List<EqualizerEffectDefinition> eqs = [];
    bool playing;
    double gain = 1;

    public event Action<IDeck>? Ended;
    public event Action<IDeck, string>? Failed { add { } remove { } } // ошибки узла приходят при создании
    public event Action<IDeck>? StateChanged;

    public GraphDeck(GraphEngine engine)
    {
        this.engine = engine;
        engine.Decks.Add(this);
    }

    public async Task<bool> LoadAsync(MediaSource src, Track t)
    {
        Unload();
        if (!await engine.EnsureAsync()) return false;
        var r = await engine.Graph!.CreateMediaSourceAudioInputNodeAsync(src);
        if (r.Status != MediaSourceAudioInputNodeCreationStatus.Success)
        {
            Program.Log(new Exception($"AudioGraph: {r.Status} {r.ExtendedError?.Message}"));
            return false;
        }
        var n = r.Node;
        n.OutgoingGain = gain;
        eqs.Clear();
        for (int i = 0; i < 3; i++)
        {
            var eq = new EqualizerEffectDefinition(engine.Graph);
            eqs.Add(eq);
            n.EffectDefinitions.Add(eq);
        }
        ApplyEq();
        n.MediaSourceCompleted += (_, _) => { playing = false; Ended?.Invoke(this); };
        n.Stop();
        n.AddOutgoingConnection(engine.Output);
        node = n;
        return true;
    }

    /// <summary>Перенести текущие настройки эквалайзера на узел (работает на лету).</summary>
    public void ApplyEq()
    {
        var g = SettingsStore.Current.EqGains;
        for (int i = 0; i < 12; i++)
        {
            var band = eqs.Count == 3 ? eqs[i / 4].Bands[i % 4] : null;
            if (band == null) continue;
            if (i < GraphEngine.Freqs.Length)
            {
                band.FrequencyCenter = GraphEngine.Freqs[i];
                band.Bandwidth = 1.5;
                band.Gain = GraphEngine.Linear(i < g.Length ? g[i] : 0);
            }
            else band.Gain = 1; // две запасные полосы — нейтральны
        }
    }

    public void Play()
    {
        if (node == null) return;
        node.Start();
        playing = true;
        engine.UpdateRunning();
        StateChanged?.Invoke(this);
    }

    public void Pause()
    {
        if (node == null) return;
        node.Stop();
        playing = false;
        engine.UpdateRunning();
        StateChanged?.Invoke(this);
    }

    public void Unload()
    {
        if (node == null) return;
        try { node.Stop(); node.Dispose(); } catch { }
        node = null;
        playing = false;
        engine.UpdateRunning();
    }

    /// <summary>Граф пересоздаётся — узел уже недействителен.</summary>
    public void Drop() { node = null; playing = false; }

    public bool Playing => playing;
    public bool Loaded => node != null;
    public double Position => node?.Position.TotalSeconds ?? 0;
    public double Duration => node?.Duration.TotalSeconds ?? 0;
    public double Gain { set { gain = Math.Clamp(value, 0, 1); if (node != null) node.OutgoingGain = gain; } }
    public void Seek(double sec) => node?.Seek(TimeSpan.FromSeconds(Math.Max(0, sec)));
    public void Dispose() => Unload();
}

// ====================================================================== карточка Windows для AudioGraph

/// <summary>Медиа-карточка Windows (и остров) для режима с эквалайзером — её ведём сами.</summary>
sealed class SmtcBridge
{
    SystemMediaTransportControls? s;
    readonly Action toggle, next, prev;
    readonly Action<double> seek;

    public SmtcBridge(Action toggle, Action next, Action prev, Action<double> seek)
    {
        this.toggle = toggle; this.next = next; this.prev = prev; this.seek = seek;
    }

    bool Ensure()
    {
        if (s != null) return true;
        var hwnd = AppHost.Island?.Handle ?? IntPtr.Zero;
        if (hwnd == IntPtr.Zero) return false;
        try
        {
            s = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            s.IsPlayEnabled = s.IsPauseEnabled = s.IsNextEnabled = s.IsPreviousEnabled = true;
            s.ButtonPressed += (_, e) =>
            {
                switch (e.Button)
                {
                    case SystemMediaTransportControlsButton.Play:
                    case SystemMediaTransportControlsButton.Pause: toggle(); break;
                    case SystemMediaTransportControlsButton.Next: next(); break;
                    case SystemMediaTransportControlsButton.Previous: prev(); break;
                }
            };
            s.PlaybackPositionChangeRequested += (_, e) => seek(e.RequestedPlaybackPosition.TotalSeconds);
            return true;
        }
        catch (Exception ex) { Program.Log(ex); s = null; return false; }
    }

    public bool Enabled
    {
        set
        {
            if (!value && s == null) return;
            if (!Ensure()) return;
            s!.IsEnabled = value;
            if (!value) s.DisplayUpdater.ClearAll();
        }
    }

    public void Show(Track t)
    {
        if (!Ensure()) return;
        var u = s!.DisplayUpdater;
        u.Type = MediaPlaybackType.Music;
        u.MusicProperties.Title = t.Title;
        u.MusicProperties.Artist = t.Artist;
        u.MusicProperties.AlbumTitle = t.AlbumTitle ?? "";
        var cover = t.CoverBig ?? t.Cover;
        u.Thumbnail = cover != null ? RandomAccessStreamReference.CreateFromUri(new Uri(cover)) : null;
        u.Update();
    }

    public void State(bool playing, double pos, double dur)
    {
        if (!Ensure()) return;
        s!.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        if (dur > 0)
        {
            s.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero, MinSeekTime = TimeSpan.Zero, Position = TimeSpan.FromSeconds(pos),
                MaxSeekTime = TimeSpan.FromSeconds(dur), EndTime = TimeSpan.FromSeconds(dur),
            });
        }
    }
}
