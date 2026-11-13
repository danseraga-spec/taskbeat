using System.Windows.Threading;

namespace CoolPlayer;

/// <summary>
/// Один таймер, который просыпается ровно на границе минуты: обновляет часы,
/// проверяет правила расписания и напоминания. Между минутами — ни одного тика.
/// </summary>
sealed class Scheduler
{
    readonly DispatcherTimer timer = new(DispatcherPriority.Background);
    readonly HashSet<Guid> fired = [];

    public event Action? MinuteTick;
    public event Action<Reminder>? ReminderDue;

    public Scheduler()
    {
        timer.Tick += (_, _) => { timer.Stop(); Check(); Arm(); };
        SettingsStore.Changed += Check;
    }

    public void Start()
    {
        Check();
        Arm();
    }

    void Arm()
    {
        var now = DateTime.Now;
        var next = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
        timer.Interval = next - now + TimeSpan.FromMilliseconds(40);
        timer.Start();
    }

    public void Check()
    {
        var now = DateTime.Now;
        foreach (var r in SettingsStore.Current.Reminders.ToList())
            if (r.DueAt <= now && fired.Add(r.Id))
                ReminderDue?.Invoke(r);
        MinuteTick?.Invoke();
    }

    public static ScheduleRule? ActiveRule() =>
        SettingsStore.Current.Rules.FirstOrDefault(r => r.Matches(DateTime.Now));

    public static Reminder? NextReminder() =>
        SettingsStore.Current.Reminders.Where(r => r.DueAt > DateTime.Now).MinBy(r => r.DueAt);

    public void Snooze(Reminder r, TimeSpan by)
    {
        r.SnoozedUntil = DateTime.Now + by;
        fired.Remove(r.Id);
        SettingsStore.Touch();
    }

    public void Done(Reminder r)
    {
        var list = SettingsStore.Current.Reminders;
        var next = r.Next(DateTime.Now);
        if (next is { } n) { r.When = n; r.SnoozedUntil = null; }
        else list.Remove(r);
        fired.Remove(r.Id);
        SettingsStore.Touch();
    }
}
