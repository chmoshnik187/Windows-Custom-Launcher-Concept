namespace DotShell.Core.Launcher;

using System.Runtime.InteropServices;
using System.Text.Json;
using DotShell.Core.Clipboard; // FuzzyScore — тот же нечёткий поиск, что и в буфере
using DotShell.Core.Settings;

/// <summary>Одно приложение из индекса: имя, куда запускает, ярлык, частота использования.</summary>
public sealed class AppEntry
{
    public string Name { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Shortcut { get; set; } = string.Empty;
    public int Uses { get; set; }
}

/// <summary>
/// Индекс приложений для лаунчера: ярлыки из меню "Пуск" (пользовательского
/// и общего), разрешённые в целевые .exe.
///
/// Почему ярлыки, а не реестр Uninstall: ярлыки — это ровно то, что человек
/// считает "своими программами"; в реестре половина записей — сервисы и остатки
/// удалённого. Плюс у ярлыка сразу есть красивое имя и иконка.
///
/// Разрешение .lnk делаем через WScript.Shell на отдельном STA-потоке:
/// это COM-объект с apartment-моделью, и дёргать его из пула потоков —
/// просить случайные падения. Индекс кешируется в apps.json и переживает
/// перезапуски; частота использования копится там же (частые — выше в списке).
/// </summary>
public sealed class AppIndex
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _cachePath;
    private readonly object _lock = new();

    public List<AppEntry> Entries { get; } = new();

    public bool IsIndexing { get; private set; }

    public event Action? Changed;

    public AppIndex()
    {
        _cachePath = Path.Combine(AppSettings.SettingsDirectory, "apps.json");
        Load();
    }

    /// <summary>Индекс ленивый: первое открытие лаунчера строит его в фоне.</summary>
    public void EnsureReady()
    {
        if (Entries.Count == 0 && !IsIndexing) _ = RebuildAsync();
    }

    public async Task RebuildAsync()
    {
        IsIndexing = true;
        Changed?.Invoke();

        var scanned = await ScanOnStaThreadAsync();

        lock (_lock)
        {
            var oldUses = Entries.ToDictionary(e => e.Target, e => e.Uses);
            Entries.Clear();
            foreach (var e in scanned)
            {
                if (oldUses.TryGetValue(e.Target, out var uses)) e.Uses = uses;
                Entries.Add(e);
            }
            Entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }

        Save();
        IsIndexing = false;
        Changed?.Invoke();
    }

    /// <summary>Нечёткий поиск; пустой запрос = частые и по алфавиту.</summary>
    public List<AppEntry> Search(string query, int limit = 8)
    {
        List<AppEntry> pool;
        lock (_lock) pool = Entries.ToList();

        if (string.IsNullOrWhiteSpace(query))
            return pool.OrderByDescending(e => e.Uses)
                       .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                       .Take(limit).ToList();

        return pool
            .Select(e => (Entry: e, Score: InMemoryClipboardHistory.FuzzyScore(query, e.Name)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.Uses)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }

    public void Bump(AppEntry entry)
    {
        entry.Uses++;
        Save();
    }

    // ------------------------------------------------------------------

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var list = JsonSerializer.Deserialize<List<AppEntry>>(File.ReadAllText(_cachePath));
            if (list is null) return;
            lock (_lock)
            {
                foreach (var e in list.Where(e => File.Exists(e.Target))) Entries.Add(e);
            }
        }
        catch { } // битый кеш не повод не запускаться: просто перестроим
    }

    private void Save()
    {
        try
        {
            List<AppEntry> snapshot;
            lock (_lock) snapshot = Entries.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(snapshot, JsonOpts));
        }
        catch { }
    }

    private static Task<List<AppEntry>> ScanOnStaThreadAsync()
    {
        var tcs = new TaskCompletionSource<List<AppEntry>>();
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(Scan()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        thread.IsBackground = true;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static List<AppEntry> Scan()
    {
        var dirs = new List<string>();
        void AddDir(string? d) { if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) dirs.Add(d); }

        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"));
        AddDir(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Microsoft\Windows\Start Menu\Programs"));

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        object? shell = shellType is not null ? Activator.CreateInstance(shellType) : null;

        var byTarget = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            IEnumerable<string> lnks;
            try { lnks = Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var lnk in lnks)
            {
                try
                {
                    var target = ResolveTarget(shell, lnk);
                    if (string.IsNullOrEmpty(target) ||
                        !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(target))
                        continue;

                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (ContainsAny(name, "uninstall", "unins", "setup", "redist", "feedback", "report"))
                        continue;

                    // Один exe может иметь несколько ярлыков: оставляем имя покороче —
                    // оно обычно и есть "человеческое" название программы.
                    if (byTarget.TryGetValue(target, out var existing))
                    {
                        if (name.Length < existing.Name.Length) existing.Name = name;
                        continue;
                    }

                    byTarget[target] = new AppEntry
                    {
                        Name = name,
                        Target = target,
                        Shortcut = lnk,
                    };
                }
                catch { } // один кривой ярлык не должен ломать весь индекс
            }
        }

        return byTarget.Values.ToList();
    }

    private static string ResolveTarget(object? shell, string lnkPath)
    {
        if (shell is null) return string.Empty;
        // dynamic по COM-объекту: TargetPath у ярлыка, и ничего своего парсить не надо.
        dynamic shortcut = ((dynamic)shell).CreateShortcut(lnkPath);
        return (string?)shortcut.TargetPath ?? string.Empty;
    }

    private static bool ContainsAny(string s, params string[] needles)
    {
        foreach (var n in needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
