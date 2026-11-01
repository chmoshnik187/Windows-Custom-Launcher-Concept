namespace DotShell.Core.Windowing;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Приложения, которые держат значок в области уведомлений.
///
/// Устроено так: каждая программа с трей-значком создаёт скрытое окно-приёмник,
/// чтобы получать сообщения от оболочки. Классы этих окон говорящие —
/// `TgWsProxy…SystemTrayIcon`, `YandexBrowser_StatusTrayWindow`,
/// `Electron_NotifyIconHostWindow`, `Qt…TrayIconMessageWindowClass`.
/// Значит, просканировав невидимые окна верхнего уровня по этим признакам и
/// узнав владельца (PID), можно собрать настоящий список трей-приложений.
///
/// На Windows 11 24H2+ сам список скрытых значков так не прочитать: панель задач
/// переписана на XAML-остров (`TopLevelWindowForOverflowXamlIsland`), старого
/// `ToolbarWindow32` в переполнении больше нет. А владельцы значков есть —
/// ими и пользуемся.
/// </summary>
public static class TrayApps
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    private const int GwlStyle = -16;
    private const int WsExToolWindow = 0x00000080;

    public sealed class TrayApp
    {
        public string ProcessName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? IconPath { get; init; }
        public int ProcessId { get; init; }
        public IntPtr MainWindow { get; init; }
        public string OwnerClass { get; init; } = string.Empty;
    }

    /// <summary>Что показывать и что прятать. Хранится в настройках.</summary>
    public sealed class Options
    {
        /// <summary>Прятать служебные процессы (прокси, webhelper и подобные).</summary>
        public bool HideHelpers { get; set; } = true;

        /// <summary>Имена процессов, которые человек скрыл вручную.</summary>
        public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Закреплённые процессы: видны, даже когда не запущены.</summary>
        public HashSet<string> Pinned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Служебные процессы: прокси Telegram, webhelper Steam, разные «runtime».
    /// Они держат значок, но человеку бесполезны — прячем по умолчанию.
    /// </summary>
    public static bool LooksLikeHelper(string processName)
    {
        var name = processName.ToLowerInvariant();

        return name.Contains("proxy")
            || name.Contains("webhelper")
            || name.Contains("helper")
            || name.Contains("runtimebroker")
            || name.StartsWith("steamweb", StringComparison.Ordinal)
            || name.Contains("crashpad");
    }

    /// <summary>Собрать список приложений с трей-значком (по одному на процесс).</summary>
    public static List<TrayApp> Scan(Options? options = null)
    {
        var found = new List<(uint pid, string cls)>();

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (IsWindowVisible(hwnd)) return true;

                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                var name = cls.ToString();
                if (!LooksLikeTray(name)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return true;

                found.Add((pid, name));
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        var result = new List<TrayApp>();

        // Telegram, например, держит два окна-приёмника (своё и TgWsProxy),
        // поэтому склеиваем по исполняемому файлу, а не по PID.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pid, cls) in found)
        {


            try
            {
                using var process = Process.GetProcessById((int)pid);

                // Служебные оболочки не показываем: часы, батарея — это сама Windows.
                if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* чужой разряд/права */ }

                var key = path ?? process.ProcessName;
                if (!seen.Add(key)) continue;

                if (options is not null)
                {
                    if (options.HideHelpers && LooksLikeHelper(process.ProcessName)) continue;
                    if (options.Hidden.Contains(process.ProcessName)) continue;
                }

                result.Add(new TrayApp
                {
                    ProcessId = (int)pid,
                    ProcessName = process.ProcessName,
                    DisplayName = process.MainWindowTitle.Length > 0
                        ? process.MainWindowTitle
                        : process.ProcessName,
                    IconPath = path,
                    MainWindow = process.MainWindowHandle,
                    OwnerClass = cls,
                });
            }
            catch { /* процесс уже закрылся между скан-проходами */ }
        }

        // Закреплённые показываем всегда: программа закрыта — плитка остаётся
        // приглушённой, клик запускает её заново. Иначе значок исчезает и
        // непонятно, куда делась программа.
        if (options is not null)
        {
            foreach (var pin in options.Pinned)
            {
                if (result.Any(a => string.Equals(a.IconPath, pin, StringComparison.OrdinalIgnoreCase))) continue;

                var name = Path.GetFileNameWithoutExtension(pin);
                result.Add(new TrayApp
                {
                    ProcessId = 0,
                    ProcessName = name,
                    DisplayName = name,
                    IconPath = pin,
                    MainWindow = IntPtr.Zero,
                    OwnerClass = "pinned",
                });
            }
        }

        return result.OrderBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool LooksLikeTray(string className)
    {
        if (className.Length == 0) return false;

        return className.Contains("Tray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("StatusTray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)
            || className.Contains("SystemTray", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Показать окно приложения (если оно есть) или запустить его снова.</summary>
    public static void Activate(TrayApp app)
    {
        try
        {
            if (app.MainWindow != IntPtr.Zero)
            {
                ShowWindow(app.MainWindow, 9);       // SW_RESTORE
                SetForegroundWindow(app.MainWindow);
                return;
            }

            if (!string.IsNullOrEmpty(app.IconPath) && File.Exists(app.IconPath))
                Process.Start(new ProcessStartInfo(app.IconPath) { UseShellExecute = true });
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
