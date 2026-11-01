namespace DotShell.Core.Wallpaper;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

/// <summary>Текущие обои: название, превью и папка проекта.</summary>
public sealed record WallpaperSource(string Monitor, string Title, string PreviewPath, string ProjectPath);

/// <summary>
/// Чтение того, что сейчас стоит в Wallpaper Engine.
///
/// Зачем: у владельца WE, а не Lively, и управлять WE снаружи нельзя — у него
/// нет IPC. Зато у него есть config.json с выбранными обоями по каждому монитору,
/// а рядом с сценой лежит preview (превью проекта). Из превью можно взять
/// палитру обоев без захвата экрана — это работает даже когда рабочий стол
/// закрыт окнами или игра идёт в полноэкранном режиме.
///
/// Честная граница: живой кадр DX11-сцены снаружи не снимается — PrintWindow и
/// BitBlt на окне WPEDesktopDX11Window отдают чёрный. Для настоящего живого
/// цвета нужен Windows.Graphics.Capture + D3D11, это отдельная большая задача.
/// </summary>
public static class WallpaperEngine
{
    private static readonly object Gate = new();

    private static WallpaperSource? _cached;
    private static DateTime _cachedConfigTime;
    private static DateTime _checkedAt;

    /// <summary>Почему не удалось прочитать обои (для диагностики в статусе).</summary>
    public static string? LastError { get; private set; }

    /// <summary>Запущен ли Wallpaper Engine прямо сейчас.</summary>
    public static bool IsRunning()
    {
        try
        {
            return Process.GetProcessesByName("wallpaper64").Length > 0
                || Process.GetProcessesByName("wallpaper32").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Текущие обои или null, если WE не настроен/не найден.</summary>
    public static WallpaperSource? Current()
    {
        lock (Gate)
        {
            // Конфиг не дёргаем чаще раза в 10 секунд: файл читается с диска.
            if (DateTime.Now - _checkedAt < TimeSpan.FromSeconds(10)) return _cached;
            _checkedAt = DateTime.Now;

            try
            {
                var config = FindConfig();
                if (config is null) { LastError = "config.json не найден"; _cached = null; return null; }

                var stamp = File.GetLastWriteTimeUtc(config);
                if (_cached is not null && stamp == _cachedConfigTime) return _cached;

                var parsed = Parse(config);
                if (parsed is not null) LastError = null;
                _cached = parsed;
                _cachedConfigTime = stamp;
                return parsed;
            }
            catch (Exception ex)
            {
                LastError = ex.GetType().Name + ": " + ex.Message;
                return _cached;
            }
        }
    }

    /// <summary>Путь к config.json Wallpaper Engine (ищем во всех библиотеках Steam).</summary>
    private static string? FindConfig()
    {
        var steamRoots = new List<string>();

        try
        {
            var steam = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
            if (!string.IsNullOrWhiteSpace(steam)) steamRoots.Add(steam.Replace('/', '\\'));
        }
        catch { }

        try
        {
            var install = Microsoft.Win32.Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
            if (!string.IsNullOrWhiteSpace(install)) steamRoots.Add(install.Replace('/', '\\'));
        }
        catch { }

        // Если реестр не отдал путь (или Steam ставили нестандартно) — смотрим
        // обычные места. Лучше найти конфиг, чем остаться без цвета обоев.
        foreach (var guess in new[]
                 {
                     @"C:\Program Files (x86)\Steam",
                     @"C:\Program Files\Steam",
                     @"D:\Steam",
                     @"D:\SteamLibrary",
                     @"E:\SteamLibrary",
                 })
        {
            if (Directory.Exists(guess)) steamRoots.Add(guess);
        }

        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var direct = Path.Combine(root, @"steamapps\common\wallpaper_engine\config.json");
            if (File.Exists(direct)) return direct;

            var libraries = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;

            foreach (var line in File.ReadLines(libraries))
            {
                // Строки вида: "path"		"D:\\SteamLibrary"
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase)) continue;

                var parts = trimmed.Split('"', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                var path = parts[^1].Replace("\\\\", "\\").Replace('/', '\\');
                var candidate = Path.Combine(path, @"steamapps\common\wallpaper_engine\config.json");
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>Разбор config.json: берём обои основного монитора (Monitor0).</summary>
    private static WallpaperSource? Parse(string configPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

        JsonElement wallpapers = default;
        bool found = false;

        // Структура: { "?installdirectory": ..., "<имя_пользователя>": { "general": { "wallpaperconfig": { "selectedwallpapers": {...} } } } }
        foreach (var user in doc.RootElement.EnumerateObject())
        {
            // В корне лежит ещё и "?installdirectory" строкой — TryGetProperty
            // на строке бросает InvalidOperationException, поэтому проверяем тип.
            if (user.Value.ValueKind != JsonValueKind.Object) continue;
            if (!user.Value.TryGetProperty("general", out var general)) continue;
            if (general.ValueKind != JsonValueKind.Object) continue;
            if (!general.TryGetProperty("wallpaperconfig", out var wc)) continue;
            if (wc.ValueKind != JsonValueKind.Object) continue;
            if (!wc.TryGetProperty("selectedwallpapers", out var selected)) continue;
            if (selected.ValueKind != JsonValueKind.Object || !selected.EnumerateObject().Any()) continue;

            wallpapers = selected;
            found = true;
            break;
        }

        if (!found) { LastError = "нет selectedwallpapers"; return null; }

        var monitor = "Monitor0";
        var file = string.Empty;

        if (wallpapers.TryGetProperty(monitor, out var first) && first.TryGetProperty("file", out var f))
        {
            file = f.GetString() ?? string.Empty;
        }
        else
        {
            // Монитора 0 нет — берём первый попавшийся, чтобы не остаться без цвета.
            foreach (var entry in wallpapers.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("file", out var anyFile)) continue;
                file = anyFile.GetString() ?? string.Empty;
                monitor = entry.Name;
                if (!string.IsNullOrEmpty(file)) break;
            }
        }

        if (string.IsNullOrWhiteSpace(file)) { LastError = "пустой путь обоев"; return null; }

        var projectPath = file.Replace('/', '\\');
        if (projectPath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            projectPath = Path.GetDirectoryName(projectPath) ?? projectPath;

        if (!Directory.Exists(projectPath)) { LastError = "папки проекта нет: " + projectPath; return null; }

        var title = ReadTitle(projectPath) ?? Path.GetFileName(projectPath.TrimEnd('\\'));
        var preview = FindPreview(projectPath);
        if (preview is null) LastError = "нет preview в " + projectPath;

        return new WallpaperSource(monitor, title, preview ?? string.Empty, projectPath);
    }

    /// <summary>Название сцены из project.json.</summary>
    private static string? ReadTitle(string projectPath)
    {
        try
        {
            var json = Path.Combine(projectPath, "project.json");
            if (!File.Exists(json)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                return title.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Превью проекта: jpg/png/gif рядом со сценой.</summary>
    private static string? FindPreview(string projectPath)
    {
        foreach (var name in new[] { "preview.jpg", "preview.png", "preview.gif", "preview.jpeg" })
        {
            var path = Path.Combine(projectPath, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }
}