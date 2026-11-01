namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DotShell.Core.Input;
using DotShell.Core.Native;

/// <summary>
/// «Подсказка хоткеев»: зажал Ctrl, Alt или Win — рядом с нижним краем экрана
/// всплывает полупрозрачная плашка с шорткатами ИМЕННО активной программы
/// (база HotkeyCatalog + твои дописки в hotkeys.json). Отпустил — плашка
/// гаснет через секунду.
///
/// Хук низкоуровневый (WH_KEYBOARD_LL), но работа в нём копеечная: посмотрели
/// код клавиши и ушли дальше, а показ делаем через Dispatcher. Клавиатуру мы
/// НЕ блокируем: CallNextHookEx вызывается всегда.
/// </summary>
public static class HotkeyHud
{
    private static IntPtr _hook = IntPtr.Zero;
    private static Win32.LowLevelKeyboardProc? _proc; // держим ссылку: иначе сборщик мусора съест хук
    private static Window? _window;
    private static WrapPanel? _chips;
    private static DispatcherTimer? _hideTimer;
    private static string _lastProcess = string.Empty;
    // Коды модификаторов, которые реально приходят из WH_KEYBOARD_LL.
    private const int VkLeftCtrl = 0xA2;
    private const int VkRightCtrl = 0xA3;
    private const int VkLeftAlt = 0xA4;
    private const int VkRightAlt = 0xA5;

    public static bool Enabled { get; private set; }

    public static void SetEnabled(bool on)
    {
        Enabled = on;
        if (on) Install();
        else { Uninstall(); HideNow(); }
    }

    private static void Install()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = HookCallback;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Info("hotkey hud: hook not installed");
    }

    private static void Uninstall()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = System.Runtime.InteropServices.Marshal.ReadInt32(lParam);
            uint msg = (uint)wParam.ToInt64();
            // Низкоуровневый хук присылает НЕ общие коды (VK_CONTROL=0x11,
            // VK_MENU=0x12), а левые/правые: 0xA2/0xA3 и 0xA4/0xA5. Из-за этого
            // подсказки раньше срабатывали только на клавишу Win, а на обычный
            // левый Ctrl и Alt — молчали.
            bool modifier = vk is Win32.VK_CONTROL or Win32.VK_MENU or Win32.VK_LWIN or Win32.VK_RWIN
                or VkLeftCtrl or VkRightCtrl or VkLeftAlt or VkRightAlt;

            if (modifier && (msg == Win32.WM_KEYDOWN || msg == Win32.WM_SYSKEYDOWN))
            {
                _hideTimer?.Stop();
                Show();
            }
            else if (modifier && (msg == Win32.WM_KEYUP || msg == Win32.WM_SYSKEYUP))
            {
                // Отпустил модификатор — даём секунду дожать сочетание и гасим.
                if (_hideTimer is null)
                {
                    _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); HideNow(); };
                }
                _hideTimer.Stop();
                _hideTimer.Start();
            }
        }
        return Win32.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private static void Show()
    {
        var process = ShellServices.Instance.WindowWatcher.ForegroundProcessName;
        if (string.IsNullOrEmpty(process)) return;

        if (_window is null)
        {
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                SizeToContent = SizeToContent.WidthAndHeight,
            };
            var border = new Border
            {
                CornerRadius = new CornerRadius(2),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(14, 10, 16, 12),
                Margin = new Thickness(8),
                MaxWidth = 760,
            };
            border.SetResourceReference(Border.BackgroundProperty, "DsSurface");
            border.SetResourceReference(Border.BorderBrushProperty, "DsBorder");
            _chips = new WrapPanel { Orientation = Orientation.Horizontal };
            border.Child = _chips;
            _window.Content = border;

            _window.SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(_window).Handle;
                var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
                ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE;
                ex &= ~Win32.WS_EX_APPWINDOW;
                Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
                ShellServices.Instance.OwnWindows.Add(hwnd);
            };
        }

        if (!string.Equals(process, _lastProcess, StringComparison.OrdinalIgnoreCase) || _chips!.Children.Count == 0)
        {
            _lastProcess = process;
            RebuildChips(process);
        }

        PositionAtBottom();
        if (!_window.IsVisible) _window.Show();
    }

    /// <summary>
    /// Плашка собрана как техническая таблица, а не как набор цветных пилюль:
    /// микро-заголовок с именем программы, ниже — ровная сетка «клавиши → что
    /// делают». Клавиши акцентом и моноширинным начертанием, описание тихое.
    /// Пилюли с синим текстом читались как рекламный баннер из нулевых.
    /// </summary>
    private static void RebuildChips(string process)
    {
        _chips!.Children.Clear();

        var root = new StackPanel { Width = 640 };

        var header = new Controls.TrackedText
        {
            Text = $"ХОТКЕИ · {process}",
            FontSize = 8,
            Tracking = 0.28,
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        header.SetResourceReference(Controls.TrackedText.BrushProperty, "DsTextMuted");
        root.Children.Add(header);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        int row = 0;
        foreach (var tip in HotkeyCatalog.For(process))
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var keys = new TextBlock
            {
                Text = tip.Keys,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 3),
            };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
            Grid.SetRow(keys, row);
            Grid.SetColumn(keys, 0);
            grid.Children.Add(keys);

            var what = new TextBlock
            {
                Text = tip.What,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 3),
            };
            what.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
            Grid.SetRow(what, row);
            Grid.SetColumn(what, 2);
            grid.Children.Add(what);

            // Волосяная линия между строками: сетка читается, декора нет.
            if (row > 0)
            {
                var line = new Border
                {
                    Height = 1,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 0, 0, 0),
                };
                line.SetResourceReference(Border.BackgroundProperty, "DsBorder");
                line.Opacity = 0.5;
                Grid.SetRow(line, row);
                Grid.SetColumnSpan(line, 3);
                grid.Children.Add(line);
            }

            row++;
        }

        root.Children.Add(grid);
        _chips.Children.Add(root);
    }

    private static void PositionAtBottom()
    {
        if (_window is null) return;
        var wa = SystemParameters.WorkArea;
        _window.UpdateLayout();
        _window.Left = wa.Left + (wa.Width - _window.ActualWidth) / 2;
        _window.Top = wa.Bottom - _window.ActualHeight - 24;
    }

    private static void HideNow()
    {
        _window?.Hide();
    }
}





