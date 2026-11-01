namespace DotShell.Core.Launcher;

using System.Diagnostics;

/// <summary>
/// Запущена ли программа с такой целью.
///
/// Плитка сейчас ведёт себя как кнопка «Пуск»: нажал — и непонятно, открылось ли,
/// уже работает или свёрнуто. Точка под плиткой отвечает на этот вопрос.
///
/// Проверяем по списку процессов, а не по главному окну: свёрнутое в трей
/// приложение окон не имеет, но работает — и это как раз тот случай, когда
/// индикатор нужен больше всего.
/// </summary>
public static class ProcessProbe
{
    private static readonly object Gate = new();
    private static HashSet<string> _names = new(StringComparer.OrdinalIgnoreCase);
    private static long _stamp;
    private const int CacheMs = 2000;

    /// <summary>Работает ли программа, на которую указывает плитка.</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    /// <summary>
    /// Путь процесса, чьё окно сейчас на переднем плане. null — если не вышло
    /// (системные окна, защищённые процессы).
    /// </summary>
    public static string? ForegroundTarget()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return null;

            GetWindowThreadProcessId(window, out uint pid);
            if (pid == 0) return null;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.MainModule?.FileName;
        }
        catch { return null; }
    }

    /// <summary>Совпадает ли цель ярлыка с процессом на переднем плане.</summary>
    public static bool IsForeground(string target)
    {
        var active = ForegroundTarget();
        if (string.IsNullOrEmpty(active) || string.IsNullOrEmpty(target)) return false;

        return string.Equals(
            System.IO.Path.GetFullPath(active),
            System.IO.Path.GetFullPath(target),
            StringComparison.OrdinalIgnoreCase);
    }
    public static bool IsRunning(PinnedApp app) => IsRunning(app.Target);

    public static bool IsRunning(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;

        // Папки и документы не «запускаются» — у них нет процесса.
        if (Directory.Exists(target)) return false;

        var exe = ResolveExe(target);
        if (exe is null) return false;

        var names = RunningNames();
        return names.Contains(exe);
    }

    /// <summary>Имя процесса без расширения для цели плитки (учитывает .lnk).</summary>
    private static string? ResolveExe(string target)
    {
        try
        {
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // Ярлык ведёт на настоящий exe — берём его оттуда.
                var resolved = IconCache.Extract(target, target);
                if (resolved is not null) target = resolved;
            }

            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;

            return Path.GetFileNameWithoutExtension(target);
        }
        catch { return null; }
    }

    private static HashSet<string> RunningNames()
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (now - _stamp < CacheMs && _stamp != 0) return _names;

            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try { set.Add(process.ProcessName); } catch { }
                    finally { process.Dispose(); }
                }
            }
            catch { }

            _names = set;
            _stamp = now;
            return _names;
        }
    }
}
