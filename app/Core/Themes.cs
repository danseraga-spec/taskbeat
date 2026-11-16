using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace CoolPlayer;

/// <summary>Тёмная/светлая тема окна Taskbeat и цвет акцента. Применяется при запуске (кисти подменяются до создания окон).</summary>
static class Themes
{
    public static readonly (string key, string name, string hex)[] Accents =
    [
        ("gold", L.T("Золотой"), "#FFC24A"), ("orange", L.T("Оранжевый"), "#FF8A4C"), ("red", L.T("Красный"), "#FF5A5F"),
        ("pink", L.T("Розовый"), "#FF6FB5"), ("purple", L.T("Фиолетовый"), "#B57BFF"), ("blue", L.T("Синий"), "#4C9DFF"),
        ("teal", L.T("Бирюзовый"), "#3DD6C6"), ("green", L.T("Зелёный"), "#4CD964"),
    ];

    public static bool IsLight { get; private set; }
    public static Color Accent { get; private set; } = Color.FromRgb(255, 194, 74);

    public static void Apply(ResourceDictionary d)
    {
        var s = SettingsStore.Current;
        IsLight = s.Theme == "light" || (s.Theme == "system" && SystemIsLight());
        try { Accent = (Color)ColorConverter.ConvertFromString(s.AccentColor); } catch { }
        d["Accent"] = Freeze(Accent);
        d["Gold"] = Freeze(Accent);
        if (!IsLight) return;
        d["Bg"] = Brush("#F4F4F6");
        d["Panel"] = Brush("#FFFFFF");
        d["Panel2"] = Brush("#ECECF0");
        d["Hover"] = Brush("#E3E3E8");
        d["Line"] = Brush("#DCDCE2");
        d["Fg"] = Brush("#141417");
        d["Fg2"] = Brush("#5E5E66");
        d["Fg3"] = Brush("#8E8E96");
    }

    static bool SystemIsLight()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    static SolidColorBrush Brush(string hex) => Freeze((Color)ColorConverter.ConvertFromString(hex));
    static SolidColorBrush Freeze(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
