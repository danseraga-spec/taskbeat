using System.Windows.Markup;

namespace CoolPlayer;

/// <summary>
/// Перевод интерфейса. Ключ — русская строка, как она написана в коде;
/// для английского берётся перевод из словаря (Strings.En.cs). Язык меняется после перезапуска.
/// </summary>
static partial class L
{
    public static bool En { get; private set; }

    public static void Init(string lang) => En = lang == "en" ||
        (lang == "auto" && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase)
                         && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("uk", StringComparison.OrdinalIgnoreCase)
                         && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("be", StringComparison.OrdinalIgnoreCase)
                         && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("kk", StringComparison.OrdinalIgnoreCase));

    public static string T(string ru) => En && ru != null && En_.TryGetValue(ru, out var en) ? en : ru!;

    /// <summary>Строка с подстановками: L.F("Добавлено в «{0}»", name).</summary>
    public static string F(string ru, params object?[] args) => string.Format(T(ru), args);
}

/// <summary>
/// Перевод разметки XAML без правки каждого файла: при загрузке любого элемента его русские
/// подписи (Text, Content, Header, ToolTip, подсказка в поле поиска, заголовок окна) заменяются по словарю.
/// </summary>
static class XamlTranslator
{
    public static void Register()
    {
        if (!L.En) return;
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.FrameworkElement), System.Windows.FrameworkElement.LoadedEvent,
            new System.Windows.RoutedEventHandler((s, _) => Translate((System.Windows.DependencyObject)s)), true);
        System.Windows.EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Controls.ContextMenu.OpenedEvent,
            new System.Windows.RoutedEventHandler((s, _) => Translate((System.Windows.DependencyObject)s)), true);
    }

    static void Translate(System.Windows.DependencyObject root)
    {
        var stack = new Stack<object>();
        stack.Push(root);
        int guard = 0;
        while (stack.Count > 0 && guard++ < 5000)
        {
            var o = stack.Pop();
            switch (o)
            {
                case System.Windows.Controls.TextBlock tb:
                    if (tb.Inlines.Count > 0 && System.Windows.Data.BindingOperations.GetBindingExpression(tb, System.Windows.Controls.TextBlock.TextProperty) == null)
                        foreach (var r in tb.Inlines.OfType<System.Windows.Documents.Run>().ToList()) r.Text = Tr(r.Text);
                    break;
                case System.Windows.Window w:
                    w.Title = Tr(w.Title);
                    break;
            }
            if (o is System.Windows.Controls.ContentControl { Content: string c } cc && !IsBound(cc, System.Windows.Controls.ContentControl.ContentProperty)) cc.Content = Tr(c);
            if (o is System.Windows.Controls.HeaderedContentControl { Header: string h } hc) hc.Header = Tr(h);
            if (o is System.Windows.Controls.HeaderedItemsControl { Header: string hi } hic) hic.Header = Tr(hi);
            if (o is System.Windows.FrameworkElement { ToolTip: string tt } fe) fe.ToolTip = Tr(tt);
            if (o is System.Windows.Controls.TextBox { Tag: string tag } tbx) tbx.Tag = Tr(tag);
            if (o is System.Windows.DependencyObject d)
                foreach (var child in System.Windows.LogicalTreeHelper.GetChildren(d)) if (child is System.Windows.DependencyObject) stack.Push(child);
        }
    }

    static bool IsBound(System.Windows.DependencyObject d, System.Windows.DependencyProperty p) => System.Windows.Data.BindingOperations.IsDataBound(d, p);

    static string Tr(string s)
    {
        if (s.Length == 0) return s;
        var t = L.T(s);
        if (!ReferenceEquals(t, s)) return t;
        var trimmed = s.Trim();
        return trimmed.Length != s.Length ? L.T(trimmed) : s;
    }
}

/// <summary>Для XAML: Text="{local:T Моя музыка}".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string text) => Text = text;
    [ConstructorArgument("text")] public string Text { get; set; } = "";
    public override object ProvideValue(IServiceProvider sp) => L.T(Text);
}
