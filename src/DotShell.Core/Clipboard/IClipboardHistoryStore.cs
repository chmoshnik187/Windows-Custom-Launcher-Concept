namespace DotShell.Core.Clipboard;

/// <summary>
/// История буфера обмена.
///
/// Про "за сессию, пока компьютер не выключился": по умолчанию история живёт
/// только в памяти (ClipboardPersist = false). Но она ДЕРЖИТСЯ, пока работает процесс,
/// то есть пока ты не выключил/перезагрузил машину. Это ровно то поведение, которое ты просил.
///
/// Когда захочешь поиск по истории за неделю — включишь ClipboardPersist,
/// и записи поедут в SQLite (%LOCALAPPDATA%\DotShell\clipboard.db).
/// Интерфейс от этого не изменится, поэтому переделывать UI не придётся.
/// </summary>
public interface IClipboardHistoryStore
{
    IReadOnlyList<ClipboardItem> Items { get; }
    event Action? Changed;

    /// <summary>Добавить запись. Если такое же содержимое уже было — поднимает его наверх.</summary>
    void Add(ClipboardItem item);

    void Remove(long id);
    void SetPinned(long id, bool pinned);
    void BumpUsage(long id);
    void ClearUnpinned();

    /// <summary>Поиск: подстрока + простая нечёткость (пропуск символов), как в fzf.</summary>
    IReadOnlyList<ClipboardItem> Search(string query, int limit = 50);
}

public sealed class InMemoryClipboardHistory : IClipboardHistoryStore
{
    private readonly List<ClipboardItem> _items = new();
    private readonly object _lock = new();
    private long _nextId = 1;
    private readonly int _maxItems;
    private readonly Func<bool> _persistAllowed;

    public InMemoryClipboardHistory(int maxItems = 500, Func<bool>? persistAllowed = null)
    {
        _maxItems = maxItems <= 0 ? int.MaxValue : maxItems;
        _persistAllowed = persistAllowed ?? (() => false);
    }

    public IReadOnlyList<ClipboardItem> Items
    {
        get { lock (_lock) return _items.ToArray(); }
    }

    public event Action? Changed;

    public void Add(ClipboardItem item)
    {
        lock (_lock)
        {
            // Дедупликация: то же содержимое подряд не плодим, а поднимаем старую запись.
            var existing = _items.FirstOrDefault(i => i.Hash == item.Hash && i.Kind == item.Kind);
            if (existing is not null)
            {
                _items.Remove(existing);
                existing.CopiedAt = DateTimeOffset.Now;
                existing.UseCount++;
                _items.Insert(0, existing);
                Changed?.Invoke();
                return;
            }

            item.Id = _nextId++;
            if (string.IsNullOrEmpty(item.Hash))
                item.Hash = Hash(item.Text + "|" + item.Kind);

            _items.Insert(0, item);

            // Вытесняем самые старые НЕзакреплённые записи, если вышли за лимит.
            while (_items.Count(i => !i.Pinned) > _maxItems)
            {
                var victim = _items.LastOrDefault(i => !i.Pinned);
                if (victim is null) break;
                _items.Remove(victim);
            }
        }
        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        lock (_lock) _items.RemoveAll(i => i.Id == id);
        Changed?.Invoke();
    }

    public void SetPinned(long id, bool pinned)
    {
        lock (_lock)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it is not null) it.Pinned = pinned;
        }
        Changed?.Invoke();
    }

    public void BumpUsage(long id)
    {
        lock (_lock)
        {
            var it = _items.FirstOrDefault(i => i.Id == id);
            if (it is not null) it.UseCount++;
        }
    }

    public void ClearUnpinned()
    {
        lock (_lock) _items.RemoveAll(i => !i.Pinned);
        Changed?.Invoke();
    }

    public IReadOnlyList<ClipboardItem> Search(string query, int limit = 50)
    {
        ClipboardItem[] snapshot;
        lock (_lock) snapshot = _items.ToArray();

        if (string.IsNullOrWhiteSpace(query))
            return snapshot.Take(limit).ToArray();

        return snapshot
            .Select(i => new { Item = i, Score = FuzzyScore(query, i.Text) })
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Item.CopiedAt)
            .Take(limit)
            .Select(x => x.Item)
            .ToArray();
    }

    /// <summary>
    /// Нечёткий поиск: символы запроса должны встречаться в тексте по порядку,
    /// но не обязательно подряд. Чем плотнее совпадение и чем ближе к началу — тем выше балл.
    /// Тот же принцип, что в fzf / Flow Launcher, только в 20 строках.
    /// </summary>
    public static int FuzzyScore(string query, string text)
    {
        if (string.IsNullOrEmpty(query)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;

        var q = query.ToLowerInvariant();
        var t = text.ToLowerInvariant();

        int exact = t.IndexOf(q, StringComparison.Ordinal);
        if (exact >= 0) return 10000 - Math.Min(exact, 999); // точное совпадение всегда в топе

        int qi = 0, score = 0, gap = 0, lastMatch = -1;
        for (int ti = 0; ti < t.Length && qi < q.Length; ti++)
        {
            if (t[ti] == q[qi])
            {
                qi++;
                score += 10;
                if (lastMatch >= 0) gap = ti - lastMatch - 1;
                score -= gap * 2;              // штраф за "размазанность"
                if (ti == 0 || !char.IsLetterOrDigit(t[ti - 1])) score += 6; // бонус за начало слова
                lastMatch = ti;
            }
        }
        return qi == q.Length ? Math.Max(1, score) : 0;
    }

    private static string Hash(string s)
    {
        // SHA не нужен, нам лишь бы отличать записи. Берём стабильный FNV-1a.
        unchecked
        {
            ulong h = 14695981039346656037UL;
            foreach (var ch in s) { h ^= ch; h *= 1099511628211UL; }
            return h.ToString("X16");
        }
    }
}
