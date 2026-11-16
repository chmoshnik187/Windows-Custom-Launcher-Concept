namespace DotShell.Island.Wpf;

using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DotShell.Core.Native;
using DotShell.Core.Windowing;

/// <summary>
/// D0/D1: host-слой рабочего стола (фундамент под размещение виджетов на desktop-layer).
/// Включается флагом DesktopHostEnabled. По возможности создаёт host-окно на каждый монитор;
/// при невозможности перечислить мониторы — один host на виртуальный экран.
/// </summary>
public sealed class DesktopHostWindow : Window
{
    private static readonly List<DesktopHostWindow> _hosts = new();

    public static void Sync()
    {
        try
        {
            bool want = ShellServices.Instance.Settings.Current.DesktopHostEnabled;
            if (want) EnsureHosts();
            else CloseAll();
        }
        catch (Exception ex)
        {
            Log.Info($"desktop host sync failed: {ex.Message}");
        }
    }

    private static void EnsureHosts()
    {
        try
        {
            var monitors = Monitors.All();
            if (monitors.Count == 0)
            {
                if (_hosts.Count == 0)
                {
                    var single = new DesktopHostWindow(null);
                    _hosts.Add(single);
                    single.Show();
                }
                else
                {
                    foreach (var h in _hosts.ToArray()) h.RefreshBounds();
                }
                return;
            }

            // Конфигурация мониторов изменилась — пересоздаём набор.
            if (_hosts.Count != monitors.Count)
            {
                CloseAll();
            }

            if (_hosts.Count == 0)
            {
                for (int i = 0; i < monitors.Count; i++)
                {
                    var h = new DesktopHostWindow(monitors[i].DeviceName);
                    _hosts.Add(h);
                    h.Show();
                }
            }
            else
            {
                foreach (var h in _hosts.ToArray()) h.RefreshBounds();
            }
        }
        catch
        {
            if (_hosts.Count == 0)
            {
                try
                {
                    var single = new DesktopHostWindow(null);
                    _hosts.Add(single);
                    single.Show();
                }
                catch { }
            }
        }
    }

    private static void CloseAll()
    {
        foreach (var h in _hosts.ToArray())
        {
            try { h.Close(); } catch { }
        }
        _hosts.Clear();
    }

    /// <summary>D1.2: удерживать host-слой ниже desktop-виджетов при HWND_BOTTOM стратегии.</summary>
    public static void ReassertBottom()
    {
        foreach (var h in _hosts.ToArray())
        {
            try { h.SendToBackLayer(); } catch { }
        }
    }

    private readonly string? _monitorDevice;
    private readonly System.Windows.Threading.DispatcherTimer _keepBackTimer;

    private DesktopHostWindow(string? monitorDevice)
    {
        _monitorDevice = monitorDevice;
        Title = string.IsNullOrEmpty(monitorDevice) ? "DotShell Desktop Host" : $"DotShell Desktop Host ({monitorDevice})";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0));
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Focusable = false;
        IsHitTestVisible = false;

        _keepBackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _keepBackTimer.Tick += (_, _) =>
        {
            try { if (IsLoaded) SendToBackLayer(); } catch { }
        };

        Loaded += (_, _) =>
        {
            RefreshBounds();
            ConfigureWindowStyles();
            SendToBackLayer();
            _keepBackTimer.Start();
        };

        SourceInitialized += (_, _) =>
        {
            ConfigureWindowStyles();
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            _keepBackTimer.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _hosts.Remove(this);
        };
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                Sync();
            });
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
        {
            try { Dispatcher.BeginInvoke(Sync); } catch { }
        }
        return IntPtr.Zero;
    }

    private void RefreshBounds()
    {
        try
        {
            Rect bounds;
            if (!string.IsNullOrEmpty(_monitorDevice))
            {
                var physical = Monitors.WorkAreaForDevice(_monitorDevice);
                if (physical is { } wa)
                {
                    // Приближение: физические пиксели как DIPs. На 100% DPI совпадает;
                    // точная per-monitor DIP-геометрия для mixed-DPI ограничена окружением.
                    bounds = new Rect(wa.Left, wa.Top, wa.Width, wa.Height);
                }
                else
                {
                    bounds = MonitorHelper.GetVirtualScreenDip();
                }
            }
            else
            {
                bounds = MonitorHelper.GetVirtualScreenDip();
            }

            Left = bounds.Left;
            Top = bounds.Top;
            Width = Math.Max(1, bounds.Width);
            Height = Math.Max(1, bounds.Height);
        }
        catch { }
    }

    private void ConfigureWindowStyles()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE;
            ex &= ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
            TryAttachToDesktopLayer(hwnd);
        }
        catch { }
    }

    private void SendToBackLayer()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var parent = Win32.GetParent(hwnd);
            if (parent == IntPtr.Zero || parent == Win32.GetDesktopWindow())
            {
                Win32.SetWindowPos(hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
        }
        catch { }
    }

    private static void TryAttachToDesktopLayer(IntPtr hwnd)
    {
        try
        {
            // Best-effort: прикрепить host к WorkerW (слой рабочего стола).
            // При неудаче остаётся fallback HWND_BOTTOM.
            var progman = Win32.FindWindow("Progman", null);
            if (progman == IntPtr.Zero) return;

            Win32.SendMessageTimeout(progman, 0x052C, UIntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

            IntPtr workerW = IntPtr.Zero;
            Win32.EnumWindows((top, _) =>
            {
                var shellView = Win32.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (shellView != IntPtr.Zero)
                {
                    workerW = Win32.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return false;
                }
                return true;
            }, IntPtr.Zero);

            if (workerW != IntPtr.Zero)
            {
                Win32.SetParent(hwnd, workerW);
            }
        }
        catch
        {
            // fallback: HWND_BOTTOM в SendToBackLayer
        }
    }
}
