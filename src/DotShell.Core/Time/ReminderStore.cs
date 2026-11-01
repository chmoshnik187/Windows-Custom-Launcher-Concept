namespace DotShell.Core.Time;

using System.Text.Json;
using System.Text.RegularExpressions;
using DotShell.Core.Settings;

/// <summary>Напоминание: текст, время срабатывания, ежедневно ли.</summary>
public sealed class Reminder
{
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset FireAt { get; set; }
    public bool Daily { get; set; }

    /// <summary>Человеческая подпись для списка: «каждый день в 23:00» или «12 мая 18:30».</summary>
    public string WhenLabel =>
        (Daily ? "каждый день в " : "") + FireAt.ToString(Daily ? "HH:mm" : "d MMM HH:mm");
}

/// <summary>
/// Разбор напоминаний одной фразой, по-русски и без церемоний:
///   «таблетки каждый день в 23:00»  → ежедневно в 23:00
///   «в 18:30 созвон»                → один раз в 18:30 (сегодня, а если прошло — завтра)
///   «через 20 минут чай»            → один раз через 20 минут
///
/// Никакого NLP: часы, «каждый день», «через N минут/часов» — этого хватает
/// на 95% бытовых напоминаний, а сложное пусть делает календарь.
/// </summary>
public static class ReminderParser
{
    private static readonly Regex InSpan = new(
        @"через\s+(\d+)\s*(секунд\w*|минут\w*|час\w*)", RegexOptions.IgnoreCase);
    private static readonly Regex Clock = new(
        @"\b(\d{1,2})[:.](\d{2})\b", RegexOptions.IgnoreCase);
    private static readonly Regex Daily = new(
        @"кажд\w*\s+день|ежедневно", RegexOptions.IgnoreCase);
    private static readonly Regex ServiceWords = new(
        @"\s*(кажд\w*\s+день|ежедневно|через\s+\d+\s*(?:секунд\w*|минут\w*|час\w*)|в\s+\d{1,2}[:.]\d{2})\s*",
        RegexOptions.IgnoreCase);

    public static Reminder? Parse(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return null;

        var text = phrase.Trim();
        var now = DateTimeOffset.Now;

        // «через N минут/часов/секунд»
        var span = InSpan.Match(text);
        if (span.Success)
        {
            int n = int.Parse(span.Groups[1].Value);
            var unit = span.Groups[2].Value;
            var delta = unit.StartsWith("сек", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromSeconds(n)
                : unit.StartsWith("мин", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromMinutes(n)
                : TimeSpan.FromHours(n);
            return new Reminder { Text = Clean(text), FireAt = now + delta, Daily = false };
        }

        // Часы HH:MM
        var clock = Clock.Match(text);
        if (clock.Success)
        {
            int h = int.Parse(clock.Groups[1].Value);
            int m = int.Parse(clock.Groups[2].Value);
            if (h > 23 || m > 59) return null;

            var fire = new DateTimeOffset(now.Date.Add(new TimeSpan(h, m, 0)), now.Offset);
            if (fire <= now) fire = fire.AddDays(1); // «в 9:00» вечером означает завтра утром

            return new Reminder
            {
                Text = Clean(text),
                FireAt = fire,
                Daily = Daily.IsMatch(text),
            };
        }

        return null;
    }

    /// <summary>Убрать из текста служебную часть времени — останется суть напоминания.</summary>
    private static string Clean(string text) =>
        ServiceWords.Replace(text, " ").Trim();
}

/// <summary>
/// Хранилище напоминаний + тикер срабатываний. Ежедневные после срабатывания
/// сами переносятся на завтра: «каждый день» должно означать каждый день.
/// </summary>
public sealed class ReminderStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly string _path;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();

    public List<Reminder> Items { get; } = new();

    /// <summary>Сработало напоминание: UI показывает тост и пишет в статус.</summary>
    public event Action<Reminder>? Fired;

    public event Action? Changed;

    public ReminderStore()
    {
        _path = Path.Combine(AppSettings.SettingsDirectory, "reminders.json");
        Load();
        _timer = new System.Timers.Timer(10_000);
        _timer.Elapsed += (_, _) => Check();
        _timer.Start();
    }

    public bool Add(string phrase)
    {
        var reminder = ReminderParser.Parse(phrase);
        if (reminder is null) return false;
        lock (_lock) Items.Add(reminder);
        Save();
        Changed?.Invoke();
        return true;
    }

    public void Remove(Reminder r)
    {
        lock (_lock) Items.Remove(r);
        Save();
        Changed?.Invoke();
    }

    private void Check()
    {
        var now = DateTimeOffset.Now;
        var due = new List<Reminder>();

        lock (_lock)
        {
            foreach (var r in Items.Where(r => r.FireAt <= now).ToList())
            {
                due.Add(r);
                if (r.Daily) r.FireAt = r.FireAt.AddDays(1);
                else Items.Remove(r);
            }
            if (due.Count > 0) Save();
        }

        if (due.Count > 0)
        {
            Changed?.Invoke();
            foreach (var r in due) Fired?.Invoke(r);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(_path));
            if (list is null) return;
            lock (_lock) Items.AddRange(list);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            List<Reminder> snapshot;
            lock (_lock) snapshot = Items.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }

    public void Dispose() => _timer.Dispose();
}
