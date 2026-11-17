namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DotShell.Core;

/// <summary>
/// Кружки-рябь по обоям при клике на пустой рабочий стол — механика в духе
/// Liquid Desktop, но в нашем языке: не вода и не градиенты, а разбегающиеся
/// точки-кольца акцента.
///
/// Почему так, а не «настоящая вода»:
///   • под иконками на Windows 11 24H2+ обычное WPF-окно рисовать не может
///     (desktop слоится через DirectComposition), для этого нужен отдельный
///     D3D11-helper — оставляем на потом;
///   • окно-рябь создаётся только на время клика (900 мс), клики сквозь него
///     проходят (WS_EX_TRANSPARENT), поэтому оно не мешает ни иконкам, ни
///     приложениям;
///   • клик ловим низкоуровневым хуком мыши без инъекций и без UIA —
///     хук видит только нажатия, а дальше проверяем, что под курсором
///     действительно рабочий стол (Progman/WorkerW/SysListView32).
/// </summary>
internal static class DesktopRipple
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private static readonly HookProc Proc = HookCallback;
    private static IntPtr _hook;
    private static long _lastRippleMs;

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;

        if (Environment.GetEnvironmentVariable("DOTSHELL_RIPPLE") == "0") return;
        try
        {
            if (!ShellServices.Instance.Settings.Current.DesktopRipple) return;
        }
        catch { }

        try
        {
            _hook = SetWindowsHookEx(WH_MOUSE_LL, Proc, GetModuleHandle(null), 0);
            Log.Info(_hook != IntPtr.Zero ? "desktop ripple: hook установлен" : "desktop ripple: hook не установился");
        }
        catch (Exception ex)
        {
            Log.Info($"desktop ripple: hook failed: {ex.Message}");
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_LBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (IsDesktopPoint(info.pt))
                {
                    long now = Environment.TickCount64;
                    if (now - _lastRippleMs > 70)
                    {
                        _lastRippleMs = now;
                        int x = info.pt.X, y = info.pt.Y;
                        Application.Current?.Dispatcher.BeginInvoke(new Action(() => Spawn(x, y)));
                    }
                }
            }
        }
        catch
        {
            // Хук должен быть быстрым и не бросать исключения наружу.
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static bool IsDesktopPoint(POINT point)
    {
        try
        {
            var hwnd = WindowFromPoint(point);
            if (hwnd == IntPtr.Zero) return false;

            var sb = new System.Text.StringBuilder(64);
            GetClassName(hwnd, sb, sb.Capacity);
            string cls = sb.ToString();

            // Клик по обоям/иконкам рабочего стола. Панель задач и наши окна
            // сюда не попадают — их классы другие.
            return cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";
        }
        catch
        {
            return false;
        }
    }

    private static void Spawn(int screenX, int screenY)
    {
        try
        {
            var window = new DesktopRippleWindow(screenX, screenY);
            window.Closed += (_, _) => { };
            window.Show();
        }
        catch (Exception ex)
        {
            Log.Info($"desktop ripple: spawn failed: {ex.Message}");
        }
    }
}

/// <summary>Одноразовое окно с расходящимися кольцами точек.</summary>
internal sealed class DesktopRippleWindow : Window
{
    private const double WindowSizeDip = 380;
    private readonly int _screenX;
    private readonly int _screenY;
    private readonly RippleView _view;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

    public DesktopRippleWindow(int screenX, int screenY)
    {
        _screenX = screenX;
        _screenY = screenY;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        Width = WindowSizeDip;
        Height = WindowSizeDip;
        IsHitTestVisible = false;

        _view = new RippleView();
        Content = _view;

        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is not HwndSource source) return;

            var ex = (long)DotShell.Core.Native.Win32.GetWindowLongPtr(source.Handle, -20);
            // Клики сквозь окно + не мешать фокусу + не показывать в Alt+Tab.
            ex |= 0x00000020; // WS_EX_TRANSPARENT
            ex |= 0x08000000; // WS_EX_NOACTIVATE
            ex |= 0x00000080; // WS_EX_TOOLWINDOW
            DotShell.Core.Native.Win32.SetWindowLongPtr(source.Handle, -20, (IntPtr)ex);
        };

        Loaded += (_, _) => PositionAndPlay();
        Closed += (_, _) => _view.Stop();
    }

    private void PositionAndPlay()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            uint dpi = GetDpiForWindow(hwnd);
            double scale = dpi <= 0 ? 1.0 : dpi / 96.0;
            int size = (int)Math.Round(WindowSizeDip * scale);
            int left = _screenX - size / 2;
            int top = _screenY - size / 2;

            // Встаём сразу над рабочим столом, но под обычными окнами.
            var progman = FindWindow("Progman", null);
            SetWindowPos(hwnd, progman, left, top, size, size, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
        catch (Exception ex)
        {
            Log.Info($"desktop ripple: position failed: {ex.Message}");
        }

        _view.Play(() => { try { Close(); } catch { } });
    }
}

/// <summary>Кольца точек: 3 волны, затухают за ~900 мс.</summary>
internal sealed class RippleView : FrameworkElement
{
    private readonly Stopwatch _watch = new();
    private bool _playing;
    private Action? _done;

    public void Play(Action done)
    {
        _done = done;
        _watch.Restart();
        _playing = true;
        CompositionTarget.Rendering += OnFrame;
    }

    public void Stop()
    {
        _playing = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (!_playing) return;

        double t = _watch.Elapsed.TotalMilliseconds / 900.0;
        if (t >= 1)
        {
            Stop();
            InvalidateVisual();
            _done?.Invoke();
            return;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        var accent = ColorPalette.Accent;
        var center = new Point(w / 2, h / 2);
        double t = _watch.Elapsed.TotalMilliseconds / 900.0;

        // Три волны: каждая чуть отстаёт по времени, затухает квадратично.
        for (int wave = 0; wave < 3; wave++)
        {
            double p = Math.Clamp(t * 1.15 - wave * 0.14, 0, 1);
            if (p <= 0 || p >= 1) continue;

            double radius = 16 + p * (w * 0.42);
            double alpha = Math.Pow(1 - p, 2) * (wave == 0 ? 0.85 : 0.55);
            double dotRadius = 2.6 * (1 - p * 0.55);

            var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), accent.R, accent.G, accent.B));
            brush.Freeze();

            int count = Math.Clamp((int)(radius / 9), 10, 90);
            for (int i = 0; i < count; i++)
            {
                double angle = i * Math.PI * 2 / count + wave * 0.35;
                var point = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
                dc.DrawEllipse(brush, null, point, dotRadius, dotRadius);
            }
        }

        // Всплеск в точке клика: несколько точек разлетаются и гаснут быстро.
        if (t < 0.35)
        {
            double p = t / 0.35;
            var splash = new SolidColorBrush(Color.FromArgb((byte)((1 - p) * 220), 0xF4, 0xF4, 0xF8));
            splash.Freeze();
            for (int i = 0; i < 8; i++)
            {
                double angle = i * Math.PI * 2 / 8;
                double r = 4 + p * 22;
                dc.DrawEllipse(splash, null,
                    new Point(center.X + Math.Cos(angle) * r, center.Y + Math.Sin(angle) * r),
                    1.8, 1.8);
            }
        }
    }
}
