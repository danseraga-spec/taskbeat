using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace CoolPlayer;

/// <summary>
/// Last.fm: «сейчас играет» и скробблинг (трек дослушан до половины или 4 минут).
/// Ключ приложения выдаёт last.fm/api/account/create — впишите его в ApiKey/Secret перед сборкой.
/// </summary>
static class LastFm
{
    // TODO: ключ и секрет приложения Last.fm (https://www.last.fm/api/account/create)
    const string ApiKey = "";
    const string Secret = "";
    const string Api = "https://ws.audioscrobbler.com/2.0/";

    public static bool Configured => ApiKey.Length > 0 && Secret.Length > 0;
    public static bool Connected => SettingsStore.Current.LastFmSession.Length > 0;
    public static string User => SettingsStore.Current.LastFmUser;
    public static event Action? Changed;

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    static string? session;
    static string? pendingToken;
    static Track? playing;
    static DateTime startedUtc;
    static double listened;
    static DateTime lastTick;
    static bool scrobbled;

    public static void Start()
    {
        if (!Configured) return;
        AppHost.Player.Changed += OnPlayer;
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        t.Tick += (_, _) => Tick();
        t.Start();
    }

    // ------------------------------------------------------------------ вход

    /// <summary>Шаг 1: получить токен и открыть страницу разрешения в браузере.</summary>
    public static async Task<string?> BeginConnectAsync()
    {
        try
        {
            var r = await Call("auth.getToken", [], signed: true, post: false);
            pendingToken = r.GetProperty("token").GetString();
            AppHost.OpenUri($"https://www.last.fm/api/auth/?api_key={ApiKey}&token={pendingToken}");
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Шаг 2: после «Разрешить» на сайте — получить сессию.</summary>
    public static async Task<string?> FinishConnectAsync()
    {
        if (pendingToken == null) return L.T("Сначала нажмите «Подключить»");
        try
        {
            var r = await Call("auth.getSession", new() { ["token"] = pendingToken }, signed: true, post: false);
            var s = r.GetProperty("session");
            string key = s.GetProperty("key").GetString() ?? "";
            SettingsStore.Current.LastFmUser = s.GetProperty("name").GetString() ?? "";
            SettingsStore.Current.LastFmSession = await ProtectAsync(key);
            SettingsStore.Save();
            session = key;
            pendingToken = null;
            Changed?.Invoke();
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static void Disconnect()
    {
        SettingsStore.Current.LastFmSession = "";
        SettingsStore.Current.LastFmUser = "";
        SettingsStore.Save();
        session = null;
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------ скробблинг

    static void OnPlayer()
    {
        var t = AppHost.Player.Current;
        if (t == playing) return;
        playing = t;
        startedUtc = DateTime.UtcNow;
        listened = 0;
        lastTick = DateTime.UtcNow;
        scrobbled = false;
        if (t != null && Connected) _ = SafeAsync(() => NowPlayingAsync(t));
    }

    static void Tick()
    {
        var now = DateTime.UtcNow;
        if (AppHost.Player.IsPlaying) listened += (now - lastTick).TotalSeconds;
        lastTick = now;
        if (scrobbled || playing is not { } t || !Connected) return;
        double dur = t.Duration > 0 ? t.Duration : AppHost.Player.Duration;
        if (dur < 30) return; // Last.fm не принимает треки короче 30 секунд
        if (listened >= Math.Min(dur / 2, 240))
        {
            scrobbled = true;
            var at = startedUtc;
            _ = SafeAsync(() => ScrobbleAsync(t, at));
        }
    }

    static Task NowPlayingAsync(Track t) => Call("track.updateNowPlaying", TrackArgs(t), signed: true, post: true);

    static Task ScrobbleAsync(Track t, DateTime startedUtc)
    {
        var a = TrackArgs(t);
        a["timestamp"] = new DateTimeOffset(startedUtc).ToUnixTimeSeconds().ToString();
        return Call("track.scrobble", a, signed: true, post: true);
    }

    static Dictionary<string, string> TrackArgs(Track t)
    {
        var a = new Dictionary<string, string> { ["artist"] = t.Artist, ["track"] = t.Title };
        if (t.AlbumTitle is { Length: > 0 } al) a["album"] = al;
        if (t.Duration > 0) a["duration"] = ((int)t.Duration).ToString();
        return a;
    }

    static async Task SafeAsync(Func<Task> f)
    {
        try { await f(); }
        catch (Exception ex) { Paths.Trace("last.fm: " + ex.Message); }
    }

    // ------------------------------------------------------------------ API

    static async Task<JsonElement> Call(string method, Dictionary<string, string> args, bool signed, bool post)
    {
        args["method"] = method;
        args["api_key"] = ApiKey;
        if (method is "track.updateNowPlaying" or "track.scrobble")
            args["sk"] = session ??= await UnprotectAsync(SettingsStore.Current.LastFmSession);
        if (signed)
        {
            var sb = new StringBuilder();
            foreach (var kv in args.OrderBy(k => k.Key, StringComparer.Ordinal)) sb.Append(kv.Key).Append(kv.Value);
            sb.Append(Secret);
            args["api_sig"] = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
        }
        args["format"] = "json";
        HttpResponseMessage resp = post
            ? await Http.PostAsync(Api, new FormUrlEncodedContent(args))
            : await Http.GetAsync(Api + "?" + string.Join("&", args.Select(k => k.Key + "=" + Uri.EscapeDataString(k.Value))));
        using (resp)
        {
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                int code = e.GetInt32();
                if (code == 9) Disconnect(); // сессия отозвана
                throw new Exception(doc.RootElement.TryGetProperty("message", out var m) ? $"Last.fm: {m.GetString()}" : $"Last.fm: ошибка {code}");
            }
            return doc.RootElement.Clone();
        }
    }

    static async Task<string> ProtectAsync(string s)
    {
        var buf = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.ConvertStringToBinary(s, BinaryStringEncoding.Utf8));
        CryptographicBuffer.CopyToByteArray(buf, out var bytes);
        return Convert.ToBase64String(bytes);
    }

    static async Task<string> UnprotectAsync(string b64)
    {
        if (b64.Length == 0) return "";
        var plain = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(Convert.FromBase64String(b64)));
        return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain);
    }
}

/// <summary>
/// Статус в Discord («Слушает …») через локальный канал discord-ipc. Нужен Application ID
/// с discord.com/developers — впишите его в AppId перед сборкой.
/// </summary>
static class DiscordPresence
{
    // TODO: Application ID приложения Discord (https://discord.com/developers/applications)
    const string AppId = "";

    public static bool Configured => AppId.Length > 0;
    static NamedPipeClientStream? pipe;
    static Track? shown;
    static bool shownPlaying;
    static DateTime lastAttempt;
    static readonly SemaphoreSlim gate = new(1, 1);

    public static void Start()
    {
        if (!Configured) return;
        AppHost.Player.Changed += () => _ = UpdateAsync();
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        t.Tick += (_, _) => _ = UpdateAsync();
        t.Start();
    }

    public static async Task UpdateAsync(bool force = false)
    {
        if (!Configured) return;
        var p = AppHost.Player;
        var t = SettingsStore.Current.DiscordPresence ? p.Current : null;
        bool playing = t != null && p.IsPlaying;
        double pos = p.Position, dur = t?.Duration > 0 ? t.Duration : p.Duration;
        if (!force && t == shown && playing == shownPlaying) return;
        if (!await gate.WaitAsync(0)) return;
        try
        {
            if (!await EnsureAsync()) return;
            object? activity = t == null || !playing ? null : new
            {
                type = 2, // «Слушает»
                details = Cut(t.Title),
                state = Cut(t.Artist.Length > 0 ? t.Artist : "Taskbeat"),
                timestamps = dur > 0 ? new
                {
                    start = DateTimeOffset.UtcNow.AddSeconds(-pos).ToUnixTimeMilliseconds(),
                    end = DateTimeOffset.UtcNow.AddSeconds(dur - pos).ToUnixTimeMilliseconds(),
                } : null,
                assets = new
                {
                    large_image = t.CoverBig is { } c && c.StartsWith("http") ? c : "taskbeat",
                    large_text = Cut(t.AlbumTitle ?? "Taskbeat"),
                    small_image = "taskbeat",
                    small_text = "Taskbeat",
                },
            };
            await SendAsync(1, new { cmd = "SET_ACTIVITY", nonce = Guid.NewGuid().ToString(), args = new { pid = Environment.ProcessId, activity } });
            shown = t;
            shownPlaying = playing;
        }
        catch (Exception ex)
        {
            Paths.Trace("discord: " + ex.Message);
            pipe?.Dispose();
            pipe = null;
        }
        finally { gate.Release(); }
    }

    static string Cut(string s) => s.Length < 2 ? s + "  " : s.Length > 120 ? s[..120] : s;

    static async Task<bool> EnsureAsync()
    {
        if (pipe is { IsConnected: true }) return true;
        if ((DateTime.Now - lastAttempt).TotalSeconds < 30) return false; // Discord не запущен — не долбимся
        lastAttempt = DateTime.Now;
        for (int i = 0; i < 10; i++)
        {
            var c = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await c.ConnectAsync(200); }
            catch { c.Dispose(); continue; }
            pipe = c;
            await SendAsync(0, new { v = 1, client_id = AppId });
            await ReadAsync(); // READY
            return true;
        }
        return false;
    }

    static async Task SendAsync(int op, object payload)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        var buf = new byte[8 + json.Length];
        BitConverter.GetBytes(op).CopyTo(buf, 0);
        BitConverter.GetBytes(json.Length).CopyTo(buf, 4);
        json.CopyTo(buf, 8);
        await pipe!.WriteAsync(buf);
        await pipe.FlushAsync();
        if (op == 1) await ReadAsync();
    }

    static async Task ReadAsync()
    {
        var head = new byte[8];
        using var cts = new CancellationTokenSource(3000);
        await pipe!.ReadExactlyAsync(head, cts.Token);
        int len = BitConverter.ToInt32(head, 4);
        if (len > 0 && len < 1 << 20) await pipe.ReadExactlyAsync(new byte[len], cts.Token);
    }

    public static void Stop()
    {
        try { pipe?.Dispose(); } catch { }
        pipe = null;
    }
}
