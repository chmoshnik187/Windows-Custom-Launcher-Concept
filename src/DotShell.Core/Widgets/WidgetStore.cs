namespace DotShell.Core.Widgets;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Один виджет на рабочем столе: тип, последнее положение, размер и
/// необязательный параметр (например, путь папки для «умной папки»).
/// Нулевой размер означает «подгонять под содержимое», как было до 0.7.0.</summary>
/// <summary>Конфигурация живой подборки/упрощённых правил для виджета «Умная папка» (MVP).</summary>
public sealed class FolderCollectionSpec
{
    public bool Enabled { get; set; }
    public string Preset { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public bool Recursive { get; set; }
    /// <summary>Классы: image, video, audio, doc, pdf, archive или расширения (".png"). Пусто — все.</summary>
    public List<string> Kinds { get; set; } = new();
    public string NameContains { get; set; } = string.Empty;
    /// <summary>Изменён за последние N дней. 0 — без фильтра.</summary>
    public int WithinDays { get; set; }
    /// <summary>Минимальный размер в МБ. 0 — без фильтра.</summary>
    public int MinSizeMb { get; set; }
    public int Limit { get; set; } = 50;

    public FolderCollectionSpec Clone() => new()
    {
        Enabled = Enabled,
        Preset = Preset ?? string.Empty,
        Scope = Scope ?? string.Empty,
        Recursive = Recursive,
        Kinds = Kinds?.ToList() ?? new List<string>(),
        NameContains = NameContains ?? string.Empty,
        WithinDays = WithinDays,
        MinSizeMb = MinSizeMb,
        Limit = Limit > 0 ? Limit : 50,
    };
}

/// <summary>Именованный набор правил (портал) для умной папки, сохраняемый отдельно от виджета.</summary>
public sealed class FolderPortal
{
    public string Name { get; set; } = string.Empty;
    public FolderCollectionSpec Spec { get; set; } = new();
}

/// <summary>Хранилище именованных порталов/наборов правил умной папки (глобально для пользователя).</summary>
public static class FolderPortalStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static string StorePath => Path.Combine(AppSettings.SettingsDirectory, "folder-portals.json");

    public static List<FolderPortal> Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new List<FolderPortal>();
            var list = JsonSerializer.Deserialize<List<FolderPortal>>(File.ReadAllText(StorePath));
            return list?.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name)).ToList() ?? new List<FolderPortal>();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FolderPortalStore.Load failed: {ex.Message}");
            return new List<FolderPortal>();
        }
    }

    public static void Save(IReadOnlyCollection<FolderPortal> portals)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(portals, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FolderPortalStore.Save failed: {ex.Message}");
        }
    }
}

public sealed class WidgetSpec
{
    public string Type { get; set; } = string.Empty;
    public int X { get; set; } = -32000;
    public int Y { get; set; } = -32000;
    public int W { get; set; }
    public int H { get; set; }

    /// <summary>Дополнительные данные виджета. Для типа "folder" — путь к папке.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Закреплённые элементы (полные пути) внутри виджета-папки.</summary>
    public List<string> PinnedPaths { get; set; } = new();

    /// <summary>Живая подборка/правила (опционально, для folder).</summary>
    public FolderCollectionSpec? Collection { get; set; }

    /// <summary>D1: идентификатор монитора раскладки (device/name), пусто — legacy/не задан.</summary>
    public string LayoutMonitor { get; set; } = string.Empty;

    /// <summary>D1: координата X относительно рабочей области монитора LayoutMonitor. int.MinValue — не задано.</summary>
    public int LayoutX { get; set; } = int.MinValue;

    /// <summary>D1: координата Y относительно рабочей области монитора LayoutMonitor. int.MinValue — не задано.</summary>
    public int LayoutY { get; set; } = int.MinValue;
}

/// <summary>
/// Какие виджеты хозяин поставил на рабочий стол и куда он их оттащил.
/// Файл widgets.json рядом с настройками: правится блокнотом, переживает
/// перезапуски и переустановку.
/// </summary>
public sealed class WidgetStore
{
    private sealed class WidgetStoreFile
    {
        public int Version { get; set; } = CurrentVersion;
        public List<WidgetSpec> Items { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();
    public const int CurrentVersion = 2;

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

    /// <summary>
    /// D1: обновить layout с учётом монитора (координаты в DIP экрана; monitor-relative вычисляется снаружи при необходимости).
    /// Если per-monitor данные не заданы, сохраняет только абсолютные X/Y.
    /// </summary>
    public void UpdateLayout(string type, int x, int y, string layoutMonitor = "", int layoutX = int.MinValue, int layoutY = int.MinValue, string path = "")
    {
        lock (_lock)
        {
            var spec = FindSpec(type, path);
            if (spec is null) return;
            spec.X = x;
            spec.Y = y;
            spec.LayoutMonitor = layoutMonitor ?? string.Empty;
            spec.LayoutX = layoutX;
            spec.LayoutY = layoutY;
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

    /// <summary>Сохраняет закреплённые элементы виджета-папки.</summary>
    public void UpdatePinnedPaths(string type, string path, IReadOnlyCollection<string> pinnedPaths)
    {
        lock (_lock)
        {
            var spec = FindSpec(type, path);
            if (spec is null) return;
            spec.PinnedPaths = pinnedPaths?
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }
        Save();
    }

    /// <summary>Сохранить конфигурацию живой подборки folder-виджета.</summary>
    public void UpdateCollection(string type, string path, FolderCollectionSpec? collection)
    {
        lock (_lock)
        {
            var spec = FindSpec(type, path);
            if (spec is null) return;
            spec.Collection = collection;
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
            var json = File.ReadAllText(StorePath).TrimStart();
            List<WidgetSpec>? list = null;

            if (json.StartsWith("[", StringComparison.Ordinal))
            {
                // Legacy format (v1): plain array
                list = JsonSerializer.Deserialize<List<WidgetSpec>>(json);
            }
            else
            {
                // v2+: wrapper object
                var file = JsonSerializer.Deserialize<WidgetStoreFile>(json, JsonOpts);
                if (file?.Items is not null)
                {
                    list = file.Items;
                    // Future migrations: if (file.Version < CurrentVersion) { ... }
                }
            }

            if (list is null) return;
            lock (_lock)
            {
                foreach (var i in list.Where(i => !string.IsNullOrWhiteSpace(i.Type)))
                {
                    i.Path ??= string.Empty;
                    i.PinnedPaths ??= new List<string>();
                    i.LayoutMonitor ??= string.Empty;
                    Items.Add(i);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WidgetStore.Load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            List<WidgetSpec> snapshot;
            lock (_lock) snapshot = Items.Select(CloneSpec).ToList();
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var file = new WidgetStoreFile { Version = CurrentVersion, Items = snapshot };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(file, JsonOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WidgetStore.Save failed: {ex.Message}");
        }
    }

    internal static WidgetSpec CloneSpec(WidgetSpec s) => new()
    {
        Type = s.Type ?? string.Empty,
        X = s.X,
        Y = s.Y,
        W = s.W,
        H = s.H,
        Path = s.Path ?? string.Empty,
        PinnedPaths = s.PinnedPaths?.ToList() ?? new List<string>(),
        Collection = s.Collection?.Clone(),
        LayoutMonitor = s.LayoutMonitor ?? string.Empty,
        LayoutX = s.LayoutX,
        LayoutY = s.LayoutY,
    };

    /// <summary>D1.1: снимок текущей раскладки для профилей.</summary>
    public List<WidgetSpec> Snapshot()
    {
        lock (_lock) return Items.Select(CloneSpec).ToList();
    }

    /// <summary>D1.1: заменить текущий набор виджетов (применение профиля). Событие Changed не вызывается — вызывающий управляет UI.</summary>
    public void ReplaceAll(IEnumerable<WidgetSpec> specs)
    {
        lock (_lock)
        {
            Items.Clear();
            foreach (var s in specs ?? Array.Empty<WidgetSpec>())
            {
                if (s is null || string.IsNullOrWhiteSpace(s.Type)) continue;
                var c = CloneSpec(s);
                c.Path ??= string.Empty;
                c.PinnedPaths ??= new List<string>();
                c.LayoutMonitor ??= string.Empty;
                Items.Add(c);
            }
        }
        Save();
    }
}
