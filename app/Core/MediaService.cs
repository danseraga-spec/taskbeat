using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GSMTC = global::Windows.Media.Control;
using Streams = global::Windows.Storage.Streams;

namespace CoolPlayer;

sealed class MediaState
{
    public bool Has, Playing;
    public string Title = "", Artist = "", Key = "", App = "";
    public double Pos, Dur;
    public long Stamp = Stopwatch.GetTimestamp();
    public BitmapSource? Art;
    public Color Accent = Color.FromRgb(235, 235, 240);

    public double Position
    {
        get
        {
            double p = Pos;
            if (Playing) p += (Stopwatch.GetTimestamp() - Stamp) / (double)Stopwatch.Frequency;
            return Dur > 0 ? Math.Clamp(p, 0, Dur) : p;
        }
    }

    public MediaState Clone() => (MediaState)MemberwiseClone();
}

/// <summary>
/// Текущая медиа-сессия Windows (Spotify, браузеры, плееры). Работает только на событиях
/// системы — никакого опроса. Все обращения происходят в UI-потоке.
/// </summary>
sealed class MediaService(Dispatcher ui)
{
    GSMTC.GlobalSystemMediaTransportControlsSessionManager? mgr;
    GSMTC.GlobalSystemMediaTransportControlsSession? cur;
    string title = "", artist = "", key = "";
    BitmapSource? art;
    Color accent = Color.FromRgb(235, 235, 240);

    public MediaState State { get; private set; } = new();
    /// <summary>Аргумент — сменился трек.</summary>
    public event Action<bool>? Changed;

    public async void Start()
    {
        try
        {
            mgr = await GSMTC.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            mgr.CurrentSessionChanged += (_, _) => ui.InvokeAsync(Attach);
            mgr.SessionsChanged += (_, _) => ui.InvokeAsync(Attach);
            await ui.InvokeAsync(Attach);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    void Attach()
    {
        var s = mgr?.GetCurrentSession();
        if (cur != null)
        {
            cur.MediaPropertiesChanged -= OnProps;
            cur.PlaybackInfoChanged -= OnPlayback;
            cur.TimelinePropertiesChanged -= OnTimeline;
        }
        cur = s;
        if (cur != null)
        {
            cur.MediaPropertiesChanged += OnProps;
            cur.PlaybackInfoChanged += OnPlayback;
            cur.TimelinePropertiesChanged += OnTimeline;
        }
        _ = Refresh(true);
    }

    void OnProps(GSMTC.GlobalSystemMediaTransportControlsSession s, GSMTC.MediaPropertiesChangedEventArgs a) => ui.InvokeAsync(() => _ = Refresh(true));
    void OnPlayback(GSMTC.GlobalSystemMediaTransportControlsSession s, GSMTC.PlaybackInfoChangedEventArgs a) => ui.InvokeAsync(() => _ = Refresh(false));
    void OnTimeline(GSMTC.GlobalSystemMediaTransportControlsSession s, GSMTC.TimelinePropertiesChangedEventArgs a) => ui.InvokeAsync(() => _ = Refresh(false));

    async Task Refresh(bool props)
    {
        var s = cur;
        if (s == null)
        {
            title = artist = key = "";
            art = null;
            Publish(false, null);
            return;
        }
        bool trackChanged = false;
        try
        {
            if (props)
            {
                var p = await s.TryGetMediaPropertiesAsync();
                if (s != cur) return;
                string t = p?.Title ?? "";
                string ar = string.IsNullOrEmpty(p?.Artist) ? p?.AlbumArtist ?? "" : p!.Artist;
                string k = s.SourceAppUserModelId + "|" + t + "|" + ar;
                if (k != key)
                {
                    key = k; title = t; artist = ar; art = null;
                    accent = Color.FromRgb(235, 235, 240);
                    trackChanged = t.Length > 0;
                }
                if (art == null && p?.Thumbnail != null) _ = LoadArt(p.Thumbnail, k);
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
        Publish(trackChanged, s);
    }

    void Publish(bool trackChanged, GSMTC.GlobalSystemMediaTransportControlsSession? s)
    {
        var st = new MediaState
        {
            Has = s != null && title.Length > 0,
            Title = title, Artist = artist, Key = key, Art = art, Accent = accent,
            App = s == null ? "" : FriendlyApp(s.SourceAppUserModelId),
        };
        if (s != null)
        {
            try
            {
                st.Playing = s.GetPlaybackInfo()?.PlaybackStatus ==
                             GSMTC.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var tl = s.GetTimelineProperties();
                st.Dur = (tl.EndTime - tl.StartTime).TotalSeconds;
                st.Pos = (tl.Position - tl.StartTime).TotalSeconds;
                if (st.Playing)
                {
                    double d = (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                    if (d > 0 && d < 36000) st.Pos += d;
                }
                if (st.Dur < 1) st.Dur = 0;
            }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }
        st.Stamp = Stopwatch.GetTimestamp();
        State = st;
        Changed?.Invoke(trackChanged);
    }

    async Task LoadArt(Streams.IRandomAccessStreamReference r, string k)
    {
        try
        {
            using var ras = await r.OpenReadAsync();
            if (k != key) return;
            using var src = ras.AsStreamForRead();
            var ms = new MemoryStream();
            await src.CopyToAsync(ms);
            if (ms.Length == 0 || k != key) return;
            ms.Position = 0;

            BitmapSource bmp = BitmapDecoder.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            int side = Math.Min(bmp.PixelWidth, bmp.PixelHeight);
            BitmapSource sq = new CroppedBitmap(bmp, new Int32Rect((bmp.PixelWidth - side) / 2, (bmp.PixelHeight - side) / 2, side, side));
            const int max = 192; // больше на экране не нужно — экономим память
            if (side > max) sq = new TransformedBitmap(sq, new ScaleTransform((double)max / side, (double)max / side));
            var final = new WriteableBitmap(new FormatConvertedBitmap(sq, PixelFormats.Pbgra32, null, 0));
            final.Freeze();
            if (k != key) return;
            art = final;
            accent = AverageColor(final);
            Publish(false, cur);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    /// <summary>Средний цвет обложки с упором на насыщенные пиксели.</summary>
    static Color AverageColor(BitmapSource b)
    {
        int w = b.PixelWidth, h = b.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        b.CopyPixels(px, stride, 0);
        double r = 0, g = 0, bl = 0, total = 0;
        for (int y = 0; y < h; y += 4)
        for (int x = 0; x < w; x += 4)
        {
            int i = y * stride + x * 4;
            double B = px[i], G = px[i + 1], R = px[i + 2];
            double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
            double weight = 0.05 + (mx - mn) / 255.0 * (mx / 255.0);
            r += R * weight; g += G * weight; bl += B * weight; total += weight;
        }
        if (total <= 0) return Color.FromRgb(235, 235, 240);
        return Color.FromRgb((byte)(r / total), (byte)(g / total), (byte)(bl / total));
    }

    /// <summary>Понятное имя приложения по его AppUserModelId.</summary>
    public static string FriendlyApp(string id)
    {
        var a = id.ToLowerInvariant();
        if (a.Contains("taskbeat") || a.Contains("coolplayer") || a.Contains("webview")) return "Taskbeat";
        if (a.Contains("spotify")) return "Spotify";
        if (a.Contains("yandex") && a.Contains("music")) return "Яндекс Музыка";
        if (a.Contains("vk")) return "VK";
        if (a.Contains("chrome")) return "Chrome";
        if (a.Contains("msedge")) return "Edge";
        if (a.Contains("firefox")) return "Firefox";
        if (a.Contains("opera")) return "Opera";
        if (a.Contains("yandex")) return "Яндекс Браузер";
        if (a.Contains("zunemusic") || a.Contains("media")) return "Медиаплеер";
        var name = System.IO.Path.GetFileNameWithoutExtension(id.Split('!')[0]);
        return name.Length > 0 ? char.ToUpper(name[0]) + name[1..] : id;
    }

    public GSMTC.GlobalSystemMediaTransportControlsSessionManager? Manager => mgr;

    public void Command(string name)
    {
        var s = cur;
        if (s == null) return;
        try
        {
            switch (name)
            {
                case "toggle":
                    _ = s.TryTogglePlayPauseAsync();
                    var st = State.Clone();
                    st.Pos = st.Position; st.Playing = !st.Playing; st.Stamp = Stopwatch.GetTimestamp();
                    State = st; Changed?.Invoke(false); // мгновенный отклик
                    break;
                case "next": _ = s.TrySkipNextAsync(); break;
                case "prev": _ = s.TrySkipPreviousAsync(); break;
            }
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    public void Seek(double seconds)
    {
        var s = cur;
        if (s == null) return;
        try
        {
            _ = s.TryChangePlaybackPositionAsync(TimeSpan.FromSeconds(seconds).Ticks);
            var st = State.Clone();
            st.Pos = seconds; st.Stamp = Stopwatch.GetTimestamp();
            State = st; Changed?.Invoke(false);
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }
}
