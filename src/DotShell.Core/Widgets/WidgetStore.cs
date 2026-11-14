namespace DotShell.Core.Widgets;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Один виджет на рабочем столе: тип, последнее положение, размер и
/// необязательный параметр (например, путь папки для «умной папки»).
/// Нулевой размер означает «подгонять под содержимое», как было до 0.7.0.</summary>
public sealed class WidgetSpec
{
    public string Type { get; set; } = string.Empty;
    public int X { get; set; } = -32000;
    public int Y { get; set; } = -32000;
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>Дополнительные данные виджета. Для типа "folder" — путь к папке.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>
/// Какие виджеты хозяин поставил на рабочий стол и куда он их оттащил.
/// Файл widgets.json рядом с настройками: правится блокнотом, переживает
/// перезапуски и переустановку.
/// </summary>
public sealed class WidgetStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public List<WidgetSpec> Items { get; } = new();

    public event Action? Changed;

    private string StorePath => Path.Combine(AppSettings.SettingsDirectory, "widgets.json");

    public WidgetStore() => Load();

    public WidgetSpec? Find(string type)
    {
        lock (_lock) return Items.FirstOrDefault(i => i.Type == type);
    }

    public bool Has(string type) => Find(type) is not null;

    /// <summary>Добавить виджет, если его ещё нет; вернуть его spec.
    /// Для виджетов, у которых может быть несколько экземпляров (например, папки),
    /// уникальность определяется парой (Type, Path).</summary>
    public WidgetSpec Add(string type, int x, int y, string path = "")
    {
        lock (_lock)
        {
            var existing = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
                ? Items.FirstOrDefault(i => i.Type == type && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))
                : Items.FirstOrDefault(i => i.Type == type);
            if (existing is not null) return existing;

            var spec = new WidgetSpec { Type = type, X = x, Y = y, Path = path ?? string.Empty };
            Items.Add(spec);
            Save();
            Changed?.Invoke();
            return spec;
        }
    }

    /// <summary>Обновить параметр Path (для папки-виджета).</summary>
    public void UpdatePath(string type, string oldPath, string newPath)
    {
        lock (_lock)
        {
            var spec = Items.FirstOrDefault(i => i.Type == type && string.Equals(i.Path, oldPath, StringComparison.OrdinalIgnoreCase));
            if (spec is null) return;
            spec.Path = newPath ?? string.Empty;
        }
        Save();
    }

    /// <summary>Убрать весь сохранённый состав виджетов.</summary>
    public void Clear()
    {
        if (Items.Count == 0) return;

        Items.Clear();
        Save();
    }

    public void Remove(string type, string path = "")
    {
        lock (_lock)
        {
            if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
                Items.RemoveAll(i => i.Type == type && string.Equals(i.Path, path ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            else
                Items.RemoveAll(i => i.Type == type);
        }
        Save();
        Changed?.Invoke();
    }

    public void UpdatePosition(string type, int x, int y, string path = "")
    {
        lock (_lock)
        {
            var spec = FindSpec(type, path);
            if (spec is null) return;
            spec.X = x;
            spec.Y = y;
        }
        Save();
    }

    /// <summary>Виджет потянули за угол: запоминаем размер.</summary>
    public void UpdateSize(string type, int width, int height, string path = "")
    {
        lock (_lock)
        {
            var spec = FindSpec(type, path);
            if (spec is null) return;
            spec.W = width;
            spec.H = height;
        }
        Save();
    }

    private WidgetSpec? FindSpec(string type, string path)
    {
        if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
            return Items.FirstOrDefault(i => i.Type == type && string.Equals(i.Path, path ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return Items.FirstOrDefault(i => i.Type == type);
    }

    // ------------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var list = JsonSerializer.Deserialize<List<WidgetSpec>>(File.ReadAllText(StorePath));
            if (list is null) return;
            lock (_lock)
            {
                foreach (var i in list.Where(i => !string.IsNullOrWhiteSpace(i.Type))) Items.Add(i);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            List<WidgetSpec> snapshot;
            lock (_lock) snapshot = Items.ToList();
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }
}
