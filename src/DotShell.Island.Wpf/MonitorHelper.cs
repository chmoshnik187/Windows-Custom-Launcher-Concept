namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Interop;
using DotShell.Core.Native;
using DotShell.Core.Windowing;

/// <summary>
/// Единая точка получения геометрии монитора для WPF-окон.
/// Win32 отдаёт физические пиксели; здесь переводим в DIP текущего окна.
/// </summary>
internal static class MonitorHelper
{
    /// <summary>
    /// Рабочая область монитора, на котором находится окно, в DIP-координатах.
    /// Fallback: <see cref="SystemParameters.WorkArea"/> (основной монитор).
    /// </summary>
    public static Rect GetWorkAreaDip(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero && Monitors.ForWindow(hwnd) is { } physical)
                return PhysicalWorkAreaToDip(window, physical);
        }
        catch
        {
            // fallback ниже
        }

        return SystemParameters.WorkArea;
    }

    /// <summary>Рабочая область монитора, на котором сейчас курсор, в DIP окна.</summary>
    public static Rect GetWorkAreaDipForCursor(Window window)
    {
        try
        {
            if (Win32.GetCursorPos(out var pt))
            {
                var monitor = Win32.MonitorFromPoint(pt, Win32.MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var mi = Win32.MONITORINFOEX.Create();
                    if (Win32.GetMonitorInfo(monitor, ref mi))
                    {
                        var physical = new WorkArea(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
                        return PhysicalWorkAreaToDip(window, physical);
                    }
                }
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }

    /// <summary>Рабочая область монитора якоря (остров, если открыт; иначе курсор).</summary>
    public static Rect GetAnchorWorkAreaDip(Window window)
    {
        try
        {
            var island = IslandWindow.Current;
            if (island is not null && island.IsLoaded)
                return GetWorkAreaDip(island);
        }
        catch { }

        return GetWorkAreaDipForCursor(window);
    }

    private static Rect PhysicalWorkAreaToDip(Window window, WorkArea physical)
    {
        var topLeft = window.PointFromScreen(new Point(physical.Left, physical.Top));
        var bottomRight = window.PointFromScreen(new Point(physical.Right, physical.Bottom));

        double left = window.Left + topLeft.X;
        double top = window.Top + topLeft.Y;
        double width = Math.Max(120, bottomRight.X - topLeft.X);
        double height = Math.Max(120, bottomRight.Y - topLeft.Y);
        return new Rect(left, top, width, height);
    }

    /// <summary>Границы виртуального экрана в DIP.</summary>
    public static Rect GetVirtualScreenDip()
    {
        return new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
    }

    /// <summary>Кламп окна внутри рабочей области монитора (или виртуального экрана как fallback).</summary>
    public static void ClampToWorkArea(Window window, Rect? workArea = null)
    {
        var area = workArea ?? GetWorkAreaDip(window);
        double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(w) || w <= 0) w = 320;
        if (double.IsNaN(h) || h <= 0) h = 200;

        double left = window.Left;
        double top = window.Top;
        if (double.IsNaN(left)) left = area.Left;
        if (double.IsNaN(top)) top = area.Top;

        if (w <= area.Width) left = Math.Clamp(left, area.Left, area.Right - w);
        else left = area.Left;
        if (h <= area.Height) top = Math.Clamp(top, area.Top, area.Bottom - h);
        else top = area.Top;

        window.Left = left;
        window.Top = top;
    }
}
