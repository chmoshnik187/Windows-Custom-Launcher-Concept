namespace DotShell.Core.Shelf;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Файл, положенный на полку острова: ждёт, пока его утащат в нужное окно.</summary>
public sealed class ShelfFile
{
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    public string SizeLabel => Size switch
    {
        < 1024 => $"{Size} Б",
        < 1024 * 1024 => $"{Size / 1024.0:F0} КБ",
        < 1024L * 1024 * 1024 => $"{Size / 1024.0 / 1024.0:F1} МБ",
        _ => $"{Size / 1024.0 / 1024.0 / 1024.0:F2} ГБ",
    };
}

/// <summary>
/// Полка для файлов: бросил на остров — файл лежит и ждёт.
///
/// Зачем: классическая боль Windows — "перетащить файл из этой папки в то окно,
/// но сначала дойти до него". Полка превращает остров в промежуточный карман:
/// скинул сюда пачку, потом вытянул по одному куда нужно (настоящим
/// drag-and-drop, файл прилетает в целевое окно как обычное перетаскивание).
///
/// Список переживает перезапуск (shelf.json), несуществующие файлы при
/// загрузке молча отбрасываются.
/// </summary>
public sealed class ShelfStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private readonly string _path;
    private readonly object _lock = new();

    public List<ShelfFile> Items { get; } = new();

    public event Action? Changed;

    public ShelfStore()
    {
        _path = System.IO.Path.Combine(AppSettings.SettingsDirectory, "shelf.json");
        Load();
    }

    public void Add(IEnumerable<string> paths)
    {
        bool changed = false;
        lock (_lock)
        {
            foreach (var p in paths)
            {
                if (!File.Exists(p)) continue;
                if (Items.Any(i => i.Path == p)) continue;

                var info = new FileInfo(p);
                Items.Add(new ShelfFile
                {
                    Path = p,
                    Name = info.Name,
                    Size = info.Length,
                });
                changed = true;
            }
        }
        if (changed) { Save(); Changed?.Invoke(); }
    }

    public void Remove(ShelfFile item)
    {
        lock (_lock) Items.Remove(item);
        Save();
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_lock) Items.Clear();
        Save();
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<ShelfFile>>(File.ReadAllText(_path));
            if (list is null) return;
            lock (_lock)
            {
                foreach (var f in list.Where(f => File.Exists(f.Path))) Items.Add(f);
            }
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            List<ShelfFile> snapshot;
            lock (_lock) snapshot = Items.ToList();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }
}
