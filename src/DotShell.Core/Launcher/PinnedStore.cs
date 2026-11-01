namespace DotShell.Core.Launcher;

using System.Security.Cryptography;
using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Одна закреплённая программа.</summary>
public sealed class PinnedApp
{
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;

    /// <summary>Путь к ярлыку (.lnk), если программа пришла из индекса меню «Пуск».</summary>
    public string Shortcut { get; set; } = string.Empty;

    /// <summary>Сочетание для запуска, например «Ctrl+Alt+1». Пусто — без клавиши.</summary>
    public string Hotkey { get; set; } = string.Empty;

    /// <summary>Кеш иконки: png рядом с настройками, чтобы не дёргать exe каждый старт.</summary>
    public string IconPath { get; set; } = string.Empty;

    /// <summary>
    /// Программа ещё на месте. Раньше отсутствующие записи просто выкидывались
    /// при загрузке — человек не понимал, куда делась плитка. Теперь запись
    /// остаётся, но помечается: её видно, она не запускается, и понятно почему.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Exists => File.Exists(Target) || Directory.Exists(Target);
}

/// <summary>
/// Группа программ — «полка» в стиле Nothing.
///
/// Владелец просил не плоский список, а группировки, и не как в проводнике:
/// поэтому у группы есть имя, свой значок и свой акцент, а внутри — плитки.
/// </summary>
public sealed class PinnedGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "ГРУППА";

    /// <summary>Иконка группы (Segoe Fluent Icons), пусто — точка-заглушка.</summary>
    public string Glyph { get; set; } = string.Empty;

    /// <summary>Свой цвет группы (hex). Пусто — акцент темы.</summary>
    public string Color { get; set; } = string.Empty;

    public List<PinnedApp> Items { get; set; } = new();
}

/// <summary>
/// Панель «мои программы» на главной странице острова, теперь с группами.
///
/// Владелец хотел «лаунчер, который переделает рабочий стол»: первый шаг —
/// перенести любимые программы ВНУТРЬ острова, чтобы рабочий стол можно было
/// очистить. Второй шаг — разложить их по группам, как полки у Nothing.
/// Список живёт в pinned.json и переживает перезапуски; старый плоский формат
/// (массив программ) при первом запуске сам превращается в одну группу.
/// </summary>
public sealed class PinnedStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public List<PinnedGroup> Groups { get; } = new();

    /// <summary>Плоский список всех программ — для хоткеев и поиска.</summary>
    public IEnumerable<PinnedApp> Items
    {
        get
        {
            lock (_lock) return Groups.SelectMany(g => g.Items).ToList();
        }
    }

    public event Action? Changed;

    private string StorePath => Path.Combine(AppSettings.SettingsDirectory, "pinned.json");

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _reloadTimer;
    private bool _saving;

    /// <summary>
    /// Следить за pinned.json: правишь файл руками — остров перечитывает его
    /// без перезапуска. Полезно, когда группу удобнее поправить в редакторе,
    /// чем таскать плитки мышью.
    /// </summary>
    public void StartWatching()
    {
        if (_watcher is not null) return;

        try
        {
            var dir = AppSettings.SettingsDirectory;
            if (!Directory.Exists(dir)) return;

            _reloadTimer = new System.Timers.Timer(400) { AutoReset = false };
            _reloadTimer.Elapsed += (_, _) => ReloadFromDisk();

            _watcher = new FileSystemWatcher(dir, "pinned.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => { if (!_saving) _reloadTimer?.Start(); };
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* наблюдение не критично: без него просто нет хот-релоада */ }
    }

    private void ReloadFromDisk()
    {
        if (_saving) return;
        try
        {
            lock (_lock) Groups.Clear();
            Load();
            Changed?.Invoke();
        }
        catch { }
    }

    public PinnedStore() => Load();

    // ------------------------------------------------------------------
    //  Группы
    // ------------------------------------------------------------------

    public PinnedGroup? FindGroup(string? groupId)
    {
        lock (_lock) return Groups.FirstOrDefault(g => g.Id == groupId);
    }

    /// <summary>Создать группу. Пустое имя заменяется на «ГРУППА N».</summary>
    public PinnedGroup AddGroup(string name, string glyph = "")
    {
        PinnedGroup group;
        lock (_lock)
        {
            group = new PinnedGroup
            {
                Name = string.IsNullOrWhiteSpace(name) ? $"ГРУППА {Groups.Count + 1}" : name.Trim().ToUpperInvariant(),
                Glyph = glyph,
            };
            Groups.Add(group);
        }
        Save();
        Changed?.Invoke();
        return group;
    }

    /// <summary>Задать имя, значок и цвет группы.</summary>
    public void SetLook(string groupId, string? name = null, string? glyph = null, string? color = null)
    {
        lock (_lock)
        {
            var group = Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            if (!string.IsNullOrWhiteSpace(name)) group.Name = name.Trim().ToUpperInvariant();
            if (glyph is not null) group.Glyph = glyph;
            if (color is not null) group.Color = color;
        }
        Save();
        Changed?.Invoke();
    }

    public void RenameGroup(string groupId, string name)
    {
        lock (_lock)
        {
            var group = Groups.FirstOrDefault(g => g.Id == groupId);
            if (group is null) return;
            group.Name = string.IsNullOrWhiteSpace(name) ? group.Name : name.Trim().ToUpperInvariant();
        }
        Save();
        Changed?.Invoke();
    }

    public void RemoveGroup(string groupId)
    {
        lock (_lock) Groups.RemoveAll(g => g.Id == groupId);
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------
    //  Программы
    // ------------------------------------------------------------------

    public bool Contains(string target) =>
        Items.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase));

    /// <summary>Добавить программу в группу (по умолчанию — в первую).</summary>
    public void Add(string name, string target, string shortcut = "", string? groupId = null)
    {
        if (string.IsNullOrWhiteSpace(target)) return;

        lock (_lock)
        {
            if (Groups.Count == 0) Groups.Add(new PinnedGroup { Name = "МОИ ПРОГРАММЫ" });
            if (Groups.SelectMany(g => g.Items).Any(p =>
                    string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase))) return;

            var group = Groups.FirstOrDefault(g => g.Id == groupId) ?? Groups[0];
            group.Items.Add(new PinnedApp
            {
                Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(target) : name,
                Target = target,
                Shortcut = shortcut,
                IconPath = IconCache.Extract(shortcut, target),
            });
        }

        Save();
        Changed?.Invoke();
    }

    /// <summary>Назначить или снять сочетание у программы. Пустая строка — снять.</summary>
    public void SetHotkey(string target, string hotkey)
    {
        lock (_lock)
        {
            var item = Groups.SelectMany(g => g.Items)
                .FirstOrDefault(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            item.Hotkey = hotkey ?? string.Empty;
        }
        Save();
        Changed?.Invoke();
    }

    public void Remove(PinnedApp app)
    {
        lock (_lock)
            foreach (var group in Groups)
                group.Items.RemoveAll(i => ReferenceEquals(i, app) ||
                    string.Equals(i.Target, app.Target, StringComparison.OrdinalIgnoreCase));

        Save();
        Changed?.Invoke();
    }

    /// <summary>Переложить программу в другую группу (перетаскиванием плитки).</summary>
    public void MoveToGroup(PinnedApp app, string groupId)
    {
        lock (_lock)
        {
            var target = Groups.FirstOrDefault(g => g.Id == groupId);
            if (target is null) return;

            foreach (var group in Groups)
            {
                var item = group.Items.FirstOrDefault(i =>
                    string.Equals(i.Target, app.Target, StringComparison.OrdinalIgnoreCase));
                if (item is null || ReferenceEquals(group, target)) continue;

                group.Items.Remove(item);
                target.Items.Add(item);
                break;
            }
        }
        Save();
        Changed?.Invoke();
    }

    // ------------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var raw = File.ReadAllText(StorePath);
            if (string.IsNullOrWhiteSpace(raw)) return;

            lock (_lock)
            {
                // Новый формат: { "version": 2, "groups": [...] }
                if (raw.TrimStart().StartsWith('{'))
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("groups", out var groups) &&
                        groups.ValueKind == JsonValueKind.Array)
                    {
                        var parsed = JsonSerializer.Deserialize<List<PinnedGroup>>(groups.GetRawText(), JsonOpts);
                        if (parsed is not null) Groups.AddRange(parsed);
                    }
                }
                else
                {
                    // Старый формат: плоский массив программ — заворачиваем в группу.
                    var flat = JsonSerializer.Deserialize<List<PinnedApp>>(raw, JsonOpts);
                    if (flat is { Count: > 0 })
                        Groups.Add(new PinnedGroup { Name = "МОИ ПРОГРАММЫ", Items = flat });
                }

                // Отсутствующие программы НЕ выкидываем: плитка остаётся и
                // помечается «не найдена» (PinnedApp.Exists). Раньше они тихо
                // исчезали при перезапуске — это выглядело как потеря данных.
            }
        }
        catch { } // битый файл не повод не запускаться
    }

    private void Save()
    {
        try
        {
            List<PinnedGroup> snapshot;
            lock (_lock) snapshot = Groups.ToList();
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var payload = new { version = 2, groups = snapshot };
            // Своё же сохранение не должно выглядеть как «файл изменили снаружи».
            _saving = true;
            try { File.WriteAllText(StorePath, JsonSerializer.Serialize(payload, JsonOpts)); }
            finally { _reloadTimer?.Stop(); _saving = false; }
        }
        catch { }
    }
}

/// <summary>
/// Иконки закреплённых плиток: извлекаем из exe/ярлыка один раз при закреплении
/// и складываем png в %LOCALAPPDATA%\\DotShell\\icons. Дёргать ExtractAssociatedIcon
/// на каждом старте — медленно, а png ещё и переживает переезд программы.
/// </summary>
public static class IconCache
{
    private static readonly object Lock = new();

    public static string IconsDirectory => Path.Combine(AppSettings.SettingsDirectory, "icons");

        /// <summary>
    /// Монохромная версия иконки — как в Nothing: один цвет, форма читается
    /// силуэтом. Кешируется рядом (`&lt;hash&gt;-mono.png`), чтобы не пересчитывать
    /// при каждом открытии окна.
    ///
    /// Логика: если у иконки есть прозрачность — красим силуэт в тихий белый,
    /// сохраняя альфу (так выглядят почти все ярлыки Windows). Если картинка
    /// непрозрачная (фото-иконка) — переводим в градации того же тона.
    /// </summary>
    /// <summary>
    /// Чёрно-белая версия иконки — «иконки остаются своими, но в одном стиле».
    ///
    /// Именно градации серого, а не силуэт: попытка сделать Nothing-силуэты
    /// превращала ярлыки в одинаковые белые капли, потому что у .lnk форма
    /// лежит в цвете, а не в альфе. Серое сохраняет узнаваемость.
    ///
    /// Контраст подтягивается: на чёрном фоне тёмные иконки иначе пропадают.
    /// </summary>
    public static string Monochrome(string iconPath, byte tintR = 0xEC, byte tintG = 0xEC, byte tintB = 0xEE)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath)) return string.Empty;

        var target = Path.Combine(IconsDirectory,
            Path.GetFileNameWithoutExtension(iconPath) + "-gray.png");
        if (File.Exists(target)) return target;

        lock (Lock)
        {
            try
            {
                using var source = new System.Drawing.Bitmap(iconPath);
                int w = source.Width, h = source.Height;
                using var result = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                double tintLum = (0.299 * tintR + 0.587 * tintG + 0.114 * tintB);

                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        var px = source.GetPixel(x, y);
                        if (px.A < 8) { result.SetPixel(x, y, System.Drawing.Color.Transparent); continue; }

                        double lum = (0.299 * px.R + 0.587 * px.G + 0.114 * px.B) / 255.0;

                        // Мягкий подъём контраста: тени не проваливаются в чёрный.
                        double shaped = Math.Pow(Math.Clamp(lum, 0, 1), 0.72);
                        shaped = 0.18 + 0.92 * shaped;      // нижняя граница, чтобы не слилось с фоном
                        shaped = Math.Clamp(shaped, 0, 1.15);

                        int level = (int)Math.Clamp(shaped * tintLum, 0, 255);
                        result.SetPixel(x, y, System.Drawing.Color.FromArgb(px.A, level, level, level));
                    }

                Directory.CreateDirectory(IconsDirectory);
                result.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                return target;
            }
            catch { return string.Empty; }
        }
    }
    /// <summary>Возвращает путь к png (пустую строку, если иконку достать не удалось).</summary>
    public static string Extract(string shortcut, string target)
    {
        // Берём иконку у самой программы, а не у ярлыка: у .lnk сверху
        // нарисована стрелка-оверлей, и она попадала в плитку как белый квадратик.
        var source = File.Exists(target) ? target
            : !string.IsNullOrWhiteSpace(shortcut) && File.Exists(shortcut) ? shortcut
            : target;
        var hash = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(source.ToLowerInvariant())))[..12];
        var path = Path.Combine(IconsDirectory, hash + ".png");

        lock (Lock)
        {
            try
            {
                if (File.Exists(path)) return path;
                using var icon = System.Drawing.Icon.ExtractAssociatedIcon(source);
                if (icon is null) return string.Empty;
                using var bitmap = icon.ToBitmap();
                Directory.CreateDirectory(IconsDirectory);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                return path;
            }
            catch { return string.Empty; }
        }
    }
}