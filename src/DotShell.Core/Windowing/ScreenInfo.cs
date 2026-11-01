namespace DotShell.Core.Windowing;

using DotShell.Core.Native;

/// <summary>
/// Геометрия монитора и определение "что-то открыто на весь экран".
///
/// Вынесено отдельно от WindowWatcher, потому что нужно и острову (встать по центру
/// верхнего края), и будущим обоям (занять весь виртуальный экран), и панели
/// буфера обмена (появиться рядом с курсором, но не вылезти за монитор).
///
/// Все координаты здесь — ФИЗИЧЕСКИЕ пиксели, как их отдаёт Win32.
/// Переводом в логические единицы WPF (DIU) занимается вызывающий код,
/// потому что ядро ничего не знает про WPF.
/// </summary>
public static class ScreenInfo
{
    public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    /// <summary>Монитор, на котором сейчас находится курсор.</summary>
    public static ScreenRect GetMonitorFromCursor()
    {
        if (!Win32.GetCursorPos(out var pt)) return GetPrimaryMonitor();
        IntPtr monitor = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
        return FromMonitor(monitor);
    }

    /// <summary>Монитор, на котором находится указанное окно.</summary>
    public static ScreenRect GetMonitorFromWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return GetPrimaryMonitor();
        IntPtr monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        return FromMonitor(monitor);
    }

    public static ScreenRect GetPrimaryMonitor()
    {
        IntPtr monitor = Win32.MonitorFromPoint(new Win32.POINT { X = 0, Y = 0 },
            Win32.MONITOR_DEFAULTTOPRIMARY);
        return FromMonitor(monitor);
    }

    /// <summary>Общая площадь всех мониторов (для слоя обоев).</summary>
    public static ScreenRect GetVirtualScreen()
    {
        int x = Win32.GetSystemMetrics(Win32.SM_XVIRTUALSCREEN);
        int y = Win32.GetSystemMetrics(Win32.SM_YVIRTUALSCREEN);
        int w = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int h = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        return new ScreenRect(x, y, x + w, y + h);
    }

    /// <summary>
    /// Занято ли сейчас всё экранное пространство каким-то окном.
    ///
    /// ВНИМАНИЕ: это более мягкая проверка, чем WindowWatcher.CheckFullscreen.
    /// Та требовала ещё и отсутствия рамки (чтобы не считать полноэкранным
    /// обычное развернутое окно браузера). Здесь нам важно именно "пиксели
    /// рабочего стола не видны", поэтому рамка не проверяется: развернутое
    /// на весь монитор окно без рамки (borderless-windowed игра, видеоплеер)
    /// и есть тот случай, когда остров надо прятать, а обои — останавливать.
    /// </summary>
    public static bool IsForegroundCoveringMonitor()
    {
        IntPtr hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == Win32.GetDesktopWindow()) return false;
        if (!Win32.IsWindowVisible(hwnd)) return false;

        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0
            && cloaked != 0)
            return false;

        if (!TryGetExtendedBounds(hwnd, out var win)) return false;

        var monitor = GetMonitorFromWindow(hwnd);
        return win.Left <= monitor.Left && win.Top <= monitor.Top &&
               win.Right >= monitor.Right && win.Bottom >= monitor.Bottom;
    }

    /// <summary>
    /// Точный прямоугольник окна: с учётом тени и скруглений, а не "логических" границ.
    /// Нужен для режима "заплывания" обоев — дырки режутся именно по этим границам.
    /// </summary>
    public static bool TryGetExtendedBounds(IntPtr hwnd, out Win32.RECT bounds)
    {
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out bounds, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
            return true;

        return Win32.GetWindowRect(hwnd, out bounds);
    }

    private static ScreenRect FromMonitor(IntPtr monitor)
    {
        var mi = Win32.MONITORINFOEX.Create();
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref mi))
        {
            int w = Win32.GetSystemMetrics(Win32.SM_CXSCREEN);
            int h = Win32.GetSystemMetrics(Win32.SM_CYSCREEN);
            return new ScreenRect(0, 0, w, h);
        }
        return new ScreenRect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom);
    }
}
