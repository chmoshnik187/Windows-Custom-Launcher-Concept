namespace DotShell.Core.Wallpaper;

using System.Runtime.InteropServices;
using System.Text.Json;
using DotShell.Core.Settings;
using Microsoft.Win32;

/// <summary>Один слот расписания: «с этого часа показывать эту картинку».</summary>
public sealed class WallpaperSlot
{
    public string Name { get; set; } = string.Empty;
    public int StartHour { get; set; }
    public string Path { get; set; } = string.Empty;

    public WallpaperSlot Clone() => new()
    {
        Name = Name,
        StartHour = StartHour,
        Path = Path,
    };
}

/// <summary>
/// Обои по времени суток: четыре слота, каждый действует от своего часа до
/// следующего. Отдельный файл, а не AppSettings: это не «настройка острова»,
/// а расписание с путями к картинкам, и его удобно править/сбрасывать отдельно.
/// </summary>
public sealed class WallpaperSchedule
{
    public bool Enabled { get; set; }

    /// <summary>Обои, которые были до первого включения — точка возврата.</summary>
    public bool OriginalCaptured { get; set; }
    public string OriginalPath { get; set; } = string.Empty;

    /// <summary>Ключ последнего применённого слота, чтобы не дёргать обои каждую минуту.</summary>
    public string LastAppliedKey { get; set; } = string.Empty;

    public List<WallpaperSlot> Slots { get; set; } = DefaultSlots();

    public static List<WallpaperSlot> DefaultSlots() => new()
    {
        new() { Name = "Утро", StartHour = 6 },
        new() { Name = "День", StartHour = 12 },
        new() { Name = "Вечер", StartHour = 18 },
        new() { Name = "Ночь", StartHour = 23 },
    };

    public WallpaperSchedule Clone() => new()
    {
        Enabled = Enabled,
        OriginalCaptured = OriginalCaptured,
        OriginalPath = OriginalPath,
        LastAppliedKey = LastAppliedKey,
        Slots = Slots.Select(s => s.Clone()).ToList(),
    };

    /// <summary>Слот, который действует в указанный момент (с учётом перехода через полночь).</summary>
    public WallpaperSlot CurrentSlot(DateTime now)
    {
        var ordered = Ordered();
        var current = ordered.LastOrDefault(s => s.StartHour <= now.Hour);
        return current ?? ordered[^1];
    }

    /// <summary>Следующая граница: когда включится другой слот.</summary>
    public WallpaperSlot NextSlot(DateTime now)
    {
        var ordered = Ordered();
        return ordered.FirstOrDefault(s => s.StartHour > now.Hour) ?? ordered[0];
    }

    public List<WallpaperSlot> Ordered() =>
        (Slots.Count > 0 ? Slots : DefaultSlots())
            .OrderBy(s => s.StartHour)
            .ToList();

    /// <summary>Ключ «этот слот уже применён»: час + путь. Смена пути перезапускает применение.</summary>
    public static string KeyFor(WallpaperSlot slot) => $"{slot.StartHour}|{slot.Path}";

    /// <summary>Проверка перед сохранением: четыре слота, уникальные часы.</summary>
    public bool Validate(out string error)
    {
        var slots = Slots;
        if (slots.Count < 2)
        {
            error = "нужно хотя бы два слота";
            return false;
        }

        var badHour = slots.FirstOrDefault(s => s.StartHour is < 0 or > 23);
        if (badHour is not null)
        {
            error = $"у слота «{badHour.Name}» час вне 00–23";
            return false;
        }

        var duplicate = slots.GroupBy(s => s.StartHour).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            error = $"два слота начинаются в {duplicate.Key:00}:00 — часы должны различаться";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>Хранение расписания в `%LOCALAPPDATA%\DotShell\wallpaper-schedule.json`.</summary>
public static class WallpaperScheduleStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // Кириллицу пишем как есть, а не \uXXXX: файл читают глазами.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static string StorePath => Path.Combine(AppSettings.SettingsDirectory, "wallpaper-schedule.json");

    public static WallpaperSchedule Load()
    {
        try
        {
            if (!File.Exists(StorePath)) return new WallpaperSchedule();
            var schedule = JsonSerializer.Deserialize<WallpaperSchedule>(File.ReadAllText(StorePath));
            if (schedule is null) return new WallpaperSchedule();
            if (schedule.Slots is null || schedule.Slots.Count == 0) schedule.Slots = WallpaperSchedule.DefaultSlots();
            return schedule;
        }
        catch (Exception ex)
        {
            LastError = $"wallpaper schedule: чтение не удалось: {ex.Message}";
            return new WallpaperSchedule();
        }
    }

    /// <summary>Последняя ошибка чтения/записи — для диагностики из UI-слоя.</summary>
    public static string LastError { get; private set; } = string.Empty;

    public static void Save(WallpaperSchedule schedule)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(schedule, JsonOpts));
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = $"wallpaper schedule: сохранение не удалось: {ex.Message}";
        }
    }
}

/// <summary>
/// Смена системных обоев через `SystemParametersInfo(SPI_SETDESKWALLPAPER)`.
/// Стиль (заполнение/по центру) не трогаем: Windows применит текущий.
/// </summary>
public static class WallpaperApi
{
    private const uint SpiSetDesktopWallpaper = 0x0014;
    private const uint SpifUpdateIniFile = 0x01;
    private const uint SpifSendChange = 0x02;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp",
    };

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, string value, uint flags);

    /// <summary>Путь текущих обоев из реестра. Пусто — сплошной цвет или стена Wallpaper Engine.</summary>
    public static string CurrentPath()
    {
        try
        {
            return (Registry.GetValue(
                @"HKEY_CURRENT_USER\Control Panel\Desktop", "WallPaper", null) as string ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Поставить обои. false — файла нет/формат не тот/Windows отказала.</summary>
    public static bool TryApply(string path, out string error)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "путь к картинке не задан";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "файл не найден";
            return false;
        }

        if (!AllowedExtensions.Contains(Path.GetExtension(path)))
        {
            error = "формат не поддерживается (нужен jpg/png/bmp/webp)";
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            if (!SystemParametersInfo(SpiSetDesktopWallpaper, 0, full, SpifUpdateIniFile | SpifSendChange))
            {
                error = "Windows не приняла файл";
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Запомнить исходные обои один раз, чтобы потом было куда вернуться.</summary>
    public static void CaptureOriginal(WallpaperSchedule schedule)
    {
        if (schedule.OriginalCaptured) return;
        schedule.OriginalPath = CurrentPath();
        schedule.OriginalCaptured = true;
    }

    /// <summary>Вернуть обои, которые были до расписания.</summary>
    public static bool TryRestoreOriginal(WallpaperSchedule schedule, out string error)
    {
        if (!schedule.OriginalCaptured || string.IsNullOrWhiteSpace(schedule.OriginalPath))
        {
            error = "исходные обои не сохранены (был сплошной цвет или Wallpaper Engine)";
            return false;
        }

        return TryApply(schedule.OriginalPath, out error);
    }
}
