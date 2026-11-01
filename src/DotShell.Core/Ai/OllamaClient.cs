namespace DotShell.Core.Ai;

using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>Модель, которая уже лежит в Ollama.</summary>
public sealed record OllamaModel(string Name, long SizeBytes)
{
    public string SizeText => SizeBytes <= 0 ? "" : $"{SizeBytes / 1024.0 / 1024.0 / 1024.0:F1} ГБ";
}

/// <summary>
/// Прямой разговор с Ollama по её родному API (не OpenAI-совместимому):
/// список моделей и скачивание модели с прогрессом.
///
/// Зачем родной API: /api/pull отдаёт поток строк JSON, в которых есть
/// "total" и "completed" — из них получается честный прогресс загрузки.
/// Через OpenAI-совместимый эндпоинт такого нет вообще.
///
/// Здесь нет ни ключей, ни облака: всё локально, на 127.0.0.1.
/// </summary>
public static class OllamaClient
{
    public const string Root = "http://localhost:11434";

    /// <summary>Модель, которую предлагаем скачать, если у человека пусто.
    /// Маленькая, шустрая, прилично говорит по-русски — 1.4 ГБ вместо 20.</summary>
    public const string Suggested = "qwen3:1.7b";

    // Таймаут выключен: скачивание модели идёт минутами, и это нормально.
    private static readonly Lazy<HttpClient> LazyHttp =
        new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    private static HttpClient Http => LazyHttp.Value;

    /// <summary>Сервер Ollama отвечает?</summary>
    public static async Task<bool> IsRunningAsync(CancellationToken cancel = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(2));
            using var resp = await Http.GetAsync(Root + "/api/version", cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Все модели, которые Ollama видит у себя на диске.</summary>
    public static async Task<List<OllamaModel>> ListAsync(CancellationToken cancel = default)
    {
        var result = new List<OllamaModel>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(TimeSpan.FromSeconds(6));

            var json = await Http.GetStringAsync(Root + "/api/tags", cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in models.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                long size = 0;
                if (item.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes)) size = bytes;

                result.Add(new OllamaModel(name!, size));
            }

            result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // Сервер молчит — вернём пусто, интерфейс сам покажет, что делать.
        }

        return result;
    }

    /// <summary>
    /// Скачать модель. <paramref name="progress"/> получает человеческий статус
    /// («pulling manifest», «загрузка…») и долю 0..1 (может быть отрицательной,
    /// пока сервер не прислал total).
    /// </summary>
    public static async Task<bool> PullAsync(
        string model,
        Action<string, double> progress,
        CancellationToken cancel = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { model, stream = true });
            using var req = new HttpRequestMessage(HttpMethod.Post, Root + "/api/pull")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (!resp.IsSuccessStatusCode)
            {
                progress($"Ollama ответила {(int)resp.StatusCode}", -1);
                return false;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancel);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(cancel);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("error", out var error))
                    {
                        progress(error.GetString() ?? "ошибка загрузки", -1);
                        return false;
                    }

                    var status = root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";

                    double fraction = -1;
                    if (root.TryGetProperty("total", out var totalEl) &&
                        root.TryGetProperty("completed", out var doneEl) &&
                        totalEl.TryGetInt64(out var total) && total > 0 &&
                        doneEl.TryGetInt64(out var done))
                    {
                        fraction = (double)done / total;
                    }

                    progress(Translate(status), fraction);

                    if (status == "success") return true;
                }
                catch (JsonException)
                {
                    // Обрывок строки — не повод бросать загрузку.
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            progress("загрузка отменена", -1);
            return false;
        }
        catch (Exception ex)
        {
            progress("не удалось скачать: " + ex.Message, -1);
            return false;
        }
    }

    private static string Translate(string status) => status switch
    {
        "pulling manifest" => "получаю описание модели…",
        "verifying sha256 digest" => "проверяю контрольную сумму…",
        "writing manifest" => "записываю манифест…",
        "removing any unused layers" => "убираю лишнее…",
        "success" => "готово",
        _ when status.StartsWith("pulling ", StringComparison.Ordinal) => "скачиваю…",
        _ when status.StartsWith("downloading", StringComparison.Ordinal) => "скачиваю…",
        _ => status,
    };
}
