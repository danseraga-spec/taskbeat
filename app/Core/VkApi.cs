using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using global::Windows.Security.Cryptography;
using global::Windows.Security.Cryptography.DataProtection;

namespace CoolPlayer;

public sealed class VkPlaylist
{
    public long Id { get; init; }
    public long OwnerId { get; init; }
    public string Title { get; init; } = "";
    public int Count { get; init; }
    public string? AccessKey { get; init; }
    public string? Cover { get; init; }
    public string CountText => $"{Count} {Plural(Count, "трек", "трека", "треков")}";

    static string Plural(int n, string one, string few, string many) =>
        (n % 10 == 1 && n % 100 != 11) ? one : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? few : many;
}

sealed class VkException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Аудио VK. Публичного API для музыки у VK нет, поэтому используется токен мобильного клиента
/// (Kate Mobile или официальное приложение VK для Android), полученный при входе пользователя.
/// Пароль пользователь вводит сам на oauth.vk.com; приложение его не видит.
/// </summary>
sealed class VkApi
{
    public const int ClientId = 2685278; // Kate Mobile
    const string UserAgent = "KateMobileAndroid/109.1 lite-550 (Android 13; SDK 33; arm64-v8a; Google Pixel 7; ru)";
    const string Version = "5.131";
    public const string RedirectUri = "https://oauth.vk.com/blank.html";

    public static string AuthUrl =>
        $"https://oauth.vk.com/authorize?client_id={ClientId}&display=page&redirect_uri={RedirectUri}" +
        $"&scope=audio,offline&response_type=token&v={Version}";

    // Публичный секрет клиента Kate Mobile (общеизвестен, используется всеми сторонними клиентами).
    const string ClientSecret = "lxhD8OD7dMsqtXIm5IUY";

    public sealed record DirectAuthResult(
        string? Token, long UserId, string? Error, string? Description,
        string? ValidationType, string? PhoneMask, string? ValidationSid, string? ValidationUrl,
        string? CaptchaSid, string? CaptchaImg);

    /// <summary>
    /// Прямой вход: логин и пароль вводит пользователь в окне Taskbeat,
    /// они уходят только на oauth.vk.com и нигде не сохраняются. Только такой токен VK пускает к аудио.
    /// </summary>
    public static async Task<DirectAuthResult> DirectAuthAsync(string login, string password,
        string? code = null, string? captchaSid = null, string? captchaKey = null, bool forceSms = false)
    {
        var args = new Dictionary<string, string>
        {
            ["grant_type"] = "password", ["client_id"] = ClientId.ToString(), ["client_secret"] = ClientSecret,
            ["username"] = login, ["password"] = password, ["scope"] = "audio,offline", ["v"] = Version,
            ["2fa_supported"] = "1", ["lang"] = "ru",
        };
        if (!string.IsNullOrWhiteSpace(code)) args["code"] = code.Trim();
        if (forceSms) args["force_sms"] = "1";
        if (captchaSid != null && captchaKey != null) { args["captcha_sid"] = captchaSid; args["captcha_key"] = captchaKey.Trim(); }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth.vk.com/token") { Content = new FormUrlEncodedContent(args) };
        req.Headers.UserAgent.ParseAdd(UserAgent);
        using var resp = await Http.SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var r = doc.RootElement;
        string? S(string k) => r.TryGetProperty(k, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
        long uid = r.TryGetProperty("user_id", out var u) && u.TryGetInt64(out var x) ? x : 0;
        return new DirectAuthResult(S("access_token"), uid, S("error"), S("error_description"),
            S("validation_type"), S("phone_mask"), S("validation_sid"), S("redirect_uri"), S("captcha_sid"), S("captcha_img"));
    }

    // ------------------------------------------------------------------ вход по QR-коду
    // Поток «войти на другом устройстве»: анонимный токен → auth.getAuthCode (ссылка для QR) →
    // опрос auth.checkAuthCode, пока пользователь не подтвердит вход в приложении VK на телефоне.

    /// <summary>Клиент VK, от имени которого выдан токен (у каждого свой User-Agent).</summary>
    public sealed record Client(int Id, string Secret, string Ua);
    public static readonly Client Kate = new(ClientId, "lxhD8OD7dMsqtXIm5IUY", UserAgent);
    public static readonly Client Android = new(2274003, "hHbZxrka2uZ6jB1inYsH",
        "VKAndroidApp/8.52-14102 (Android 13; SDK 33; arm64-v8a; Google Pixel 7; ru; 2400x1080)");
    static Client ById(int id) => id == Android.Id ? Android : Kate;

    public sealed record QrCode(string Url, string Hash, string AnonToken, int ExpiresIn, Client Client);
    public sealed record QrStatus(int Status, string? Token, long UserId, string? Error, string Keys);

    const string QrVersion = "5.199";

    /// <summary>QR-вход разрешён не всем клиентам — пробуем официальный Android, затем Kate.</summary>
    public static async Task<QrCode> QrStartAsync()
    {
        Exception? last = null;
        foreach (var c in new[] { Android, Kate })
        {
            try { return await QrStartAsync(c); }
            catch (Exception ex) { last = ex; Paths.Trace($"vk-qr: клиент {c.Id} не дал код: {ex.Message}"); }
        }
        throw last!;
    }

    static async Task<QrCode> QrStartAsync(Client c)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://oauth.vk.com/get_anonym_token?client_id={c.Id}&client_secret={c.Secret}&v={QrVersion}");
        req.Headers.UserAgent.ParseAdd(c.Ua);
        using var resp = await Http.SendAsync(req);
        using var a = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!a.RootElement.TryGetProperty("token", out var tk) || tk.GetString() is not { Length: > 0 } anon)
            throw new VkException(0, "VK не выдал временный ключ для QR-входа");

        var r = await RawCall("auth.getAuthCode", new()
        {
            ["anonymous_token"] = anon, ["device_name"] = "Taskbeat для Windows", ["client_id"] = c.Id.ToString(),
            ["force_regenerate"] = "1", ["auth_code_flow"] = "0",
        }, c.Ua);
        string url = r.TryGetProperty("auth_url", out var u) ? u.GetString() ?? "" : "";
        string hash = r.TryGetProperty("auth_hash", out var h) ? h.GetString() ?? "" : "";
        int exp = r.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var x) ? x : 300;
        if (url.Length == 0 || hash.Length == 0) throw new VkException(0, "VK не выдал QR-код");
        return new QrCode(url, hash, anon, exp, c);
    }

    /// <summary>Статус: 0 — ждём, 1 — отсканирован, 2 — подтверждён, 3 — отклонён, 4 — истёк.</summary>
    public static async Task<QrStatus> QrCheckAsync(QrCode qr)
    {
        var r = await RawCall("auth.checkAuthCode", new()
        {
            ["anonymous_token"] = qr.AnonToken, ["auth_hash"] = qr.Hash, ["client_id"] = qr.Client.Id.ToString(), ["web_auth"] = "0",
        }, qr.Client.Ua);
        int status = r.TryGetProperty("status", out var s) && s.TryGetInt32(out var si) ? si : 0;
        string? token = r.TryGetProperty("access_token", out var t) ? t.GetString() : null;
        long uid = r.TryGetProperty("user_id", out var u) && u.TryGetInt64(out var ui) ? ui : 0;
        string keys = r.ValueKind == JsonValueKind.Object ? string.Join(",", r.EnumerateObject().Select(p => p.Name)) : "";
        return new QrStatus(status, token, uid, null, keys);
    }

    /// <summary>Код, который приложение VK показало на телефоне после сканирования (статус 5).</summary>
    public static Task<JsonElement> QrValidateAsync(QrCode qr, string code) =>
        RawCall("auth.validateAuthCode", new()
        {
            ["auth_hash"] = qr.Hash, ["validation_code"] = code.Trim(), ["access_token"] = qr.AnonToken,
            ["anonymous_token"] = qr.AnonToken, ["client_id"] = qr.Client.Id.ToString(),
        }, qr.Client.Ua);

    /// <summary>Вызов метода без пользовательского токена (для QR-входа).</summary>
    static async Task<JsonElement> RawCall(string method, Dictionary<string, string> args, string ua)
    {
        args["v"] = QrVersion;
        args["lang"] = "ru";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.vk.com/method/" + method) { Content = new FormUrlEncodedContent(args) };
        req.Headers.UserAgent.ParseAdd(ua);
        using var resp = await Http.SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            int code = err.TryGetProperty("error_code", out var c) ? c.GetInt32() : 0;
            string msg = err.TryGetProperty("error_msg", out var m) ? m.GetString() ?? "" : "";
            throw new VkException(code, $"VK: {msg} ({code})");
        }
        return doc.RootElement.GetProperty("response").Clone();
    }

    static readonly HttpClient Http = CreateHttp();
    static readonly string TokenFile = Path.Combine(AppHost.DataDir, "vk.bin");

    string? token;
    public long UserId { get; private set; }
    public bool LoggedIn => token != null;
    public event Action? AuthChanged;

    static HttpClient CreateHttp()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        }) { Timeout = TimeSpan.FromSeconds(20) };
        return h; // User-Agent ставится на каждый запрос — по клиенту токена
    }

    // ------------------------------------------------------------------ токен

    public async Task LoadTokenAsync()
    {
        // Сразу после входа в Windows защита данных и файл могут быть ещё недоступны —
        // повторяем несколько раз, а не считаем пользователя вышедшим.
        int[] delays = [0, 1000, 2000, 4000, 8000, 15000];
        for (int i = 0; i < delays.Length; i++)
        {
            if (delays[i] > 0) await Task.Delay(delays[i]);
            if (token != null) return; // успели войти заново
            try
            {
                if (!File.Exists(TokenFile))
                {
                    Paths.Trace($"vk: ключа нет ({TokenFile})");
                    return;
                }
                var prov = new DataProtectionProvider();
                var data = await File.ReadAllBytesAsync(TokenFile);
                var plain = await prov.UnprotectAsync(CryptographicBuffer.CreateFromByteArray(data));
                var parts = CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, plain).Split('|');
                if (parts.Length >= 2 && parts[0].Length > 0 && long.TryParse(parts[1], out var uid))
                {
                    token = parts[0];
                    UserId = uid;
                    client = parts.Length > 2 && int.TryParse(parts[2], out var cid) ? ById(cid) : Kate;
                    Paths.Trace($"vk: ключ загружен (попытка {i + 1})");
                    AuthChanged?.Invoke();
                    return;
                }
                Paths.Trace($"vk: ключ не распознан ({parts.Length} частей)");
            }
            catch (Exception ex) { Paths.Trace($"vk: ключ не прочитан (попытка {i + 1}): {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    Client client = Kate;

    public async Task SetTokenAsync(string accessToken, long userId, Client? by = null)
    {
        token = accessToken;
        UserId = userId;
        client = by ?? Kate;
        try
        {
            var prov = new DataProtectionProvider("LOCAL=user");
            var buf = await prov.ProtectAsync(CryptographicBuffer.ConvertStringToBinary($"{accessToken}|{userId}|{client.Id}",BinaryStringEncoding.Utf8));
            CryptographicBuffer.CopyToByteArray(buf, out byte[] bytes);
            Paths.WriteAtomic(TokenFile, bytes);
            Paths.Trace("vk: ключ сохранён");
        }
        catch (Exception ex) { Paths.Trace($"vk: ключ не сохранён: {ex.GetType().Name}: {ex.Message}"); }
        AuthChanged?.Invoke();
    }

    public void Logout()
    {
        token = null;
        UserId = 0;
        Paths.Trace("vk: выход");
        try { File.Delete(TokenFile); File.Delete(TokenFile + ".bak"); } catch { }
        AuthChanged?.Invoke();
    }

    // ------------------------------------------------------------------ API

    async Task<JsonElement> Call(string method, Dictionary<string, string> args)
    {
        if (token == null) throw new VkException(5, "Нужно войти в VK");
        args["access_token"] = token;
        args["v"] = Version;
        args["lang"] = "ru";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.vk.com/method/" + method) { Content = new FormUrlEncodedContent(args) };
        req.Headers.UserAgent.ParseAdd(client.Ua);
        using var resp = await Http.SendAsync(req);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("error", out var err))
        {
            int code = err.GetProperty("error_code").GetInt32();
            string msg = err.TryGetProperty("error_msg", out var m) ? m.GetString() ?? "" : "";
            // 3 на audio.* — токен получен не прямым входом и к музыке не допущен
            bool noAudio = code == 3 && method is "audio.get" or "audio.search";
            if (code == 5 || noAudio) Logout();
            if (noAudio) throw new VkException(5, "Этот вход не даёт доступа к музыке — войдите заново по логину и паролю");
            throw new VkException(code, code switch
            {
                5 => "Сессия VK истекла — войдите заново",
                6 => "Слишком много запросов, попробуйте чуть позже",
                25 => "VK требует подтвердить токен — войдите заново",
                201 or 203 => "Нет доступа к аудио",
                _ => $"VK: {msg} ({code})",
            });
        }
        return doc.RootElement.GetProperty("response").Clone();
    }

    static Track ParseTrack(JsonElement a)
    {
        string? Thumb(params string[] keys)
        {
            if (!a.TryGetProperty("album", out var al) || !al.TryGetProperty("thumb", out var th)) return null;
            foreach (var k in keys)
                if (th.TryGetProperty(k, out var v) && v.GetString() is { Length: > 0 } s) return s;
            return null;
        }
        string url = a.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
        if (url.Contains("audio_api_unavailable")) url = "";
        long owner = a.GetProperty("owner_id").GetInt64(), id = a.GetProperty("id").GetInt64();
        long albumId = 0, albumOwner = 0;
        string? albumKey = null, albumTitle = null;
        if (a.TryGetProperty("album", out var al) && al.ValueKind == JsonValueKind.Object)
        {
            if (al.TryGetProperty("id", out var ai)) ai.TryGetInt64(out albumId);
            if (al.TryGetProperty("owner_id", out var ao)) ao.TryGetInt64(out albumOwner);
            albumKey = al.TryGetProperty("access_key", out var ak) ? ak.GetString() : null;
            albumTitle = al.TryGetProperty("title", out var at) ? at.GetString() : null;
        }
        bool lyrics = (a.TryGetProperty("has_lyrics", out var hl) && hl.ValueKind == JsonValueKind.True)
                      || a.TryGetProperty("lyrics_id", out _);
        return new Track
        {
            OwnerId = owner, AudioId = id,
            AccessKey = a.TryGetProperty("access_key", out var k) ? k.GetString() : null,
            AlbumId = albumId, AlbumOwnerId = albumOwner, AlbumAccessKey = albumKey, AlbumTitle = albumTitle,
            HasLyrics = lyrics,
            Id = $"{owner}_{id}",
            Title = a.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
            Artist = a.TryGetProperty("artist", out var ar) ? ar.GetString() ?? "" : "",
            Url = url,
            Duration = a.TryGetProperty("duration", out var d) ? d.GetDouble() : 0,
            Cover = Thumb("photo_68", "photo_135", "photo_270"),
            CoverBig = Thumb("photo_600", "photo_300", "photo_270"),
        };
    }

    static List<Track> Items(JsonElement r)
    {
        var src = r.ValueKind == JsonValueKind.Array ? r : r.GetProperty("items");
        return [.. src.EnumerateArray().Select(ParseTrack)];
    }

    public async Task<List<Track>> MyAudioAsync(int offset, int count) =>
        Items(await Call("audio.get", new() { ["owner_id"] = UserId.ToString(), ["offset"] = offset.ToString(), ["count"] = count.ToString() }));

    public async Task<List<Track>> SearchAsync(string q) =>
        Items(await Call("audio.search", new() { ["q"] = q, ["count"] = "200", ["auto_complete"] = "1", ["sort"] = "2" }));

    public async Task<List<Track>> RecommendationsAsync() =>
        Items(await Call("audio.getRecommendations", new() { ["count"] = "200", ["shuffle"] = "0" }));

    public async Task<List<VkPlaylist>> PlaylistsAsync()
    {
        var r = await Call("audio.getPlaylists", new() { ["owner_id"] = UserId.ToString(), ["count"] = "200" });
        return [.. r.GetProperty("items").EnumerateArray().Select(p =>
        {
            string? cover = null;
            if (p.TryGetProperty("photo", out var ph) && ph.TryGetProperty("photo_300", out var c)) cover = c.GetString();
            else if (p.TryGetProperty("thumbs", out var ths) && ths.GetArrayLength() > 0 && ths[0].TryGetProperty("photo_300", out var c2)) cover = c2.GetString();
            return new VkPlaylist
            {
                Id = p.GetProperty("id").GetInt64(),
                OwnerId = p.GetProperty("owner_id").GetInt64(),
                Title = p.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Count = p.TryGetProperty("count", out var n) ? n.GetInt32() : 0,
                AccessKey = p.TryGetProperty("access_key", out var k) ? k.GetString() : null,
                Cover = cover,
            };
        })];
    }

    public async Task<List<Track>> PlaylistTracksAsync(VkPlaylist p)
    {
        var args = new Dictionary<string, string>
        {
            ["owner_id"] = p.OwnerId.ToString(), ["playlist_id"] = p.Id.ToString(), ["count"] = "1000",
        };
        if (p.AccessKey != null) args["access_key"] = p.AccessKey;
        return Items(await Call("audio.get", args));
    }

    // ------------------------------------------------------------------ действия с треком

    /// <summary>«Микс по треку» — рекомендации, похожие на этот трек.</summary>
    public async Task<List<Track>> MixAsync(Track t) =>
        Items(await Call("audio.getRecommendations", new() { ["target_audio"] = t.Id, ["count"] = "100", ["shuffle"] = "0" }));

    public bool IsMine(Track t) => t.OwnerId == UserId;

    public async Task AddAsync(Track t)
    {
        var a = new Dictionary<string, string> { ["audio_id"] = t.AudioId.ToString(), ["owner_id"] = t.OwnerId.ToString() };
        if (t.AccessKey != null) a["access_key"] = t.AccessKey;
        await Call("audio.add", a);
    }

    public async Task DeleteAsync(Track t) =>
        await Call("audio.delete", new() { ["audio_id"] = t.AudioId.ToString(), ["owner_id"] = t.OwnerId.ToString() });

    public async Task AddToPlaylistAsync(VkPlaylist p, Track t) =>
        await Call("audio.addToPlaylist", new()
        {
            ["owner_id"] = p.OwnerId.ToString(), ["playlist_id"] = p.Id.ToString(),
            ["audio_ids"] = t.AccessKey != null ? $"{t.Id}_{t.AccessKey}" : t.Id,
        });

    public static VkPlaylist? AlbumOf(Track t) => t.AlbumId == 0 ? null : new VkPlaylist
    {
        Id = t.AlbumId, OwnerId = t.AlbumOwnerId, AccessKey = t.AlbumAccessKey, Title = t.AlbumTitle ?? "Альбом",
    };

    /// <summary>Текст песни из VK: синхронизированный (timestamps) или обычный.</summary>
    public async Task<Lyrics?> LyricsAsync(Track t)
    {
        var r = await Call("audio.getLyrics", new() { ["audio_id"] = t.Id });
        var lines = new List<LyricLine>();
        var root = r.TryGetProperty("lyrics", out var ly) ? ly : r;
        if (root.TryGetProperty("timestamps", out var ts) && ts.ValueKind == JsonValueKind.Array && ts.GetArrayLength() > 0)
        {
            foreach (var x in ts.EnumerateArray())
            {
                double b = x.TryGetProperty("begin", out var bb) && bb.TryGetDouble(out var bd) ? bd : 0;
                string line = x.TryGetProperty("line", out var l) ? l.GetString() ?? "" : "";
                lines.Add(new LyricLine(b, line));
            }
            // VK отдаёт время в миллисекундах
            if (lines.Count > 0 && lines.Max(l => l.Time) > Math.Max(1200, t.Duration * 1.5))
                lines = [.. lines.Select(l => l with { Time = l.Time / 1000 })];
            return new Lyrics(lines, true, "VK");
        }
        if (root.TryGetProperty("text", out var tx))
        {
            IEnumerable<string> plain = tx.ValueKind == JsonValueKind.Array
                ? tx.EnumerateArray().Select(e => e.GetString() ?? "")
                : (tx.GetString() ?? "").Split('\n');
            lines.AddRange(plain.Select(s => new LyricLine(-1, s.TrimEnd('\r'))));
            if (lines.Count > 0) return new Lyrics(lines, false, "VK");
        }
        return null;
    }

    public async Task<string> UserNameAsync()
    {
        var r = await Call("users.get", new() { ["user_ids"] = UserId.ToString() });
        var u = r[0];
        return $"{u.GetProperty("first_name").GetString()} {u.GetProperty("last_name").GetString()}";
    }
}
