using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>
/// Глобальные горячие клавиши (работают и в играх). Каждую можно выключить или переназначить.
/// Регистрируются на окне острова — у него уже есть цикл сообщений.
/// </summary>
static class Hotkeys
{
    public const uint ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8;
    const uint MOD_NOREPEAT = 0x4000;

    public sealed record Command(int Id, string Key, string What, uint Mods, uint Vk);

    public static readonly Command[] All =
    [
        new(1, "toggle", "Пауза / продолжить", CTRL | ALT, 0x20),
        new(2, "next", "Следующий трек", CTRL | ALT, 0x27),
        new(3, "prev", "Предыдущий трек", CTRL | ALT, 0x25),
        new(4, "volup", "Громче", CTRL | ALT, 0x26),
        new(5, "voldown", "Тише", CTRL | ALT, 0x28),
        new(6, "island", "Показать остров (и поверх игры)", CTRL | ALT, 0x49),
        new(7, "mode", "Игровой / классический режим", CTRL | ALT, 0x47),
        new(8, "lyrics", "Текст песни", CTRL | ALT, 0x4C),
        new(9, "hub", "Открыть Taskbeat", CTRL | ALT, 0x48),
        new(10, "eq", "Эквалайзер вкл/выкл", CTRL | ALT, 0x45),
    ];

    static IntPtr hwnd;
    static readonly HashSet<int> registered = [];

    /// <summary>Действия, сочетание которых занято другой программой.</summary>
    public static HashSet<string> Failed { get; } = [];
    public static event System.Action? Changed;

    public static HotkeyBinding Get(Command a)
    {
        var map = SettingsStore.Current.HotkeyMap;
        return map.TryGetValue(a.Key, out var b) ? b : new HotkeyBinding { Enabled = true, Mods = a.Mods, Vk = a.Vk };
    }

    public static void Set(Command a, HotkeyBinding b)
    {
        SettingsStore.Current.HotkeyMap[a.Key] = b;
        SettingsStore.Save();
        Apply();
    }

    public static void Reset(Command a)
    {
        SettingsStore.Current.HotkeyMap.Remove(a.Key);
        SettingsStore.Save();
        Apply();
    }

    /// <summary>Временно снять все сочетания (пока пользователь вводит новое).</summary>
    public static void Pause()
    {
        if (hwnd == IntPtr.Zero) return;
        foreach (var id in registered) UnregisterHotKey(hwnd, id);
        registered.Clear();
    }

    public static void Register(IntPtr h)
    {
        hwnd = h;
        Apply();
    }

    public static void Apply()
    {
        if (hwnd == IntPtr.Zero) return;
        foreach (var id in registered) UnregisterHotKey(hwnd, id);
        registered.Clear();
        Failed.Clear();
        if (SettingsStore.Current.Hotkeys)
        {
            foreach (var a in All)
            {
                var b = Get(a);
                if (!b.Enabled || b.Vk == 0) continue;
                // громкость можно зажимать — без NOREPEAT
                uint mods = b.Mods | (a.Key is "volup" or "voldown" ? 0 : MOD_NOREPEAT);
                if (RegisterHotKey(hwnd, a.Id, mods, b.Vk)) registered.Add(a.Id);
                else Failed.Add(a.Key);
            }
        }
        Changed?.Invoke();
    }

    public static void Handle(int id)
    {
        var a = All.FirstOrDefault(x => x.Id == id);
        switch (a?.Key)
        {
            case "toggle": AppHost.Transport("toggle"); break;
            case "next": AppHost.Transport("next"); break;
            case "prev": AppHost.Transport("prev"); break;
            case "volup": SystemVolume(0xAF); break;   // VK_VOLUME_UP
            case "voldown": SystemVolume(0xAE); break; // VK_VOLUME_DOWN
            case "island": AppHost.Island.Peek(); break;
            case "mode":
                Perf.Toggle();
                AppHost.Island.Notify(Perf.Game ? "Игровой режим" : "Классический режим",
                    Perf.Game ? "минимум ресурсов" : "красота",
                    Perf.Game ? Color.FromRgb(48, 209, 88) : Color.FromRgb(255, 194, 74));
                break;
            case "lyrics": AppHost.ShowHub(); AppHost.Hub?.ShowLyrics(null); break;
            case "hub": AppHost.ShowHub(); break;
            case "eq":
                SettingsStore.Current.EqEnabled = !SettingsStore.Current.EqEnabled;
                SettingsStore.Save();
                AppHost.Player.EqChanged();
                AppHost.Island.Notify("Эквалайзер", SettingsStore.Current.EqEnabled ? "включён" : "выключен", Color.FromRgb(10, 132, 255));
                break;
        }
    }

    // ------------------------------------------------------------------ текст и предупреждения

    public static string Format(HotkeyBinding b)
    {
        if (b.Vk == 0) return "—";
        var parts = new List<string>();
        if ((b.Mods & WIN) != 0) parts.Add("Win");
        if ((b.Mods & CTRL) != 0) parts.Add("Ctrl");
        if ((b.Mods & ALT) != 0) parts.Add("Alt");
        if ((b.Mods & SHIFT) != 0) parts.Add("Shift");
        parts.Add(KeyName(b.Vk));
        return string.Join(" + ", parts);
    }

    public static string KeyName(uint vk) => vk switch
    {
        0x20 => "Пробел", 0x25 => "←", 0x26 => "↑", 0x27 => "→", 0x28 => "↓",
        0x0D => "Enter", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "Backspace", 0x2E => "Delete", 0x2D => "Insert",
        0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown", 0x2C => "PrintScreen", 0x13 => "Pause",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        0xB3 => "Play/Pause", 0xB0 => "Next track", 0xB1 => "Prev track", 0xAF => "Vol+", 0xAE => "Vol-", 0xAD => "Mute",
        _ => KeyInterop.KeyFromVirtualKey((int)vk).ToString(),
    };

    /// <summary>Предупреждение, если сочетание обычно занято Windows или привычными командами программ.</summary>
    public static string? Warning(HotkeyBinding b, Command self)
    {
        uint m = b.Mods, vk = b.Vk;
        if (vk == 0) return null;
        bool ctrl = (m & CTRL) != 0, alt = (m & ALT) != 0, shift = (m & SHIFT) != 0, win = (m & WIN) != 0;

        var other = All.FirstOrDefault(a => a != self && Get(a) is { Enabled: true } o && o.Mods == m && o.Vk == vk);
        if (other != null) return $"Уже назначено на «{other.What}»";

        if (win) return "Сочетания с Win почти все заняты Windows (Win+D, Win+L, Win+Tab…) — может не сработать";
        if (ctrl && alt && vk == 0x2E) return "Ctrl+Alt+Delete — системное сочетание Windows";
        if (ctrl && shift && vk == 0x1B) return "Ctrl+Shift+Esc открывает диспетчер задач";
        if (ctrl && !alt && !shift && vk == 0x1B) return "Ctrl+Esc открывает меню «Пуск»";
        if (alt && !ctrl && vk is 0x09 or 0x73 or 0x1B or 0x20)
            return vk switch { 0x09 => "Alt+Tab — переключение окон", 0x73 => "Alt+F4 закрывает окна", 0x1B => "Alt+Esc — переключение окон", _ => "Alt+Пробел открывает меню окна" };
        if (ctrl && alt && vk == 0x09) return "Ctrl+Alt+Tab — переключение окон Windows";
        if (vk == 0x2C) return "PrintScreen используется для снимков экрана";
        if (vk is 0xB3 or 0xB0 or 0xB1 or 0xAF or 0xAE or 0xAD) return "Мультимедийные клавиши уже управляют музыкой через Windows — Taskbeat их и так понимает";
        if (m == 0) return "Без Ctrl/Alt/Shift клавиша перестанет работать во всех других программах и играх";
        if (m == SHIFT) return "Shift + клавиша — это обычный ввод текста";
        if (ctrl && !alt && !shift && vk is 0x41 or 0x43 or 0x56 or 0x58 or 0x5A or 0x59 or 0x53 or 0x46 or 0x50 or 0x4E or 0x57 or 0x54 or 0x52 or 0x4F)
            return "Это привычная команда программ (копировать, вставить, сохранить…) — она перестанет работать";
        if (alt && !ctrl && !shift && vk is >= 0x41 and <= 0x5A) return "Alt + буква открывает меню во многих программах";
        if (ctrl && shift && !alt && vk == 0x10) return "Ctrl+Shift переключает раскладку";
        if (ctrl && alt && !shift && vk is >= 0x25 and <= 0x28)
            return "На некоторых видеокартах Intel Ctrl+Alt+стрелки поворачивают экран (обычно выключено)";
        if (ctrl && shift && !alt && vk == 0x1B) return "Ctrl+Shift+Esc — диспетчер задач";
        if (vk is >= 0x70 and <= 0x7B && m == ALT && vk == 0x73) return "Alt+F4 закрывает окна";
        return null;
    }

    static void SystemVolume(byte vk)
    {
        keybd_event(vk, 0, 0, 0);
        keybd_event(vk, 0, 2, 0); // KEYEVENTF_KEYUP
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
}
