namespace DotShell.Core.Windowing;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DotShell.Core.Native;

/// <summary>Одно настоящее окно для «карты окон».</summary>
public sealed class WindowInfo
{
    public IntPtr Hwnd { get; init; }
    public string Title { get; init; } = string.Empty;
    public string ProcessName { get; init; } = string.Empty;
    public string ExePath { get; init; } = string.Empty;
}

/// <summary>
/// «Визуальный альт-таб» без ломки системного: честный список живых окон
/// (EnumWindows с фильтрами как у настоящего таскбара) + снимок окна через
/// PrintWindow для превью. Системный Alt+Tab мы НЕ перехватываем: это хрупко
/// и злит людей; карта живёт виджетом и открывается, когда нужна человеку.
/// </summary>
public static class WindowList
{
    /// <summary>Все окна, которые человек считает «своими программами».</summary>
    public static List<WindowInfo> Get(IntPtr[] ownWindows)
    {
        var result = new List<WindowInfo>();
        var own = new HashSet<IntPtr>(ownWindows);

        Win32.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (own.Contains(hwnd)) return true;
                if (!Win32.IsWindowVisible(hwnd)) return true;

                // Окна-инструменты (тултипы, палитры) в карте не нужны.
                var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
                if ((ex & Win32.WS_EX_TOOLWINDOW) != 0) return true;

                // «Плащи» DWM: свёрнутые в анимацию/скрытые системные окна.
                Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_CLOAKED, out int cloaked, sizeof(int));
                if (cloaked != 0) return true;

                var len = Win32.GetWindowTextLength(hwnd);
                if (len <= 0) return true;
                var sb = new StringBuilder(len + 1);
                Win32.GetWindowText(hwnd, sb, sb.Capacity);
                var title = sb.ToString().Trim();
                if (title.Length == 0) return true;

                Win32.GetWindowThreadProcessId(hwnd, out uint pid);
                string proc = string.Empty, exe = string.Empty;
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    proc = p.ProcessName;
                    try { exe = p.MainModule?.FileName ?? string.Empty; } catch { }
                }
                catch { }

                result.Add(new WindowInfo { Hwnd = hwnd, Title = title, ProcessName = proc, ExePath = exe });
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static void Focus(IntPtr hwnd)
    {
        try { Win32.SetForegroundWindow(hwnd); } catch { }
    }

    /// <summary>Вежливое «закройся» — то же, что крестик окна.</summary>
    public static void Close(IntPtr hwnd) => Win32.PostMessage(hwnd, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    /// <summary>
    /// Снимок окна (PrintWindow с PW_RENDERFULLCONTENT: ловит и D3D-контент,
    /// где обычная перерисовка даёт чёрный квадрат). Игра с античитом может
    /// отказаться — тогда превью просто не покажем, без вранья.
    /// </summary>
    public static System.Drawing.Bitmap? Snapshot(IntPtr hwnd)
    {
        try
        {
            if (!Win32.GetWindowRect(hwnd, out var rect)) return null;
            int w = rect.Width, h = rect.Height;
            if (w < 16 || h < 16 || w > 8192 || h > 8192) return null;

            var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            var hdc = g.GetHdc();
            bool ok = Win32.PrintWindow(hwnd, hdc, 2);
            g.ReleaseHdc(hdc);
            if (!ok) { bmp.Dispose(); return null; }
            return bmp;
        }
        catch { return null; }
    }
}
