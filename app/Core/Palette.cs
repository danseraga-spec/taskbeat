using System.Windows.Media;

namespace CoolPlayer;

/// <summary>Акцентный цвет из обложки — один и тот же у острова и хаба.</summary>
static class Palette
{
    public static Color Accent(Color raw)
    {
        var (h, s, l) = ToHsl(raw);
        return FromHsl(h, Math.Max(s, 0.5), Math.Clamp(l, 0.6, 0.76));
    }

    public static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), l = (mx + mn) / 2;
        if (mx - mn < 1e-6) return (0, 0, l);
        double d = mx - mn, s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
        double h = mx == r ? (g - b) / d + (g < b ? 6 : 0) : mx == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1;
            if (t > 1) t -= 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2 / 3.0) return p + (q - p) * (2 / 3.0 - t) * 6;
            return p;
        }
        return Color.FromRgb((byte)(Hue(p, q, h + 1 / 3.0) * 255), (byte)(Hue(p, q, h) * 255), (byte)(Hue(p, q, h - 1 / 3.0) * 255));
    }
}
