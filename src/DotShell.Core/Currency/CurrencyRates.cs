namespace DotShell.Core.Currency;

using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;

/// <summary>Одна точка курса: дата и значение в рублях.</summary>
public sealed record RatePoint(DateTime Date, double Value);

/// <summary>Курс валюты за период: последнее значение, предыдущее и история.</summary>
public sealed record RateSeries(string Code, string Title, IReadOnlyList<RatePoint> History)
{
    public double Last => History.Count > 0 ? History[^1].Value : 0;
    public double Previous => History.Count > 1 ? History[^2].Value : Last;
    public double Delta => Last - Previous;
    public double DeltaPercent => Previous > 0 ? (Last - Previous) / Previous * 100 : 0;
    public bool Growing => Delta >= 0;
}

/// <summary>
/// Курсы доллара и юаня к рублю — с сайта ЦБ РФ, без ключей и регистраций.
///
/// Почему ЦБ, а не «красивый» API: у большинства бесплатных сервисов теперь
/// нужен ключ, а ЦБ отдаёт официальный курс открытым XML и сразу диапазоном
/// дат (XML_dynamic.asp) — то есть график за неделю/месяц получается одним
/// запросом, а не тридцатью.
///
/// Кэш на 10 минут: курс ЦБ меняется раз в день, дёргать его на каждый тик
/// виджета бессмысленно и невежливо.
/// </summary>
public static class CurrencyRates
{
    /// <summary>Коды ЦБ: доллар США и юань.</summary>
    public const string UsdCode = "R01235";
    public const string CnyCode = "R01375";

    /// <summary>Какие валюты можно выбрать второй (коды ЦБ и подписи).</summary>
    public static readonly (string Code, string Title)[] Options =
    {
        (CnyCode, "ЮАНЬ"),
        ("R01239", "ЕВРО"),
        ("R01035", "ФУНТ"),
        ("R01820", "ИЕНА"),
        ("R01335", "ТЕНГЕ"),
        ("R01720", "ГРИВНА"),
    };

    public static string Title(string code)
    {
        foreach (var (option, title) in Options)
            if (option == code) return title;

        return code == UsdCode ? "ДОЛЛАР" : code;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    private static readonly Dictionary<string, (DateTime At, RateSeries Series)> Cache = new();
    private static readonly object Gate = new();

    /// <summary>Курс за последние <paramref name="days"/> дней (включая сегодняшний).</summary>
    public static async Task<RateSeries?> GetAsync(string code, int days, CancellationToken cancel = default)
    {
        var key = $"{code}:{days}";
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached) && DateTime.Now - cached.At < TimeSpan.FromMinutes(10))
                return cached.Series;
        }

        try
        {
            var to = DateTime.Today;
            var from = to.AddDays(-Math.Max(1, days));
            var url = "https://www.cbr.ru/scripts/XML_dynamic.asp" +
                      $"?date_req1={from:dd/MM/yyyy}&date_req2={to:dd/MM/yyyy}&VAL_NM_RQ={code}";

            using var response = await Http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return null;

            // Кодировка XML — windows-1251, но нам нужны только даты и числа (ASCII),
            // поэтому читаем как есть и не тащим провайдер кодовых страниц.
            var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
            var text = System.Text.Encoding.Latin1.GetString(bytes);

            var doc = XDocument.Parse(text);
            var points = new List<RatePoint>();

            foreach (var record in doc.Descendants("Record"))
            {
                var dateText = (string?)record.Attribute("Date");
                var valueText = (string?)record.Element("Value");
                var nominalText = (string?)record.Element("Nominal");

                if (!DateTime.TryParseExact(dateText, "dd.MM.yyyy", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    continue;

                var normalized = (valueText ?? string.Empty).Replace(',', '.');
                if (!double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    continue;

                var nominal = 1.0;
                if (double.TryParse((nominalText ?? "1").Replace(',', '.'), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var n) && n > 0)
                    nominal = n;

                points.Add(new RatePoint(date, value / nominal));
            }

            if (points.Count == 0) return null;

            var series = new RateSeries(code, Title(code), points);
            lock (Gate) Cache[key] = (DateTime.Now, series);
            return series;
        }
        catch
        {
            // Нет сети/ЦБ недоступен — виджет покажет то, что уже было, или «—».
            return null;
        }
    }
}
