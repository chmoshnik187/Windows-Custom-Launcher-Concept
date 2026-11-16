namespace DotShell.Core.Windowing;

using System.Runtime.InteropServices;
using DotShell.Core.Native;

/// <summary>
/// Мониторы: сколько их, рабочая область и идентификатор устройства.
///
/// Используется для per-monitor позиционирования/раскладок (D0/D1) без зависимости от WinForms.
/// Координаты рабочих областей — физические пиксели Win32.
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

    /// <summary>
    /// Имя устройства монитора (например, \\.\DISPLAY1) для окна — используется как
    /// стабильный ключ раскладки в рамках текущей конфигурации дисплеев.
    /// </summary>
    public static string? DeviceNameForWindow(IntPtr window)
    {
        try
        {
            var monitor = Win32.MonitorFromWindow(window, Win32.MONITOR_DEFAULTTONEAREST);
            return DeviceNameForMonitor(monitor);
        }
        catch { return null; }
    }

    /// <summary>Имя устройства монитора для точки в физических пикселях.</summary>
    public static string? DeviceNameForPoint(int x, int y)
    {
        try
        {
            var monitor = Win32.MonitorFromPoint(new Win32.POINT { X = x, Y = y }, Win32.MONITOR_DEFAULTTONEAREST);
            return DeviceNameForMonitor(monitor);
        }
        catch { return null; }
    }

    /// <summary>Рабочая область монитора по имени устройства (физические пиксели), или null.</summary>
    public static WorkArea? WorkAreaForDevice(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        WorkArea? found = null;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
            {
                try
                {
                    var mi = Win32.MONITORINFOEX.Create();
                    if (Win32.GetMonitorInfo(monitor, ref mi) &&
                        string.Equals(mi.szDevice, deviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        found = new WorkArea(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
                        return false;
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    /// <summary>Список подключённых мониторов (device name + work area).</summary>
    public static IReadOnlyList<MonitorSnapshot> All()
    {
        var list = new List<MonitorSnapshot>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
            {
                try
                {
                    var mi = Win32.MONITORINFOEX.Create();
                    if (Win32.GetMonitorInfo(monitor, ref mi))
                    {
                        var name = string.IsNullOrWhiteSpace(mi.szDevice) ? string.Empty : mi.szDevice;
                        list.Add(new MonitorSnapshot(name, new WorkArea(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom)));
                    }
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return list;
    }

    public readonly record struct MonitorSnapshot(string DeviceName, WorkArea WorkArea);

    private static string? DeviceNameForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return null;
        var mi = Win32.MONITORINFOEX.Create();
        if (!Win32.GetMonitorInfo(monitor, ref mi)) return null;
        return string.IsNullOrWhiteSpace(mi.szDevice) ? null : mi.szDevice;
    }
}

/// <summary>Прямоугольник рабочей области монитора в физических пикселях.</summary>
public readonly record struct WorkArea(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}
