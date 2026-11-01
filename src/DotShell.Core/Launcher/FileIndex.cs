namespace DotShell.Core.Launcher;

using DotShell.Core.Settings;

/// <summary>
/// Индекс файлов для умного поиска: рабочий стол, Документы, Загрузки.
///
/// Лаунчер умел искать только программы — «Spotlight» из него получался наполовину:
/// набрав имя файла, человек не находил ничего. Здесь то же самое, что у ярлыков, но
/// для файлов: редкий пересбор, нечёткий поиск и свежие выше.
///
/// Что сознательно не делается: не обходим весь диск. Индексируются три папки и
/// два уровня вложенности — этого хватает для повседневного «где мой файл», а полный
/// обход превращается в минуты работы и десятки тысяч записей в памяти.
/// </summary>
public sealed class FileIndex
{
    private readonly List<FileEntry> _entries = new();
    private DateTime _builtAt;
    private bool _building;

    public bool IsReady => _entries.Count > 0;

    public int Count => _entries.Count;

    private static readonly string[] SkipExtensions =
    {
        ".tmp", ".log", ".etl", ".dmp", ".part", ".crdownload", ".lock",
    };

    /// <summary>Пересобрать индекс, если он устарел (по умолчанию — раз в 10 минут).</summary>
    public void EnsureFresh(TimeSpan? maxAge = null)
    {
        if (_building) return;
        if (_entries.Count > 0 && DateTime.UtcNow - _builtAt < (maxAge ?? TimeSpan.FromMinutes(10))) return;

        _building = true;
        Task.Run(() =>
        {
            try { Rebuild(); }
            catch { }
            finally { _building = false; }
        });
    }

    private void Rebuild()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };

        var found = new List<FileEntry>();

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

            try
            {
                Walk(root, found, depth: 0, limit: 2500);
            }
            catch { }
        }

        // Свежие сверху: именно их обычно и ищут.
        found.Sort((a, b) => b.Modified.CompareTo(a.Modified));

        _entries.Clear();
        _entries.AddRange(found.Take(6000));
        _builtAt = DateTime.UtcNow;
        // Лог живёт в UI-проекте; в Core пишем в общий файл через Trace,
        // чтобы не тащить зависимость наоборот.
        System.Diagnostics.Trace.WriteLine($"file index: {_entries.Count} файлов из трёх папок");
    }

    private static void Walk(string folder, List<FileEntry> found, int depth, int limit)
    {
        if (depth > 2 || found.Count >= limit) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if (found.Count >= limit) return;

                var ext = Path.GetExtension(file);
                if (string.IsNullOrEmpty(ext) || SkipExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;

                try
                {
                    var info = new FileInfo(file);
                    if (info.Attributes.HasFlag(FileAttributes.System)) continue;

                    found.Add(new FileEntry(info.Name, info.FullName, info.DirectoryName ?? folder,
                        info.LastWriteTime, $"файл {ext.TrimStart('.').ToLowerInvariant()}"));
                }
                catch { }
            }

            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("AppData", StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(new FileEntry(name, dir, dir, Directory.GetLastWriteTime(dir), "папка"));
                Walk(dir, found, depth + 1, limit);
            }
        }
        catch { }
    }

    /// <summary>
    /// Поиск: буквы должны идти по порядку (как в остальных строковых фильтрах проекта),
    /// но имя, начинающееся с запроса, и точное совпадение весят больше.
    /// </summary>
    public List<FileEntry> Search(string query, int limit = 6)
    {
        query = (query ?? string.Empty).Trim();
        if (query.Length < 2) return new List<FileEntry>();

        var scored = new List<(FileEntry Entry, int Score)>();

        foreach (var entry in _entries)
        {
            int score = Score(entry.Name, query);
            if (score > 0) scored.Add((entry, score));
        }

        // При равном счёте выше тот, что менялся позже: список уже отсортирован.
        return scored
            .OrderByDescending(pair => pair.Score)
            .ThenByDescending(pair => pair.Entry.Modified)
            .Take(limit)
            .Select(pair => pair.Entry)
            .ToList();
    }

    private static int Score(string name, string query)
    {
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 300;

        var index = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 300;
        if (index > 0) return 200;

        // Нечётко: все символы запроса по порядку.
        int pos = 0;
        foreach (var ch in query)
        {
            pos = name.IndexOf(char.ToLowerInvariant(ch), pos);
            if (pos < 0) pos = name.IndexOf(char.ToUpperInvariant(ch), 0);
            if (pos < 0) return 0;
            pos++;
        }

        return 100;
    }
}

/// <summary>Файл или папка в результатах поиска.</summary>
public sealed record FileEntry(string Name, string Path, string Folder, DateTime Modified, string Kind);
