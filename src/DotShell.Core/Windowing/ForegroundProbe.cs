namespace DotShell.Core.Windowing;

using System.Runtime.InteropServices;

/// <summary>
/// «На переднем плане развёрнутое окно?» — для режима «не мешать».
///
/// Отличие от полноэкранного режима: полноэкранное окно полностью закрывает монитор
/// (игры, видео), а развёрнутое занимает рабочую область — например, браузер с сотней
/// вкладок. Во втором случае остров висит поверх полосы вкладок и мешает, хотя
/// «полным экраном» это не считается.
///
/// Проверяем по геометрии, а не по стилю окна: многие приложения рисуют «развёрнуто»
/// без WS_MAXIMIZE. Заодно пропускаем рабочий стол, панель задач и свои окна.
/// </summary>
public static class ForegroundProbe
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// Занято ли рабочее место чужим развёрнутым окном.
    /// </summary>
    public static bool IsForegroundMaximized()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            GetWindowThreadProcessId(foreground, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false;   // это мы
            if (IsIconic(foreground)) return false;                 // свёрнуто

            var cls = new System.Text.StringBuilder(64);
            GetClassName(foreground, cls, cls.Capacity);
            var name = cls.ToString();

            // Рабочий стол и панель задач — не повод прятаться.
            if (name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;

            var area = System.Windows.Forms.Screen.FromHandle(foreground).WorkingArea;

            if (!GetWindowRect(foreground, out var rect)) return false;

            // Допуск: окно «развёрнуто», если занимает почти всю рабочую область.
            const int tolerance = 8;

            bool wide = rect.Left <= area.Left + tolerance && rect.Right >= area.Right - tolerance;
            bool tall = rect.Top <= area.Top + tolerance && rect.Bottom >= area.Bottom - tolerance;

            return wide && tall;
        }
        catch (Exception)
        {
            return IsZoomedFallback();
        }
    }

    private static bool IsZoomedFallback()
    {
        try { return IsZoomed(GetForegroundWindow()); }
        catch { return false; }
    }
}
