namespace DotShell.Core.Weather;

using System.Globalization;
using System.Net.Http;
using System.Text.Json;

/// <summary>Текущая погода в городе.</summary>
public sealed record WeatherNow(
    string City,
    double Temperature,
    double FeelsLike,
    double Wind,
    double Humidity,
    string Condition,
    DateTime At)
{
    /// <summary>Русское описание состояния по английской строке сервиса.</summary>
    public static string Translate(string english)
    {
        var text = english.ToLowerInvariant();

        return text switch
        {
            _ when text.Contains("thunder") => "гроза",
            _ when text.Contains("heavy snow") || text.Contains("blizzard") => "сильный снег",
            _ when text.Contains("snow") || text.Contains("sleet") || text.Contains("ice") => "снег",
            _ when text.Contains("heavy rain") => "сильный дождь",
            _ when text.Contains("moderate rain") => "дождь",
            _ when text.Contains("light rain") || text.Contains("drizzle") => "небольшой дождь",
            _ when text.Contains("patchy rain") || text.Contains("shower") => "ливень",
            _ when text.Contains("freezing") => "ледяной дождь",
            _ when text.Contains("fog") || text.Contains("mist") => "туман",
            _ when text.Contains("overcast") => "пасмурно",
            _ when text.Contains("cloud") || text.Contains("overcast") => "облачно",
            _ when text.Contains("sunny") || text.Contains("clear") => "ясно",
            _ => english,
        };
    }

    /// <summary>Значок из Segoe Fluent Icons под состояние.</summary>
    public string Glyph
    {
        get
        {
            var text = Condition.ToLowerInvariant();

            if (text.Contains("гроза")) return "\uE7BA";
            if (text.Contains("снег")) return "\uE72B";
            if (text.Contains("дожд") || text.Contains("ливень") || text.Contains("морос")) return "\uE753";
            if (text.Contains("туман")) return "\uE753";
            if (text.Contains("облач") || text.Contains("пасмур")) return "\uE707";
            return "\uE706";
        }
    }
}

/// <summary>
/// Погода без ключей и регистраций.
///
/// Два источника, потому что один может быть недоступен: у владельца, например,
/// `api.open-meteo.com` не отвечает, а `wttr.in` работает. Сначала пробуем
/// Open-Meteo (точные координаты и коды WMO), при неудаче — wttr.in (короткий
/// текстовый ответ). Оба кэшируются на 15 минут.
/// </summary>
public static class WeatherService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private static readonly Dictionary<string, (DateTime At, WeatherNow Data)> Cache = new();
    private static readonly object Gate = new();

    public static async Task<WeatherNow?> GetAsync(string city, CancellationToken cancel = default)
    {
        city = (city ?? string.Empty).Trim();
        if (city.Length == 0) city = "Москва";

        lock (Gate)
        {
            if (Cache.TryGetValue(city, out var cached) && DateTime.Now - cached.At < TimeSpan.FromMinutes(15))
                return cached.Data;
        }

        var weather = await FromOpenMeteoAsync(city, cancel) ?? await FromWttrAsync(city, cancel);
        if (weather is null) return null;

        lock (Gate) Cache[city] = (DateTime.Now, weather);
        return weather;
    }

    /// <summary>Сбросить кэш: город сменили — старые данные больше не нужны.</summary>
    public static void ClearCache()
    {
        lock (Gate) Cache.Clear();
    }

    private static async Task<WeatherNow?> FromOpenMeteoAsync(string city, CancellationToken cancel)
    {
        try
        {
            var geo = $"https://geocoding-api.open-meteo.com/v1/search" +
                      $"?name={Uri.EscapeDataString(city)}&count=1&language=ru&format=json";

            using var geoResponse = await Http.GetAsync(geo, cancel);
            if (!geoResponse.IsSuccessStatusCode) return null;

            using var geoStream = await geoResponse.Content.ReadAsStreamAsync(cancel);
            using var geoJson = await JsonDocument.ParseAsync(geoStream, cancellationToken: cancel);

            if (!geoJson.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array ||
                results.GetArrayLength() == 0)
                return null;

            var first = results[0];
            double lat = first.GetProperty("latitude").GetDouble();
            double lon = first.GetProperty("longitude").GetDouble();

            var forecast = "https://api.open-meteo.com/v1/forecast" +
                           $"?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                           $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                           "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m,relative_humidity_2m" +
                           "&timezone=auto";

            using var response = await Http.GetAsync(forecast, cancel);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancel);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancel);

            if (!json.RootElement.TryGetProperty("current", out var current)) return null;

            double Number(string name) =>
                current.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
                    ? value.GetDouble()
                    : 0;

            int code = current.TryGetProperty("weather_code", out var codeValue) &&
                       codeValue.ValueKind == JsonValueKind.Number
                ? codeValue.GetInt32()
                : 3;

            return new WeatherNow(
                city,
                Number("temperature_2m"),
                Number("apparent_temperature"),
                Number("wind_speed_10m"),
                Number("relative_humidity_2m"),
                Describe(code),
                DateTime.Now);
        }
        catch { return null; }
    }

    /// <summary>Расшифровка кодов WMO — на случай, когда сработал Open-Meteo.</summary>
    private static string Describe(int code) => code switch
    {
        0 => "ясно",
        1 => "почти ясно",
        2 => "переменная облачность",
        3 => "пасмурно",
        45 or 48 => "туман",
        51 or 53 or 55 => "морось",
        56 or 57 => "ледяная морось",
        61 or 63 => "дождь",
        65 => "сильный дождь",
        66 or 67 => "ледяной дождь",
        71 or 73 => "снег",
        75 => "сильный снег",
        77 => "снежная крупа",
        80 or 81 => "ливень",
        82 => "сильный ливень",
        85 or 86 => "снегопад",
        95 => "гроза",
        96 or 99 => "гроза с градом",
        _ => "облачно",
    };

    private static async Task<WeatherNow?> FromWttrAsync(string city, CancellationToken cancel)
    {
        try
        {
            var url = $"https://wttr.in/{Uri.EscapeDataString(city)}?format=%t|%C|%w|%h&m";
            using var response = await Http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return null;

            var text = (await response.Content.ReadAsStringAsync(cancel)).Trim();
            var parts = text.Split('|');
            if (parts.Length < 3) return null;

            double temperature = ParseNumber(parts[0]);
            var condition = WeatherNow.Translate(parts[1]);
            double wind = ParseNumber(parts[2]);
            double humidity = parts.Length > 3 ? ParseNumber(parts[3]) : 0;

            return new WeatherNow(city, temperature, temperature, wind, humidity, condition, DateTime.Now);
        }
        catch { return null; }
    }

    /// <summary>«+7°C» → 7, «↙6km/h» → 6, «85%» → 85.</summary>
    private static double ParseNumber(string raw)
    {
        var digits = new string(raw.Where(ch => char.IsDigit(ch) || ch == '-' || ch == '+' || ch == '.' || ch == ',')
            .ToArray())
            .Replace(',', '.');

        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }
}
