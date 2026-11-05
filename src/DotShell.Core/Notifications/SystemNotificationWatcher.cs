namespace DotShell.Core.Notifications;

using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

/// <summary>Одно новое уведомление, пришедшее от другой программы.</summary>
public sealed record SystemNotification(
    string AppName,
    string Text,
    string Aumid,
    uint Id,
    DateTimeOffset Created);

/// <summary>
/// Слушает системные уведомления Windows (тосты в центре уведомлений) и отдаёт
/// наружу только те, что появились после запуска.
///
/// Зачем именно этот API: `UserNotificationListener` — единственный документированный
/// способ прочитать чужие тосты. Проверено измерением на Win11 26300: без упаковки
/// (без MSIX) `RequestAccessAsync` возвращает Allowed, а `GetNotificationsAsync`
/// отдаёт имя программы, AUMID и тексты. Поэтому слушатель живёт в ядре и не тянет
/// UI-фреймворк.
///
/// Почему опрос, а не событие `NotificationChanged`: событие доступно только при
/// наличии COM-апартамента и привязки к окну, а опрос раз в 1.2 с стоит микросекунды
/// и не зависит от того, в каком потоке создан слушатель.
///
/// История из центра уведомлений НЕ показывается повторно: на старте все уже
/// существующие уведомления помечаются «виденными». Иначе после каждого перезапуска
/// остров вываливал бы всё, что накопилось за день.
/// </summary>
public sealed class SystemNotificationWatcher : IDisposable
{
    /// <summary>Свой AUMID: свои же тосты зеркалить нельзя, иначе бесконечный цикл.</summary>
    public const string OwnAumid = "DotShell.Island";

    private readonly object _gate = new();
    private readonly Dictionary<uint, DateTimeOffset> _seen = new();
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    private System.Threading.Timer? _timer;
    private UserNotificationListener? _listener;
    private DateTimeOffset _startedAt;
    private bool _polling;
    private bool _disposed;

    /// <summary>Новые уведомления. Приходят из пула потоков — вызывающий сам решает про Dispatcher.</summary>
    public event Action<SystemNotification>? Arrived;

    /// <summary>Состояние для лога/диагностики: allowed, denied, error: …</summary>
    public string Status { get; private set; } = "not started";

    /// <summary>Слушатель реально работает (доступ разрешён и опрос идёт).</summary>
    public bool Running { get; private set; }

    public void Start()
    {
        if (_disposed) return;
        _ = Task.Run(InitAsync);
    }

    private async Task InitAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            Status = access.ToString();

            // Отказ — не ошибка: пользователь мог запретить чтение уведомлений.
            // Тогда просто ничего не зеркалим, приложение работает как раньше.
            if (access != UserNotificationListenerAccessStatus.Allowed) return;

            var existing = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            lock (_gate)
            {
                foreach (var n in existing) _seen[n.Id] = n.CreationTime;
            }

            _startedAt = DateTimeOffset.Now;
            Running = true;
            _timer = new System.Threading.Timer(_ => _ = PollAsync(), null, 1000, 1200);
        }
        catch (Exception ex)
        {
            Status = "error: " + ex.Message;
        }
    }

    private async Task PollAsync()
    {
        if (_polling || _disposed || _listener is null) return;
        _polling = true;

        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var fresh = new List<SystemNotification>();

            lock (_gate)
            {
                foreach (var n in notifications)
                {
                    if (_seen.ContainsKey(n.Id)) continue;
                    _seen[n.Id] = n.CreationTime;

                    // Страховка от истории: id может быть новым, а уведомление старым
                    // (например, центр ещё не успел пересчитать id после перезапуска).
                    if (n.CreationTime < _startedAt.AddSeconds(-2)) continue;

                    var snapshot = Describe(n);
                    if (snapshot is null) continue;

                    // Одна и та же новость может прийти дважды (приложение обновило тост
                    // с новым id). Второй раз за 2 секунды не показываем.
                    var key = snapshot.Aumid + "|" + snapshot.Text;
                    if (_recent.TryGetValue(key, out var last) && (DateTimeOffset.Now - last).TotalSeconds < 2)
                        continue;
                    _recent[key] = DateTimeOffset.Now;

                    fresh.Add(snapshot);
                }

                Prune();
            }

            foreach (var item in fresh) Arrived?.Invoke(item);
        }
        catch (Exception ex)
        {
            Status = "poll error: " + ex.Message;
        }
        finally
        {
            _polling = false;
        }
    }

    /// <summary>Превратить тост в пару «кто / что», отбросив служебное и своё.</summary>
    private static SystemNotification? Describe(UserNotification n)
    {
        string aumid;
        string app;

        try { aumid = n.AppInfo.AppUserModelId ?? string.Empty; }
        catch { aumid = string.Empty; }

        try { app = n.AppInfo.DisplayInfo?.DisplayName?.Trim() ?? string.Empty; }
        catch { app = string.Empty; }

        if (string.IsNullOrEmpty(app)) app = aumid;

        // Свои уведомления не зеркалим: иначе тост DotShell, отправленный в режиме
        // «системный тост», вернулся бы в капсулу и показался дважды.
        if (aumid.Equals(OwnAumid, StringComparison.OrdinalIgnoreCase)
            || app.Equals("DotShell", StringComparison.OrdinalIgnoreCase))
            return null;

        var texts = new List<string>();
        try
        {
            var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is not null)
            {
                foreach (var element in binding.GetTextElements())
                {
                    var text = element.Text?.Trim();
                    if (!string.IsNullOrWhiteSpace(text)) texts.Add(text);
                }
            }
        }
        catch { /* чужой формат тоста — читаем, что получилось */ }

        if (texts.Count == 0) return null;

        // В шапке капсулы уже стоит имя программы, поэтому если первый текст его
        // повторяет (так делает часть приложений), он только мешает.
        if (texts.Count > 1 && texts[0].Equals(app, StringComparison.OrdinalIgnoreCase))
            texts.RemoveAt(0);

        var body = string.Join(": ", texts.Take(2));
        body = body.Replace("\r", " ").Replace("\n", " ").Trim();

        if (body.Length > 160) body = body[..157].TrimEnd() + "…";
        if (body.Length == 0) return null;

        return new SystemNotification(app, body, aumid, n.Id, n.CreationTime);
    }

    /// <summary>Память о виденном не должна расти бесконечно за сутки работы.</summary>
    private void Prune()
    {
        if (_seen.Count <= 400) return;

        foreach (var id in _seen.OrderBy(pair => pair.Value).Take(_seen.Count - 300).Select(pair => pair.Key).ToList())
            _seen.Remove(id);

        if (_recent.Count > 100)
            foreach (var key in _recent.OrderBy(pair => pair.Value).Take(_recent.Count - 50).Select(pair => pair.Key).ToList())
                _recent.Remove(key);
    }

    public void Dispose()
    {
        _disposed = true;
        Running = false;
        _timer?.Dispose();
        _timer = null;
    }
}
