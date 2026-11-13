using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;

namespace CoolPlayer;

public sealed record Option<T>(T Value, string Name);

public static class Options
{
    public static Option<InfoKind>[] InfoKinds { get; } =
    [
        new(InfoKind.Nothing, "Ничего (пустая пилюля)"),
        new(InfoKind.Clock, "Часы"),
        new(InfoKind.Weather, "Погода"),
        new(InfoKind.ClockWeather, "Часы и погода"),
        new(InfoKind.Date, "Дата"),
        new(InfoKind.NextReminder, "Ближайшее напоминание"),
        new(InfoKind.Custom, "Свой текст"),
    ];

    public static Option<double>[] Scales { get; } =
    [
        new(0, "Авто (по разрешению экрана)"),
        new(1, "100 %"),
        new(1.2, "120 %"),
        new(1.4, "140 %"),
        new(1.6, "160 %"),
    ];

    public static Option<Repeat>[] Repeats { get; } =
    [
        new(Repeat.None, "Один раз"),
        new(Repeat.Daily, "Каждый день"),
        new(Repeat.Weekdays, "По будням"),
        new(Repeat.Weekly, "Каждую неделю"),
    ];
}

internal partial class IslandSettingsPage : UserControl
{
    AppSettings model = null!;

    public IslandSettingsPage()
    {
        InitializeComponent();
        Reload();

        // напоминания правятся «вживую» — их меняет и сам остров (Готово/Отложить)
        RemList.ItemsSource = SettingsStore.Current.Reminders;
        SettingsStore.Current.Reminders.CollectionChanged += OnRemindersChanged;
        Unloaded += (_, _) => SettingsStore.Current.Reminders.CollectionChanged -= OnRemindersChanged;
        Loaded += (_, _) =>
        {
            SettingsStore.Current.Reminders.CollectionChanged -= OnRemindersChanged;
            SettingsStore.Current.Reminders.CollectionChanged += OnRemindersChanged;
        };
        RemDate.SelectedDate = DateTime.Today;
        UpdateEmpty();

    }

    /// <summary>Перечитать настройки (после сохранения или отмены).</summary>
    void Reload()
    {
        model = SettingsStore.Current.Clone();
        DataContext = model;
        AutoStartBox.IsChecked = Autostart.Enabled;
        CityStatus.Text = model.Lat != null ? $"Сейчас: {model.CityResolved}" : "Город не задан — погода не показывается";
        SaveError.Text = "";
    }

    public void SelectTab(int i) => Tabs.SelectedIndex = i;

    void OnRemindersChanged(object? s, NotifyCollectionChangedEventArgs e) => UpdateEmpty();
    void UpdateEmpty() => RemEmpty.Visibility = SettingsStore.Current.Reminders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void CenterOffset(object s, RoutedEventArgs e) => OffsetSlider.Value = 0;

    void AddRule(object s, RoutedEventArgs e) => model.Rules.Add(new ScheduleRule());

    void DeleteRule(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is ScheduleRule r) model.Rules.Remove(r);
    }

    void AddReminder(object s, RoutedEventArgs e)
    {
        RemError.Text = "";
        string text = RemText.Text.Trim();
        if (text.Length == 0) { RemError.Text = "Напишите, о чём напомнить."; return; }
        if (!TimeSpan.TryParse(RemTime.Text.Trim(), out var t) || t < TimeSpan.Zero || t >= TimeSpan.FromDays(1))
        { RemError.Text = "Время в формате ЧЧ:ММ, например 18:30."; return; }
        var repeat = (Repeat)(RemRepeat.SelectedValue ?? Repeat.None);
        var when = (RemDate.SelectedDate ?? DateTime.Today).Date + new TimeSpan(t.Hours, t.Minutes, 0);
        var r = new Reminder { Text = text, When = when, Repeat = repeat };
        if (when <= DateTime.Now)
        {
            if (repeat == Repeat.None) { RemError.Text = "Это время уже прошло."; return; }
            r.When = r.Next(DateTime.Now) ?? when;
        }
        SettingsStore.Current.Reminders.Add(r);
        SettingsStore.Touch();
        RemText.Text = "";
    }

    void DeleteReminder(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is Reminder r)
        {
            SettingsStore.Current.Reminders.Remove(r);
            SettingsStore.Touch();
        }
    }

    async void FindCity(object s, RoutedEventArgs e) => await ResolveCity();

    async Task<bool> ResolveCity()
    {
        string city = model.City.Trim();
        if (city.Length == 0)
        {
            model.Lat = model.Lon = null;
            model.CityResolved = "";
            CityStatus.Text = "Город не задан — погода не показывается";
            return true;
        }
        CityStatus.Text = "Ищу…";
        try
        {
            var r = await WeatherService.GeocodeAsync(city);
            if (r == null) { CityStatus.Text = "Такой город не найден"; return false; }
            (model.Lat, model.Lon, model.CityResolved) = (r.Value.lat, r.Value.lon, r.Value.name);
            CityStatus.Text = $"Найдено: {r.Value.name}";
            return true;
        }
        catch
        {
            CityStatus.Text = "Нет связи с сервером погоды";
            return false;
        }
    }

    async void SaveClick(object s, RoutedEventArgs e)
    {
        SaveError.Text = "";
        foreach (var r in model.Rules)
            if (!TimeSpan.TryParse(r.From, out _) || !TimeSpan.TryParse(r.To, out _))
            {
                Tabs.SelectedIndex = 1;
                SaveError.Text = $"Неверное время в расписании: «{r.From}» – «{r.To}». Формат ЧЧ:ММ.";
                return;
            }

        var cur = SettingsStore.Current;
        if (model.City.Trim() != cur.City.Trim() || (model.City.Trim().Length > 0 && model.Lat == null))
        {
            if (!await ResolveCity()) { Tabs.SelectedIndex = 3; SaveError.Text = "Не удалось найти город."; return; }
        }

        if (AutoStartBox.IsChecked != Autostart.Enabled) Autostart.Enabled = AutoStartBox.IsChecked == true;
        model.Reminders = cur.Reminders; // живой список, не копия
        // поля плеера меняются вне этой страницы — не затираем их
        model.Volume = cur.Volume; model.Shuffle = cur.Shuffle; model.RepeatOne = cur.RepeatOne;
        model.StartMinimized = cur.StartMinimized; model.SuspendTabs = cur.SuspendTabs; model.LastPage = cur.LastPage;
        SettingsStore.Replace(model);
        Reload();
        AppHost.Toast("Настройки острова сохранены");
    }

    void CloseClick(object s, RoutedEventArgs e) => Reload();
}
