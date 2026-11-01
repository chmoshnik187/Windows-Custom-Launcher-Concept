namespace DotShell.Island.Wpf;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotShell.Core.Native;

/// <summary>
/// «Шторки фокуса»: всё, кроме монитора с активным окном, уходит под тёмную
/// вуаль. Периферийное зрение выключено, мигающий чат на втором экране не
/// существует — остаётся только то, в чём ты печатаешь прямо сейчас.
///
/// Реализация одним окном на всю виртуальную поверхность: в геометрии вуали
/// вырезана дыра по границам активного монитора. Прозрачные пиксели
/// (AllowsTransparency) пропускают клики САМИ, а всё окно помечено
/// click-through — поэтому вуаль никого не ловит в плен: клик по затемнённому
/// окну проходит сквозь неё, окно оживает, дыра переезжает следом.
/// </summary>
public static class FocusBlinders
{
    private static Window? _window;
    private static Path? _veil;
    private static DispatcherTimer? _timer;
    private static Rect _lastHole = Rect.Empty;

    public static bool IsOn => _window is not null;

    public static void SetEnabled(bool on)
    {
        if (on) Show();
        else Hide();
    }

    private static void Show()
    {
        if (_window is not null) return;

        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
        };
        _veil = new Path { IsHitTestVisible = false };
        _window.Content = _veil;

        _window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
            ex &= ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
            ShellServices.Instance.OwnWindows.Add(hwnd); // авто-цвет не берёт пробы с вуали
        };

        _window.Show();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _timer.Tick += (_, _) => UpdateHole();
        _timer.Start();
        UpdateHole();
    }

    public static void Hide()
    {
        _timer?.Stop();
        _timer = null;
        _window?.Close();
        _window = null;
        _veil = null;
        _lastHole = Rect.Empty;
    }

    /// <summary>Пересчитать дыру в вуали под монитор активного окна.</summary>
    private static void UpdateHole()
    {
        if (_window is null || _veil is null) return;

        var fg = Win32.GetForegroundWindow();
        if (fg == IntPtr.Zero) return;
        // Клик по самому острову не должен переносить дыру: остров живёт сверху.
        if (ShellServices.Instance.OwnWindows.Contains(fg)) return;

        var monitor = Win32.MonitorFromWindow(fg, Win32.MONITOR_DEFAULTTONEAREST);
        var hole = MonitorBoundsDip(monitor);
        if (hole == Rect.Empty || hole == _lastHole) return;
        _lastHole = hole;

        var origin = new Point(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop);
        var full = new Rect(0, 0, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var local = new Rect(hole.X - origin.X, hole.Y - origin.Y, hole.Width, hole.Height);

        _veil.Data = new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(full), new RectangleGeometry(local));
        _veil.Fill = new SolidColorBrush(Color.FromArgb(185, 0, 0, 0));
    }

    /// <summary>Границы монитора в WPF-единицах (физические пиксели / DPI монитора).</summary>
    private static Rect MonitorBoundsDip(IntPtr monitor)
    {
        foreach (var (handle, physical, scale) in Monitors())
            if (handle == monitor)
                return new Rect(physical.X / scale, physical.Y / scale, physical.Width / scale, physical.Height / scale);
        return Rect.Empty;
    }

    private static List<(IntPtr Handle, Rect Physical, double Scale)> Monitors()
    {
        var list = new List<(IntPtr, Rect, double)>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr handle, IntPtr hdc, ref Win32.RECT rect, IntPtr data) =>
        {
            uint dpiX = 96, dpiY = 96;
            try { Win32.GetDpiForMonitor(handle, 0, out dpiX, out dpiY); } catch { }
            list.Add((handle,
                new Rect(rect.Left, rect.Top, rect.Width, rect.Height),
                dpiX / 96.0));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
