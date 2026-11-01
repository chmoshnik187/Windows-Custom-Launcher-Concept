namespace DotShell.Core.Clipboard;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>Умное действие над свежей копией из буфера.</summary>
public sealed record SmartAction(string Id, string Label);

/// <summary>
/// «Алхимия буфера»: скопировал HEX — получи конвертацию в RGB, скопировал
/// JSON — форматирование, ссылку — открытие и очистку от хвостовиков-трекеров.
/// Распознаём только однозначные случаи, никаких «угадайлок»: шелл не имеет
/// права предлагать магию, которая разочарует.
/// </summary>
public static partial class SmartClip
{
    public static List<SmartAction> Detect(string? text)
    {
        var actions = new List<SmartAction>();
        if (string.IsNullOrWhiteSpace(text)) return actions;
        var t = text.Trim();

        if (HexRegex().IsMatch(t))
        {
            actions.Add(new("hex-rgb", "в RGB"));
            actions.Add(new("hex-inv", "инвертировать цвет"));
        }

        if (t.StartsWith('{') || t.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(t);
                actions.Add(new("json-pretty", "форматировать JSON"));
                actions.Add(new("json-min", "сжать JSON"));
            }
            catch { }
        }

        if (Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            actions.Add(new("url-open", "открыть в браузере"));
            if (HasTrackingParams(uri)) actions.Add(new("url-clean", "убрать трекеры из ссылки"));
            actions.Add(new("url-domain", "скопировать домен"));
        }

        return actions.Take(4).ToList();
    }

    /// <summary>Новый текст для буфера; null = действие делает не текст (открыть ссылку).</summary>
    public static string? Transform(string id, string text)
    {
        var t = text.Trim();
        switch (id)
        {
            case "hex-rgb":
            {
                var (r, g, b) = HexToRgb(t);
                return $"rgb({r}, {g}, {b})";
            }
            case "hex-inv":
            {
                var (r, g, b) = HexToRgb(t);
                return $"#{255 - r:X2}{255 - g:X2}{255 - b:X2}";
            }
            case "json-pretty":
            {
                using var doc = JsonDocument.Parse(t);
                return JsonSerializer.Serialize(doc.RootElement,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            case "json-min":
            {
                using var doc = JsonDocument.Parse(t);
                return JsonSerializer.Serialize(doc.RootElement);
            }
            case "url-clean":
            {
                var uri = new UriBuilder(t);
                var query = uri.Query.TrimStart('?');
                var kept = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                    .Where(p => !IsTracking(p.Split('=')[0]));
                uri.Query = string.Join("&", kept);
                return uri.Uri.AbsoluteUri;
            }
            case "url-domain":
                return new Uri(t).Host;
            default:
                return null;
        }
    }

    /// <summary>true = действие не про текст, а про запуск (браузер).</summary>
    public static bool IsLaunch(string id) => id == "url-open";

    // ------------------------------------------------------------------

    [GeneratedRegex(@"^#?[0-9a-fA-F]{6}$")]
    private static partial Regex HexRegex();

    private static (int R, int G, int B) HexToRgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16));
    }

    private static readonly string[] Tracking =
        { "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "gclid", "fbclid", "yclid", "ref", "referrer" };

    private static bool IsTracking(string key) =>
        Tracking.Any(t => key.Equals(t, StringComparison.OrdinalIgnoreCase));

    private static bool HasTrackingParams(Uri uri) =>
        uri.Query.Split('&', '?').Any(p => IsTracking(p.Split('=')[0]));
}
