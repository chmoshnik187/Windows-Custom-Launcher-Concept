namespace DotShell.Core.Updates;

using System.Net.Http;
using System.Reflection;
using System.Text.Json;

/// <summary>Сведения о свежем релизе на GitHub.</summary>
public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string PageUrl,
    string? DownloadUrl,
    string Notes);

/// <summary>
/// Проверка новых версий по GitHub Releases.
///
/// Зачем это вообще нужно: остров обновляется руками (скачать архив,
/// распаковать, закрыть старый, запустить install.bat) — и человек делает
/// это всё реже, чем выходят версии. Проверка раз в несколько часов и
/// ненавязчивый пузырь над треем решают ровно одну задачу: «ты не знаешь,
/// что вышло новое» превращается в «ты знаешь и решаешь сам».
///
/// Что здесь сознательно НЕ делается: программа не качает и не подменяет
/// себя без спроса. Автообновление — это отдельный шаг (нужен надёжный
/// размен файла на живой процесс), и делать его «на глазок» опаснее,
/// чем оставить одно нажатие человеку.
/// </summary>
public static class UpdateChecker
{
    private const string Repo = "chmoshnik187/Windows-Custom-Launcher-Concept";
    private const string LatestUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    /// <summary>
    /// Подмена версии для проверки обновлений: `--fake-version=0.1.0` заставляет
    /// шелл думать, что он старый, чтобы увидеть уведомление, не откатывая установку.
    /// </summary>
    public static Version? SimulatedVersion { get; set; }

    /// <summary>Версия запущенной сборки (из Directory.Build.props).</summary>
    public static Version Current
    {
        get
        {
            if (SimulatedVersion is not null) return SimulatedVersion;

            var text = typeof(UpdateChecker).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

            // InformationalVersion бывает с суффиксом сборки: "0.6.0+abc123".
            var plus = text.IndexOf('+');
            if (plus > 0) text = text[..plus];

            return Version.TryParse(text, out var version) ? version : new Version(0, 0, 0);
        }
    }

    public static string CurrentText => Current.ToString(3);

    /// <summary>Версия без подмены — её показываем как «настоящую».</summary>
    public static string ActualText
    {
        get
        {
            var saved = SimulatedVersion;
            SimulatedVersion = null;
            try { return Current.ToString(3); }
            finally { SimulatedVersion = saved; }
        }
    }

    /// <summary>Подпись для интерфейса: настоящая версия плюс пометка теста.</summary>
    public static string CurrentTextWithHint =>
        SimulatedVersion is null ? ActualText : $"{ActualText} (тест обновления: как {SimulatedVersion.ToString(3)})";

    /// <summary>
    /// Спросить GitHub о последнем релизе. null — если сеть недоступна,
    /// GitHub молчит или релизов ещё нет: это не ошибка, а нормальная жизнь.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancel = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            // GitHub отказывает без User-Agent — это их правило, не наше.
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DotShell-UpdateCheck");

            using var resp = await http.GetAsync(LatestUrl, cancel);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cancel);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? string.Empty : string.Empty;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;

            string? download = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (asset.TryGetProperty("browser_download_url", out var d))
                        download = d.GetString();
                    break;
                }
            }

            var version = ParseTag(tag);
            if (version is null) return null;

            return new UpdateInfo(version, tag, page, download, notes);
        }
        catch
        {
            return null;
        }
    }

    private static Version? ParseTag(string tag)
    {
        var text = tag.TrimStart('v', 'V').Trim();
        var dash = text.IndexOf('-');
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var version) ? version : null;
    }
}
