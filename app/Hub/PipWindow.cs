using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace CoolPlayer;

/// <summary>
/// Картинка-в-картинке: маленькое окно поверх всех, куда «пересаживается» вкладка YouTube.
/// Видео не перезагружается — меняется только родительское окно WebView2.
/// </summary>
sealed class PipWindow : Window
{
    const string EnterCss = """
        (() => {
          let s = document.getElementById('cp-pip');
          if (!s) { s = document.createElement('style'); s.id = 'cp-pip'; document.documentElement.appendChild(s); }
          s.textContent = `
            html, body { overflow: hidden !important; background: #000 !important; }
            #movie_player, ytd-shorts #shorts-player {
              position: fixed !important; inset: 0 !important; width: 100vw !important; height: 100vh !important;
              z-index: 2147483647 !important; background: #000 !important; }
            #movie_player video { width: 100vw !important; height: 100vh !important; left: 0 !important; top: 0 !important; object-fit: contain !important; }
            #movie_player .ytp-chrome-bottom { width: calc(100vw - 24px) !important; left: 12px !important; }
            #masthead-container, #secondary, #below, #guide, ytd-mini-guide-renderer { visibility: hidden !important; }`;
          window.dispatchEvent(new Event('resize'));
        })();
        """;

    const string ExitCss = "document.getElementById('cp-pip')?.remove(); window.dispatchEvent(new Event('resize'));";

    readonly WebSurface surface;
    readonly Border slot = new() { Margin = new Thickness(5, 0, 5, 5) };
    IntPtr hwnd;

    public PipWindow(WebSurface surface)
    {
        this.surface = surface;
        Title = "Taskbeat — картинка в картинке";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = false;
        Background = Brushes.Black;
        Width = 520;
        Height = 322;
        MinWidth = 280;
        MinHeight = 190;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false,
        });

        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 24;
        Top = wa.Bottom - Height - 24;

        // верхняя полоска: перетаскивание, «вернуть в Taskbeat», «закрыть»
        var bar = new DockPanel { Height = 32, Background = Brushes.Black, LastChildFill = true };
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) AppHost.Web.ExitPip(true);
            else DragMove();
        };
        var close = BarButton("", "Закрыть и вернуть видео в Taskbeat", () => AppHost.Web.ExitPip(false));
        var back = BarButton("", "Открыть в Taskbeat", () => AppHost.Web.ExitPip(true));
        DockPanel.SetDock(close, Dock.Right);
        DockPanel.SetDock(back, Dock.Right);
        bar.Children.Add(close);
        bar.Children.Add(back);
        bar.Children.Add(new TextBlock
        {
            Text = "YouTube", Foreground = new SolidColorBrush(Color.FromRgb(150, 150, 158)), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
        });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(slot);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int round = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        };
        Loaded += async (_, _) =>
        {
            await Dispatcher.InvokeAsync(Place, System.Windows.Threading.DispatcherPriority.Loaded);
            try { await surface.Web!.ExecuteScriptAsync(EnterCss); } catch { }
        };
        slot.SizeChanged += (_, _) => Place();
        LocationChanged += (_, _) => surface.Controller?.NotifyParentWindowPositionChanged();
        // Возвращаем WebView2 в хаб ДО уничтожения окна: иначе он погибнет вместе с родителем
        Closing += (_, _) =>
        {
            surface.InPip = false;
            var c = surface.Controller;
            if (c == null) return;
            c.IsVisible = false;
            c.ParentWindow = AppHost.HubHandle;
        };
        Closed += async (_, _) =>
        {
            AppHost.RefreshHubWeb();
            try { await surface.Web!.ExecuteScriptAsync(ExitCss); } catch { }
        };
    }

    static Button BarButton(string glyph, string tip, Action click)
    {
        var b = new Button
        {
            Content = glyph, ToolTip = tip, Width = 40, Height = 32, FontSize = 12,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 206)),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Template = FlatTemplate(),
        };
        b.Click += (_, _) => click();
        return b;
    }

    static ControlTemplate FlatTemplate()
    {
        var t = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "b";
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        t.VisualTree = border;
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(38, 38, 42)), "b"));
        t.Triggers.Add(hover);
        return t;
    }

    void Place()
    {
        if (hwnd == IntPtr.Zero || slot.ActualWidth < 1 || slot.ActualHeight < 1) return;
        double s = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var p = slot.TranslatePoint(new Point(0, 0), this);
        surface.Show(hwnd, new System.Drawing.Rectangle((int)(p.X * s), (int)(p.Y * s),
            (int)Math.Ceiling(slot.ActualWidth * s), (int)Math.Ceiling(slot.ActualHeight * s)));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
