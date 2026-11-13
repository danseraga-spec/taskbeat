using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace CoolPlayer;

sealed record WeatherInfo(double Temp, double Feels, int Code, bool IsDay, double Max, double Min, double Wind)
{
    public string Desc => Code switch
    {
        0 => IsDay ? "Ясно" : "Ясная ночь",
        1 => "Малооблачно",
        2 => "Переменная облачность",
        3 => "Пасмурно",
        45 or 48 => "Туман",
        >= 51 and <= 57 => "Морось",
        >= 61 and <= 65 => "Дождь",
        66 or 67 => "Ледяной дождь",
        >= 71 and <= 77 => "Снег",
        >= 80 and <= 82 => "Ливень",
        85 or 86 => "Снегопад",
        >= 95 => "Гроза",
        _ => "—",
    };

    public static string Deg(double t) => $"{(t > 0.5 ? "+" : "")}{Math.Round(t):0}°";
}

/// <summary>Погода от Open-Meteo (бесплатно, без ключа). Обновляется раз в 30 минут.</summary>
sealed class WeatherService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(30) };

    public WeatherInfo? Current { get; private set; }
    public string Status { get; private set; } = "Город не указан";
    public event Action? Changed;

    readonly Dispatcher ui = Dispatcher.CurrentDispatcher;

    void RaiseChanged()
    {
        if (ui.CheckAccess()) Changed?.Invoke();
        else ui.InvokeAsync(() => Changed?.Invoke());
    }

    public WeatherService()
    {
        timer.Tick += async (_, _) => await RefreshAsync();
        SettingsStore.Changed += async () => await RefreshAsync();
    }

    public async void Start()
    {
        timer.Start();
        await RefreshAsync();
    }

    string? lastKey;

    public async Task RefreshAsync()
    {
        var s = SettingsStore.Current;
        if (s.Lat is not { } lat || s.Lon is not { } lon)
        {
            Current = null;
            Status = "Город не указан";
            RaiseChanged();
            return;
        }
        // при смене настроек не дёргаем сеть, если координаты те же и данные свежие
        string key = $"{lat}|{lon}";
        if (key == lastKey && Current != null && (DateTime.Now - fetched).TotalMinutes < 25) return;
        try
        {
            var inv = CultureInfo.InvariantCulture;
            string url = "https://api.open-meteo.com/v1/forecast" +
                         $"?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                         "&current=temperature_2m,apparent_temperature,weather_code,is_day,wind_speed_10m" +
                         "&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto&wind_speed_unit=ms";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var c = doc.RootElement.GetProperty("current");
            var d = doc.RootElement.GetProperty("daily");
            Current = new WeatherInfo(
                c.GetProperty("temperature_2m").GetDouble(),
                c.GetProperty("apparent_temperature").GetDouble(),
                c.GetProperty("weather_code").GetInt32(),
                c.GetProperty("is_day").GetInt32() == 1,
                d.GetProperty("temperature_2m_max")[0].GetDouble(),
                d.GetProperty("temperature_2m_min")[0].GetDouble(),
                c.GetProperty("wind_speed_10m").GetDouble());
            Status = s.CityResolved;
            lastKey = key;
            fetched = DateTime.Now;
        }
        catch (Exception ex)
        {
            Status = "Нет связи с сервером погоды";
            System.Diagnostics.Debug.WriteLine(ex);
        }
        RaiseChanged();
    }

    DateTime fetched;

    /// <summary>Город → координаты (геокодер Open-Meteo).</summary>
    public static async Task<(double lat, double lon, string name)?> GeocodeAsync(string city)
    {
        string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&format=json&name=" + Uri.EscapeDataString(city.Trim());
        using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
        var r = res[0];
        string name = r.GetProperty("name").GetString() ?? city;
        if (r.TryGetProperty("country", out var country)) name += ", " + country.GetString();
        return (r.GetProperty("latitude").GetDouble(), r.GetProperty("longitude").GetDouble(), name);
    }
}
