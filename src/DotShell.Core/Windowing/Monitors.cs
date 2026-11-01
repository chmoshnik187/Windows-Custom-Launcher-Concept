namespace DotShell.Core.Windowing;

using System.Runtime.InteropServices;

/// <summary>
/// Мониторы: сколько их и какая рабочая область у каждого.
///
/// Раньше для этого тянулся System.Windows.Forms (ради Screen) — и вместе с ним в
/// сборку попадали WinForms и его дизайнер, это около 21 МБ из 172. Здесь то же самое
/// на трёх вызовах Win32: перечислить мониторы, узнать их прямоугольники.
/// </summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    private const uint MonitorDefaultToNearest = 2;

    /// <summary>Сколько мониторов подключено.</summary>
    public static int Count
    {
        get
        {
            int count = 0;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
                {
                    count++;
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            return count;
        }
    }

    /// <summary>
    /// Рабочая область монитора, на котором находится окно (без панели задач).
    /// Возвращает null, если монитор не определился.
    /// </summary>
    public static WorkArea? ForWindow(IntPtr window)
    {
        try
        {
            var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero) return null;

            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(monitor, ref info)) return null;

            return new WorkArea(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        }
        catch { return null; }
    }
}

/// <summary>Прямоугольник рабочей области монитора в физических пикселях.</summary>
public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
