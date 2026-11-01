namespace DotShell.Core.Tasks;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Одна задача в списке заметок.</summary>
public sealed class TaskItem
{
    public string Text { get; set; } = string.Empty;
    public bool Done { get; set; }
    public DateTime Created { get; set; } = DateTime.Now;
}

/// <summary>
/// Список задач для вкладки «Заметки».
///
/// Зачем отдельно от заметок: заметка — это свободный текст, а задача — это
/// единица, которую можно вычеркнуть. Держим в tasks.json, чтобы список
/// пережил перезапуск и не мешался в settings.json.
/// </summary>
public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public List<TaskItem> Items { get; } = new();

    public event Action? Changed;

    private string StorePath => Path.Combine(AppSettings.SettingsDirectory, "tasks.json");

    public TaskStore() => Load();

    public void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_lock) Items.Insert(0, new TaskItem { Text = text.Trim() });
        Save();
        Changed?.Invoke();
    }

    public void Toggle(TaskItem item)
    {
        lock (_lock)
        {
            var found = Items.FirstOrDefault(i => ReferenceEquals(i, item));
            if (found is null) return;
            found.Done = !found.Done;
        }
        Save();
        Changed?.Invoke();
    }

    public void Remove(TaskItem item)
    {
        lock (_lock) Items.RemoveAll(i => ReferenceEquals(i, item));
        Save();
        Changed?.Invoke();
    }

    /// <summary>Убрать всё вычеркнутое — «сделано, почистил».</summary>
    public int ClearDone()
    {
        int removed;
        lock (_lock)
        {
            removed = Items.RemoveAll(i => i.Done);
        }
        if (removed > 0) { Save(); Changed?.Invoke(); }
        return removed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<TaskItem>>(File.ReadAllText(StorePath), JsonOpts);
            if (list is null) return;
            lock (_lock) Items.AddRange(list);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            List<TaskItem> snapshot;
            lock (_lock) snapshot = Items.ToList();
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }
}
