using System.Windows.Threading;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace CoolPlayer;

/// <summary>
/// Уведомления Windows → остров на основном мониторе.
/// Событие NotificationChanged доступно только упакованным приложениям, поэтому
/// раз в пару секунд смотрим список уведомлений и показываем новые.
/// </summary>
static class Notifications
{
    static UserNotificationListener? listener;
    static readonly HashSet<uint> seen = [];
    static DispatcherTimer? timer;
    static IslandWindow? host;
    static bool busy, primed;

    public static void Start(IslandWindow h)
    {
        host = h;
        try
        {
            listener = UserNotificationListener.Current;
            var status = listener.GetAccessStatus();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                Paths.Trace("уведомления: нет доступа (" + status + ")");
                listener = null;
                return;
            }
        }
        catch (Exception ex)
        {
            Paths.Trace("уведомления недоступны: " + ex.Message);
            listener = null;
            return;
        }
        BannerGuard.Apply();
        SettingsStore.Changed += BannerGuard.Apply;
        timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += async (_, _) => await CheckAsync();
        timer.Start();
        _ = CheckAsync();
    }

    public static void Stop()
    {
        timer?.Stop();
        BannerGuard.Restore();
    }

    static async Task CheckAsync()
    {
        if (listener == null || host == null || busy || !SettingsStore.Current.WinNotifications) return;
        timer!.Interval = TimeSpan.FromSeconds(Perf.Game ? 4 : 2);
        busy = true;
        try
        {
            var list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
            var ids = new HashSet<uint>();
            var fresh = new List<UserNotification>();
            foreach (var n in list)
            {
                ids.Add(n.Id);
                if (seen.Add(n.Id) && primed && DateTimeOffset.Now - n.CreationTime < TimeSpan.FromSeconds(30)) fresh.Add(n);
            }
            seen.IntersectWith(ids);
            primed = true;
            foreach (var n in fresh)
                try { BannerGuard.Seen(n.AppInfo?.AppUserModelId ?? ""); } catch { }
            // если пришло сразу несколько — показываем самое свежее
            foreach (var n in fresh.OrderByDescending(x => x.CreationTime))
            {
                string app = "";
                try { app = n.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                if (app.Contains("Taskbeat", StringComparison.OrdinalIgnoreCase) || app.Contains("CoolPlayer", StringComparison.OrdinalIgnoreCase)) continue;
                var texts = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)?.GetTextElements();
                if (texts == null || texts.Count == 0) continue;
                string title = texts[0].Text?.Trim() ?? "";
                string body = string.Join(" ", texts.Skip(1).Select(t => t.Text?.Trim()).Where(t => !string.IsNullOrEmpty(t)));
                body = body.Replace('\n', ' ').Replace('\r', ' ');
                if (app.Length > 0 && title.Length > 0 && !title.Equals(app, StringComparison.OrdinalIgnoreCase))
                    title = $"{app} · {title}";
                host.ShowNotification(app, title, body);
                break;
            }
        }
        catch (Exception ex) { Paths.Trace("уведомления: " + ex.Message); }
        finally { busy = false; }
    }
}
