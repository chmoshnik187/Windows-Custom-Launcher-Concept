namespace DotShell.Core.Maintenance;

using System.IO;
using DotShell.Core.Native;

/// <summary>Одна зона пользовательского мусора.</summary>
public sealed record JunkArea(string Name, string Path);

/// <summary>
/// «Мусоросжигательный завод» без админ-прав и без вранья.
///
/// Чистим ТОЛЬКО пользовательские зоны (%TEMP%, кэш миниатюр, дампы падений)
/// и корзину. Занятые файлы честно пропускаем (IOException) — никаких
/// «shred поверх файла, занятого системой»: это путь к сломанной машине.
/// Системные кэши обновлений/Prefetch не трогаем: туда нужен админ, а без него
/// виджет лишь сделал бы вид, что почистил.
/// </summary>
public static class JunkCleaner
{
    public static List<JunkArea> Areas()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<JunkArea>
        {
            new("папка TEMP", Path.GetTempPath()),
            new("миниатюры проводника", Path.Combine(local, @"Microsoft\Windows\Explorer")),
            new("дампы падений", Path.Combine(local, @"CrashDumps")),
            new("кэш проводника", Path.Combine(local, @"Microsoft\Windows\INetCache")),
        };
        return list.Where(a => Directory.Exists(a.Path)).ToList();
    }

    /// <summary>
    /// Разбивка мусора по зонам: сколько лежит в каждой. Нужна виджету, чтобы
    /// на большом размере показать, ИЗ ЧЕГО состоит «945 МБ», а не чёрный прямоугольник.
    /// </summary>
    public static List<(JunkArea Area, long Bytes)> Breakdown()
    {
        var list = new List<(JunkArea, long)>();
        foreach (var area in Areas())
            list.Add((area, DirectorySize(area.Path, skipThumbs: area.Name.Contains("миниатюры"))));
        return list;
    }
    /// <summary>Сколько байт мусора лежит по зонам (корзина отдельно).</summary>
    public static long Scan(out long recycleBytes)
    {
        long total = 0;
        foreach (var area in Areas()) total += DirectorySize(area.Path, skipThumbs: area.Name.Contains("миниатюры"));
        recycleBytes = RecycleSize();
        return total;
    }

    /// <summary>Чистит зоны; возвращает число освобождённых байт. Занятые файлы пропускает.</summary>
    public static long Clean()
    {
        long freed = 0;
        foreach (var area in Areas()) freed += CleanDirectory(area.Path, skipThumbs: area.Name.Contains("миниатюры"));
        return freed;
    }

    public static long RecycleSize()
    {
        try
        {
            var info = new Win32.SHQUERYRBINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.SHQUERYRBINFO>() };
            return Win32.SHQueryRecycleBin(null, ref info) == 0 ? info.i64Size : 0;
        }
        catch { return 0; }
    }

    /// <summary>Очистить корзину без вопросов и звуков. Возвращает освобождённые байты.</summary>
    public static long EmptyRecycleBin()
    {
        var before = RecycleSize();
        try { Win32.SHEmptyRecycleBin(IntPtr.Zero, null, Win32.SHERB_NOCONFIRMATION | Win32.SHERB_NOPROGRESSUI | Win32.SHERB_NOSOUND); }
        catch { }
        return before;
    }

    public static string Human(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1024.0 * 1024 * 1024):F1} ГБ",
        >= 1L << 20 => $"{bytes / (1024.0 * 1024):F0} МБ",
        >= 1L << 10 => $"{bytes / 1024.0:F0} КБ",
        _ => $"{bytes} Б",
    };

    // ------------------------------------------------------------------

    private static bool ThumbFile(string name) =>
        name.StartsWith("thumbcache", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("iconcache", StringComparison.OrdinalIgnoreCase);

    private static long DirectorySize(string path, bool skipThumbs)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (skipThumbs && ThumbFile(Path.GetFileName(file))) continue;
                try { size += new FileInfo(file).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    private static long CleanDirectory(string path, bool skipThumbs)
    {
        long freed = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (skipThumbs && ThumbFile(Path.GetFileName(file))) continue;
                try
                {
                    var info = new FileInfo(file);
                    var len = info.Length;
                    info.Attributes = FileAttributes.Normal;
                    info.Delete();
                    freed += len;
                }
                catch { } // файл занят или закрыт правами — честно пропускаем
            }

            // Пустые папки тоже мусор, но удаляем только действительно пустые.
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { Directory.Delete(dir, recursive: false); } catch { }
            }
        }
        catch { }
        return freed;
    }
}

/// <summary>
/// «Светофор обновлений» по честным косвенным признакам, без админ-прав:
///   🔴 ждёт перезагрузки  — ключ RebootRequired или отложенные переименования;
///   🟡 качает/ставит      — службы wuauserv/UsoSvc в работе;
///   🟢 чисто              — иначе.
/// Мы НЕ умеем ставить обновления на паузу без админа и не притворяемся.
/// </summary>
public static class UpdateStatus
{
    public enum Light { Green, Yellow, Red }

    public static (Light Light, string Reason, string LastInstall) Get()
    {
        if (RebootPending())
            return (Light.Red, "обновления установлены — система ждёт перезагрузки", LastInstallTime());

        if (ServiceBusy("wuauserv") || ServiceBusy("UsoSvc"))
            return (Light.Yellow, "центр обновлений работает в фоне (качает или ставит)", LastInstallTime());

        return (Light.Green, "по косвенным признакам всё чисто", LastInstallTime());
    }

    private static bool RebootPending()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            if (key is not null) return true;

            using var sm = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager");
            if (sm?.GetValue("PendingFileRenameOperations") is Array { Length: > 0 }) return true;
        }
        catch { }
        return false;
    }

    private static bool ServiceBusy(string name)
    {
        IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero;
        try
        {
            scm = Win32.OpenSCManager(null, null, Win32.SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            svc = Win32.OpenService(scm, name, Win32.SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            if (!Win32.QueryServiceStatus(svc, out var status)) return false;
            return status.dwCurrentState is Win32.SERVICE_RUNNING or Win32.SERVICE_START_PENDING or Win32.SERVICE_STOP_PENDING;
        }
        catch { return false; }
        finally
        {
            if (svc != IntPtr.Zero) Win32.CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) Win32.CloseServiceHandle(scm);
        }
    }

    private static string LastInstallTime()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\Results\Install");
            var v = key?.GetValue("LastSuccessTime") as string;
            if (!string.IsNullOrWhiteSpace(v) && DateTime.TryParse(v, out var dt))
                return dt.ToString("dd.MM.yyyy HH:mm");
        }
        catch { }
        return "неизвестно";
    }
}
