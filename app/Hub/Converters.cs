using System.Globalization;
using System.Windows.Data;

namespace CoolPlayer;

/// <summary>(значение, минимум, максимум, ширина) → ширина заливки дорожки.</summary>
public sealed class FractionWidth : IMultiValueConverter
{
    public object Convert(object[] v, Type t, object p, CultureInfo c)
    {
        if (v.Length < 4 || v[0] is not double val || v[1] is not double min || v[2] is not double max || v[3] is not double w) return 0.0;
        if (max <= min || double.IsNaN(w)) return 0.0;
        return Math.Clamp((val - min) / (max - min), 0, 1) * w;
    }

    public object[] ConvertBack(object v, Type[] t, object p, CultureInfo c) => throw new NotSupportedException();
}
