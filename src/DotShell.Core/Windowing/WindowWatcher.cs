namespace DotShell.Core.Windowing;

using DotShell.Core.Native;

/// <summary>
/// Наблюдатель за окнами. Главная задача сейчас — понять,
/// "не открыто ли что-то на весь экран", чтобы:
///   • спрятать остров (он не должен висеть поверх игры или фильма);
///   • остановить отрисовку обоев (зачем жрать GPU, если их не видно);
///   • выключить "заплывание" обоев на окна.
///
/// Как определяем fullscreen (три проверки, любая из них считается):
///   1. Прямоугольник окна = прямоугольник монитора, на котором оно находится.
///      Это ловит и настоящий exclusive-fullscreen, и borderless-windowed игры.
///   2. У окна нет заголовка и рамки (WS_CAPTION / WS_THICKFRAME сброшены)
///      и оно не свёрнуто — типичный признак игры или видеоплеера.
///   3. Окно не "клоакировано" (DWMWA_CLOAKED) — клоакированные окна
///      (свёрнутые UWP, виртуальные рабочие столы) не в счёт.
///
/// Почему опрос раз в 300 мс, а не WinEvent-хук: хук точнее, но требует
/// отдельного потока с очередью сообщений и аккуратной работы с таймаутами.
/// Для скелета опрос надёжнее и не может "подвесить" систему.
/// Заменить на WinEvent-хук — задача Фазы 2 (см. README, пункт "что докрутить").
/// </summary>
public sealed class WindowWatcher : IDisposable
{
    private readonly Timer _timer;
    private bool _fullscreen;

    /// <summary>true, если прямо сейчас что-то занимает весь экран.</summary>
    public bool IsFullscreenActive => _fullscreen;

    /// <summary>HWND окна, которое сейчас в фокусе (для whitelist'ов и отладки).</summary>
    public IntPtr ForegroundWindow { get; private set; }

    /// <summary>Имя процесса фокусного окна — нужно, чтобы не трогать игры по списку.</summary>
    public string ForegroundProcessName { get; private set; } = string.Empty;

    /// <summary>Срабатывает только в момент ПЕРЕХОДА состояния (false→true или true→false).</summary>
    public event Action<bool>? FullscreenChanged;

    public WindowWatcher(int pollMs = 500)
    {
        _timer = new Timer(_ => Poll(), null, pollMs, pollMs);
    }

    private void Poll()
    {
        bool now = CheckFullscreen(out var hwnd, out var processName);
        ForegroundWindow = hwnd;
        ForegroundProcessName = processName;

        if (now != _fullscreen)
        {
            _fullscreen = now;
            FullscreenChanged?.Invoke(now);
        }
    }

    /// <summary>Проверить "здесь и сейчас" — можно звать и вручную, например перед отрисовкой обоев.</summary>
    public bool CheckFullscreen(out IntPtr hwnd, out string processName)
    {
        hwnd = Win32.GetForegroundWindow();
        processName = string.Empty;
        if (hwnd == IntPtr.Zero || hwnd == Win32.GetDesktopWindow()) return false;
        // Клик по рабочему столу делает активным Progman/WorkerW (оболочку).
        // Это НЕ фуллскрин, иначе остров прятался бы от любого клика по обоям.
        if (hwnd == Win32.GetShellWindow()) return false;
        if (!Win32.IsWindowVisible(hwnd)) return false;

        // клоакированное окно не считаем полноэкранным
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
            return false;

        if (!Win32.GetWindowRect(hwnd, out var winRect)) return false;

        var mi = Win32.MONITORINFOEX.Create();
        IntPtr monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref mi)) return false;

        bool coversMonitor =
            winRect.Left <= mi.rcMonitor.Left && winRect.Top <= mi.rcMonitor.Top &&
            winRect.Right >= mi.rcMonitor.Right && winRect.Bottom >= mi.rcMonitor.Bottom;

        processName = GetProcessName(hwnd);

        if (!coversMonitor) return false;

        // Развёрнутое (maximize) окно — НЕ фуллскрин, даже если у него нет рамки:
        // Chrome, Spotify, Discord и VS Code рисуют заголовок сами и не имеют
        // WS_CAPTION, но showCmd у них SW_SHOWMAXIMIZED. Настоящие borderless-игры
        // maximized не бывают — они просто растягивают окно на весь монитор.
        if (Win32.GetWindowPlacement(hwnd, out var placement) &&
            placement.showCmd == Win32.SW_SHOWMAXIMIZED)
            return false;

        var className = Win32.GetWindowClassName(hwnd);
        if (className is "Progman" or "WorkerW") return false;

        long style = Win32.GetWindowLongPtr(hwnd, Win32.GWL_STYLE).ToInt64();
        bool hasCaption = (style & Win32.WS_CAPTION) == Win32.WS_CAPTION;
        bool hasThickFrame = (style & Win32.WS_THICKFRAME) != 0;

        // Окно на весь монитор БЕЗ рамки и заголовка = игра/видео.
        // Окно на весь монитор С заголовком = просто развернутое приложение,
        // и его мы полноэкранным не считаем (иначе остров исчезал бы при любом maximize).
        return !hasCaption && !hasThickFrame;
    }

    // Имя процесса запрашиваем один раз на hwnd: GetProcessById открывает handle,
    // и дёргать его каждые полсекунды расточительно (ощутимые аллокации + ядро).
    private static readonly System.Collections.Generic.Dictionary<IntPtr, string> ProcNameCache = new();

    private static string GetProcessName(IntPtr hwnd)
    {
        if (ProcNameCache.TryGetValue(hwnd, out var cached)) return cached;

        string name = string.Empty;
        try
        {
            // GetWindowThreadProcessId возвращает ID потока, а не признак успеха (в отличие
            // от большинства Win32-функций). Поэтому проверяем только pid.
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
        }
        catch
        {
            name = string.Empty;
        }

        if (ProcNameCache.Count > 256) ProcNameCache.Clear();
        ProcNameCache[hwnd] = name;
        return name;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>
    /// Точный прямоугольник окна с учётом тени и скруглений (а не "логических" границ).
    /// Нужен для режима "заплывания" обоев — дырки надо резать именно по этим границам.
    /// </summary>
    public static bool TryGetExtendedBounds(IntPtr hwnd, out Win32.RECT bounds)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out bounds, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
            return true;

        return Win32.GetWindowRect(hwnd, out bounds);
    }

    /// <summary>Периметр виртуального рабочего стола (все мониторы) — для слоя обоев.</summary>
    public static (int x, int y, int w, int h) GetVirtualScreen() =>
        (0, 0,
         Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN),
         Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN));

    public void Dispose() => _timer.Dispose();
}
