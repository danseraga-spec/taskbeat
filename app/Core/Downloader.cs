using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace CoolPlayer;

/// <summary>
/// Скачивание трека в MP3. VK отдаёт HLS (.m3u8): сегменты MPEG-TS, часть зашифрована AES-128.
/// Сегменты расшифровываются, из TS вынимается аудиопоток (это обычные MP3-кадры),
/// сверху добавляется ID3-тег с названием и исполнителем. Без ffmpeg.
/// </summary>
static class Downloader
{
    static readonly HttpClient Http = new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = TimeSpan.FromSeconds(30) };

    public static string Folder
    {
        get
        {
            var d = SettingsStore.Current.DownloadDir;
            if (string.IsNullOrWhiteSpace(d))
                d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Taskbeat");
            return d;
        }
    }

    public static async Task<string> DownloadAsync(Track t, IProgress<double>? progress = null)
    {
        if (!t.Playable) throw new InvalidOperationException("Трек недоступен");
        Directory.CreateDirectory(Folder);
        string name = string.Concat($"{t.Artist} — {t.Title}".Split(Path.GetInvalidFileNameChars())).Trim();
        if (name.Length > 150) name = name[..150];
        byte[] audio = t.Url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? await FromHlsAsync(new Uri(t.Url), progress)
            : await Http.GetByteArrayAsync(t.Url);
        string path = Path.Combine(Folder, name + Extension(audio));

        await using (var f = File.Create(path))
        {
            var tag = Id3(t.Title, t.Artist, t.AlbumTitle);
            await f.WriteAsync(tag);
            await f.WriteAsync(audio);
        }
        return path;
    }

    /// <summary>По первому кадру: ADTS (layer = 0) — это AAC, иначе MPEG-аудио (MP3).</summary>
    static string Extension(byte[] a)
    {
        int i = 0;
        if (a.Length > 10 && a[0] == 'I' && a[1] == 'D' && a[2] == '3')
            i = 10 + ((a[6] & 0x7F) << 21 | (a[7] & 0x7F) << 14 | (a[8] & 0x7F) << 7 | (a[9] & 0x7F));
        for (int n = Math.Min(a.Length - 1, i + 4096); i < n; i++)
            if (a[i] == 0xFF && (a[i + 1] & 0xE0) == 0xE0)
                return (a[i + 1] & 0xF6) == 0xF0 ? ".aac" : ".mp3";
        return ".mp3";
    }

    // ------------------------------------------------------------------ HLS

    static async Task<byte[]> FromHlsAsync(Uri playlist, IProgress<double>? progress)
    {
        var text = await Http.GetStringAsync(playlist);
        // мастер-плейлист → берём первый вариант
        if (text.Contains("#EXT-X-STREAM-INF"))
        {
            var variant = text.Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && !l.StartsWith('#'));
            playlist = new Uri(playlist, variant);
            text = await Http.GetStringAsync(playlist);
        }

        long seq = 0;
        string? method = null;
        byte[]? key = null, iv = null;
        var keyCache = new Dictionary<string, byte[]>();
        var segments = new List<(Uri uri, byte[]? key, byte[]? iv, long seq)>();
        foreach (var raw in text.Split('\n'))
        {
            var l = raw.Trim();
            if (l.StartsWith("#EXT-X-MEDIA-SEQUENCE:")) long.TryParse(l[22..], out seq);
            else if (l.StartsWith("#EXT-X-KEY:"))
            {
                var attrs = ParseAttrs(l[11..]);
                method = attrs.GetValueOrDefault("METHOD");
                key = null; iv = null;
                if (method == "AES-128" && attrs.TryGetValue("URI", out var ku))
                {
                    var kuri = new Uri(playlist, ku).ToString();
                    if (!keyCache.TryGetValue(kuri, out key)) keyCache[kuri] = key = await Http.GetByteArrayAsync(kuri);
                    if (attrs.TryGetValue("IV", out var ivs)) iv = Convert.FromHexString(ivs.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? ivs[2..] : ivs);
                }
            }
            else if (l.Length > 0 && !l.StartsWith('#'))
            {
                segments.Add((new Uri(playlist, l), method == "AES-128" ? key : null, iv, seq));
                seq++;
            }
        }

        var ts = new MemoryStream();
        for (int i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            var data = await Http.GetByteArrayAsync(s.uri);
            if (s.key != null)
            {
                using var aes = Aes.Create();
                aes.Key = s.key;
                var ivb = s.iv ?? SeqIv(s.seq);
                data = aes.DecryptCbc(data, ivb, PaddingMode.PKCS7);
            }
            ts.Write(data);
            progress?.Report((i + 1) / (double)segments.Count);
        }
        return DemuxTs(ts.ToArray());
    }

    static byte[] SeqIv(long seq)
    {
        var iv = new byte[16];
        for (int i = 0; i < 8; i++) iv[15 - i] = (byte)(seq >> (8 * i));
        return iv;
    }

    static Dictionary<string, string> ParseAttrs(string s)
    {
        var d = new Dictionary<string, string>();
        int i = 0;
        while (i < s.Length)
        {
            int eq = s.IndexOf('=', i);
            if (eq < 0) break;
            string k = s[i..eq].Trim(' ', ',');
            string v;
            if (eq + 1 < s.Length && s[eq + 1] == '"')
            {
                int end = s.IndexOf('"', eq + 2);
                v = s[(eq + 2)..end];
                i = end + 1;
            }
            else
            {
                int end = s.IndexOf(',', eq);
                if (end < 0) end = s.Length;
                v = s[(eq + 1)..end];
                i = end;
            }
            d[k] = v;
            i++;
        }
        return d;
    }

    /// <summary>Достаёт из MPEG-TS полезную нагрузку аудио-PES (MP3/AAC-кадры как есть).</summary>
    static byte[] DemuxTs(byte[] ts)
    {
        // если это уже не TS (например, сразу mp3) — отдаём как есть
        if (ts.Length < 188 || ts[0] != 0x47) return ts;
        var outp = new MemoryStream();
        int audioPid = -1;
        bool inPes = false;
        for (int p = 0; p + 188 <= ts.Length; p += 188)
        {
            if (ts[p] != 0x47) { p -= 187; continue; } // ресинхронизация
            bool pusi = (ts[p + 1] & 0x40) != 0;
            int pid = ((ts[p + 1] & 0x1F) << 8) | ts[p + 2];
            int afc = (ts[p + 3] >> 4) & 3;
            int off = p + 4;
            if (afc == 2) continue; // только adaptation field
            if (afc == 3) off += 1 + ts[off];
            if (off >= p + 188) continue;

            if (pusi && ts[off] == 0 && ts[off + 1] == 0 && ts[off + 2] == 1)
            {
                int streamId = ts[off + 3];
                bool audio = streamId is >= 0xC0 and <= 0xDF;
                if (audioPid < 0 && audio) audioPid = pid;
                if (pid != audioPid) continue;
                inPes = true;
                int hdr = ts[off + 8];
                off += 9 + hdr;
                if (off < p + 188) outp.Write(ts, off, p + 188 - off);
            }
            else if (pid == audioPid && inPes)
            {
                outp.Write(ts, off, p + 188 - off);
            }
        }
        return outp.Length > 0 ? outp.ToArray() : ts;
    }

    // ------------------------------------------------------------------ ID3v2.3

    static byte[] Id3(string title, string artist, string? album)
    {
        var frames = new MemoryStream();
        void Frame(string id, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var text = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(value)).ToArray();
            int size = text.Length + 1;
            frames.Write(Encoding.ASCII.GetBytes(id));
            frames.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0, 1]);
            frames.Write(text);
        }
        Frame("TIT2", title);
        Frame("TPE1", artist);
        Frame("TALB", album);
        int n = (int)frames.Length;
        var head = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0,
            (byte)((n >> 21) & 0x7F), (byte)((n >> 14) & 0x7F), (byte)((n >> 7) & 0x7F), (byte)(n & 0x7F) };
        return [.. head, .. frames.ToArray()];
    }
}
