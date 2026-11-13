using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace CoolPlayer;

/// <summary>
/// Пока работает Taskbeat, стандартные всплывающие окна уведомлений Windows скрыты:
/// уведомления показывает остров, а в центре уведомлений они остаются.
/// Делается это настройкой «Показывать баннеры» (ShowBanner) у каждого приложения.
/// Исходные значения сохраняются в banners.json и возвращаются при выходе;
/// если Taskbeat закрылся аварийно, они вернутся при следующем выходе или отключении настройки.
/// </summary>
static class BannerGuard
{
    const string Root = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    static readonly string BackupFile = Path.Combine(Paths.DataDir, "banners.json");
    // исходное значение ShowBanner по пути ключа (null — значения не было)
    static Dictionary<string, int?> saved = [];
    static bool active;

    static bool Wanted => SettingsStore.Current.WinNotifications && SettingsStore.Current.HideWinBanners;

    /// <summary>Включить/выключить по текущим настройкам.</summary>
    public static void Apply()
    {
        if (Wanted && !active) Suppress();
        else if (!Wanted && (active || File.Exists(BackupFile))) Restore();
    }

    static void Suppress()
    {
        try
        {
            Load();
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root != null)
                foreach (var path in Leaves(root, ""))
                    Hide(path);
            active = true;
            Save();
            Paths.Trace($"баннеры Windows скрыты ({saved.Count} приложений)");
        }
        catch (Exception ex) { Paths.Trace("баннеры: " + ex.Message); }
    }

    /// <summary>Новое приложение прислало уведомление — его баннеры тоже прячем.</summary>
    public static void Seen(string aumid)
    {
        if (!active || string.IsNullOrEmpty(aumid) || saved.ContainsKey(aumid)) return;
        try { Hide(aumid); Save(); } catch { }
    }

    public static void Restore()
    {
        try
        {
            Load();
            foreach (var (path, value) in saved)
            {
                try
                {
                    using var k = Registry.CurrentUser.OpenSubKey($@"{Root}\{path}", true);
                    if (k == null) continue;
                    if (value is int v) k.SetValue("ShowBanner", v, RegistryValueKind.DWord);
                    else k.DeleteValue("ShowBanner", false);
                }
                catch { }
            }
            Paths.Trace($"баннеры Windows возвращены ({saved.Count} приложений)");
            saved.Clear();
            File.Delete(BackupFile);
        }
        catch (Exception ex) { Paths.Trace("баннеры (возврат): " + ex.Message); }
        active = false;
    }

    static void Hide(string path)
    {
        // будильники и напоминания часов не трогаем
        if (path.StartsWith("Windows.ActionCenter", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("Microsoft.WindowsAlarms", StringComparison.OrdinalIgnoreCase)) return;
        using var k = Registry.CurrentUser.CreateSubKey($@"{Root}\{path}", true);
        if (k == null) return;
        if (!saved.ContainsKey(path)) saved[path] = k.GetValue("ShowBanner") as int?;
        k.SetValue("ShowBanner", 0, RegistryValueKind.DWord);
    }

    // ключи приложений — «листья» (у AUMID вида {GUID}\папка\файл.exe ключ вложенный)
    static IEnumerable<string> Leaves(RegistryKey key, string prefix)
    {
        var subs = key.GetSubKeyNames();
        if (subs.Length == 0 && prefix.Length > 0) { yield return prefix; yield break; }
        foreach (var name in subs)
        {
            using var sub = key.OpenSubKey(name);
            if (sub == null) continue;
            foreach (var p in Leaves(sub, prefix.Length == 0 ? name : $@"{prefix}\{name}")) yield return p;
        }
    }

    static void Load()
    {
        // после аварийного выхода исходные значения берём из файла, а не текущие нули
        if (saved.Count > 0 || !File.Exists(BackupFile)) return;
        try { saved = JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(BackupFile)) ?? []; }
        catch { saved = []; }
    }

    static void Save()
    {
        try { Paths.WriteAtomic(BackupFile, JsonSerializer.SerializeToUtf8Bytes(saved)); } catch { }
    }
}
