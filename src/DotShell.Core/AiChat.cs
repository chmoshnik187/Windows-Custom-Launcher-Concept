namespace DotShell.Core;

using System.Net.Http;
using System.Text;
using System.Text.Json;

/// <summary>
/// Мини-клиент быстрого чата для вкладки ИИ.
///
/// Сознательно "без агента": никаких инструментов, поиска, выполнения кода —
/// только переписка. История живёт в памяти до перезапуска острова
/// (это быстрый чат, а не архив разговоров).
///
/// Работает с ЛЮБЫМ OpenAI-совместимым эндпоинтом, поэтому провайдер
/// выбирается на вкладке: Qwen (DashScope), OpenRouter, OpenAI,
/// локальная Ollama или свой адрес. Один POST на сообщение,
/// ответ берём из choices[0].message.content.
/// </summary>
public sealed class AiChat
{
    /// <summary>Готовые пресеты провайдеров: имя, базовый адрес, модель по умолчанию, нужен ли ключ.</summary>
    public static readonly (string Name, string Url, string Model, bool NeedsKey)[] Providers =
    {
        // Ключи Qwen выдаёт консоль Alibaba Cloud Model Studio (qwen.ai/apiplatform).
        // Ключ привязан к региону: международный работает только с dashscope-intl,
        // пекинский — только с dashscope. Поэтому два пресета.
        ("Qwen (междунар.)", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "qwen-plus", true),
        ("Qwen (Китай)",     "https://dashscope.aliyuncs.com/compatible-mode/v1",      "qwen-plus", true),
        ("OpenRouter","https://openrouter.ai/api/v1",                      "openrouter/auto",  true),
        ("OpenAI",    "https://api.openai.com/v1",                         "gpt-4o-mini",      true),
        ("Ollama",    "http://localhost:11434/v1",                         "llama3.2",         false),
        ("Custom",    "",                                                  "",                 true),
    };

    private const string SystemPrompt =
        "Ты — встроенный быстрый помощник оболочки DotShell. " +
        "Отвечай по-русски, коротко (1-6 предложений), по делу, без инструментов и кода, " +
        "если код не попросили явно.";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    private readonly List<(string Role, string Content)> _history = new();

    public IReadOnlyList<(string Role, string Content)> History => _history;

    public void Clear() => _history.Clear();

    /// <summary>Отправить сообщение пользователя и получить ответ ассистента.</summary>
    /// <exception cref="AiChatException">ошибка, которую можно показать пользователю как есть.</exception>
    public async Task<string> SendAsync(string userText, string baseUrl, string model, string apiKey,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            throw new AiChatException("Не выбран провайдер и не задан свой адрес API (вкладка ИИ, сверху).");

        if (Providers.FirstOrDefault(p => p.Url == baseUrl).NeedsKey && string.IsNullOrWhiteSpace(apiKey))
            throw new AiChatException("Для этого провайдера нужен API-ключ (поле сверху на вкладке ИИ).");

        _history.Add(("user", userText));

        var messages = new List<object> { new { role = "system", content = SystemPrompt } };
        // Быстрый чат = короткий контекст: держим только последние 12 реплик,
        // иначе запрос распухнет и ответ будет приходить медленно.
        foreach (var (role, content) in _history.TakeLast(12))
            messages.Add(new { role, content });

        // Для локальной Ollama идём её родным API, а не OpenAI-совместимым:
        // только там можно выключить «размышления» (think=false). Иначе
        // qwen3 тратит весь лимит токенов на мысли и отдаёт пустой ответ —
        // человек видит пузырь без текста и думает, что чат сломан.
        if (IsLocalOllama(baseUrl))
            return await SendOllamaAsync(baseUrl, model, messages, cancel);

        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(model) ? "qwen-plus" : model,
            messages,
            max_tokens = 700,
        });

        var url = baseUrl.TrimEnd('/') + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var resp = await Http.SendAsync(req, cancel);
            var body = await resp.Content.ReadAsStringAsync(cancel);

            if (!resp.IsSuccessStatusCode)
            {
                _history.RemoveAt(_history.Count - 1); // не оставляем в истории то, что не ушло
                throw new AiChatException($"API ответил {(int)resp.StatusCode}: {Trim(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            _history.Add(("assistant", text));
            return text;
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            _history.RemoveAt(_history.Count - 1);
            throw new AiChatException("Таймаут: провайдер не ответил за 45 секунд.");
        }
        catch (HttpRequestException ex)
        {
            _history.RemoveAt(_history.Count - 1);
            throw new AiChatException($"Не удалось достучаться до API: {ex.Message}");
        }
    }

    private static string Trim(string s) =>
        s.Length <= 300 ? s : s[..300] + "…";

    private static bool IsLocalOllama(string baseUrl) =>
        baseUrl.Contains("11434", StringComparison.OrdinalIgnoreCase) ||
        baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Родной /api/chat у Ollama: без «размышлений», с ограничением длины
    /// ответа. Именно этот путь даёт мгновенный внятный текст на qwen3.
    /// </summary>
    private async Task<string> SendOllamaAsync(
        string baseUrl, string model, List<object> messages, CancellationToken cancel)
    {
        var root = baseUrl.TrimEnd('/');
        var v1 = root.IndexOf("/v1", StringComparison.OrdinalIgnoreCase);
        if (v1 > 0) root = root[..v1];

        var payload = JsonSerializer.Serialize(new
        {
            model = string.IsNullOrWhiteSpace(model) ? "qwen3:1.7b" : model,
            stream = false,
            think = false,
            messages,
            options = new { num_predict = 700 },
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, root + "/api/chat")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        try
        {
            using var resp = await Http.SendAsync(req, cancel);
            var body = await resp.Content.ReadAsStringAsync(cancel);

            if (!resp.IsSuccessStatusCode)
            {
                _history.RemoveAt(_history.Count - 1);
                throw new AiChatException($"Ollama ответила {(int)resp.StatusCode}: {Trim(body)}");
            }

            using var doc = JsonDocument.Parse(body);
            var text = doc.RootElement.TryGetProperty("message", out var message) &&
                       message.TryGetProperty("content", out var content)
                ? content.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                _history.RemoveAt(_history.Count - 1);

                // Самая частая причина: модель «думающая» и выбранная модель
                // не поддерживает think=false (старые сборки) — тогда весь
                // лимит уходит в размышления.
                throw new AiChatException(
                    "Модель ответила пусто. Скорее всего она «думающая» и весь лимит ушёл " +
                    "на размышления. Попробуй другую модель в списке (например qwen3:4b " +
                    "или llama3.2) — они отвечают сразу.");
            }

            _history.Add(("assistant", text));
            return text;
        }
        catch (TaskCanceledException) when (!cancel.IsCancellationRequested)
        {
            _history.RemoveAt(_history.Count - 1);
            throw new AiChatException("Таймаут: Ollama не ответила за 45 секунд.");
        }
        catch (HttpRequestException ex)
        {
            _history.RemoveAt(_history.Count - 1);
            throw new AiChatException($"Не удалось достучаться до Ollama: {ex.Message}");
        }
    }
}

/// <summary>Ошибка чата, которую можно показать пользователю как есть.</summary>
public sealed class AiChatException : Exception
{
    public AiChatException(string message) : base(message) { }
}
