namespace DotShell.Island.Wpf;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Input;
using DotShell.Core.Native;

/// <summary>
/// «Клавиши и сочетания»: справочник + живая проба.
///
/// Зачем: у владельца несколько клавиатур, и что делает Fn+F5 — на корпусе
/// не написано. Windows клавишу Fn вообще не видит (её обрабатывает контроллер
/// клавиатуры), поэтому узнать «что делает сочетание» можно только одним
/// способом: нажать и посмотреть, что пришло в систему. Это и делает проба —
/// низкоуровневый хук показывает VK, scan-код и распознанное действие.
/// </summary>
public sealed class KeyProbeWindow : Window
{
    private readonly TextBlock _live;
    private readonly StackPanel _log;
    private readonly List<string> _history = new();

    private static IntPtr _hook = IntPtr.Zero;
    private Win32.LowLevelKeyboardProc? _proc;

    public KeyProbeWindow()
    {
        Title = "DotShell — клавиши и сочетания";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(22, 18, 22, 18),
            Margin = new Thickness(16),
            Width = 720,
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });

        // -------- левая колонка: справочник --------
        var left = new StackPanel();

        left.Children.Add(Header("DOTSHELL"));
        left.Children.Add(Row("Ctrl+Shift+Space", "раскрыть / свернуть остров"));
        left.Children.Add(Row("Ctrl+Shift+V", "история буфера обмена"));
        left.Children.Add(Row("Ctrl+Shift+L", "лаунчер приложений"));
        left.Children.Add(Row("DotShell.exe --gallery", "витрина виджетов при запуске"));

        var pinned = ShellServices.Instance.Pinned.Items
            .Where(p => !string.IsNullOrWhiteSpace(p.Hotkey))
            .ToList();

        left.Children.Add(Header("МОИ ПРОГРАММЫ", new Thickness(0, 16, 0, 8)));
        if (pinned.Count == 0)
            left.Children.Add(new TextBlock
            {
                Text = "пока без клавиш — правый клик по плитке на острове → «Назначить клавишу…»",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10,
                Foreground = (Brush)FindResource("DsTextMuted"),
                TextWrapping = TextWrapping.Wrap,
            });
        foreach (var app in pinned)
            left.Children.Add(Row(app.Hotkey, app.Name));

        left.Children.Add(Header("WINDOWS", new Thickness(0, 16, 0, 8)));
        left.Children.Add(Row("Win+D", "свернуть всё, показать рабочий стол"));
        left.Children.Add(Row("Win+E", "проводник"));
        left.Children.Add(Row("Win+V", "журнал буфера обмена"));
        left.Children.Add(Row("Win+Shift+S", "ножницы: снимок области"));
        left.Children.Add(Row("Win+L", "заблокировать"));
        left.Children.Add(Row("Alt+Tab", "переключить окно"));
        left.Children.Add(Row("Win+Tab", "все окна и столы"));

        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // -------- правая колонка: проба --------
        var right = new StackPanel();

        right.Children.Add(Header("ПРОБА КЛАВИШ"));
        right.Children.Add(new TextBlock
        {
            Text = "Нажми любое сочетание — покажу, что реально пришло в Windows. " +
                   "Если на Fn+клавишу здесь ничего не появляется — эти клавиши обрабатывает " +
                   "сама клавиатура, и программа их не увидит никогда.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = (Brush)FindResource("DsTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        var liveBox = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
        };
        liveBox.SetResourceReference(Border.BorderBrushProperty, "DsAccent");
        liveBox.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");

        _live = new TextBlock
        {
            Text = "нажми клавишу…",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("DsText"),
        };
        liveBox.Child = _live;
        right.Children.Add(liveBox);

        _log = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        right.Children.Add(_log);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var close = new TextBlock
        {
            Text = "Esc — закрыть",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Foreground = (Brush)FindResource("DsTextMuted"),
            Margin = new Thickness(0, 12, 0, 0),
        };
        right.Children.Add(close);

        root.Child = grid;
        Content = root;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Loaded += (_, _) => InstallHook();
        Closed += (_, _) => UninstallHook();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Окно не должно воровать фокус у игры/работы, но клавиши ловим хуком.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
        ex |= Win32.WS_EX_TOOLWINDOW;
        ex &= ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));
    }

    private void InstallHook()
    {
        if (_hook != IntPtr.Zero) return;
        _proc = OnKey;
        _hook = Win32.SetWindowsHookEx(Win32.WH_KEYBOARD_LL, _proc, Win32.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) _live.Text = "не удалось включить пробу (хук не встал)";
    }

    private void UninstallHook()
    {
        if (_hook == IntPtr.Zero) return;
        Win32.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var vk = Marshal.ReadInt32(lParam);
            var scan = Marshal.ReadInt32(lParam, 4);
            var flags = Marshal.ReadInt32(lParam, 8);
            var message = (uint)wParam.ToInt64();

            if (message is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN)
            {
                var extended = (flags & 0x01) != 0;
                var modifierOnly = vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C
                    or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

                var text = KeyCombo.Describe(vk, scan, extended, modifierOnly);
                Dispatcher.BeginInvoke(() => ShowEvent(text));
            }
        }

        return Win32.CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void ShowEvent(string text)
    {
        _live.Text = text;

        _history.Insert(0, text);
        if (_history.Count > 6) _history.RemoveAt(_history.Count - 1);

        _log.Children.Clear();
        foreach (var line in _history.Skip(1))
        {
            _log.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 9,
                Foreground = (Brush)FindResource("DsTextMuted"),
                Margin = new Thickness(0, 0, 0, 3),
                TextWrapping = TextWrapping.Wrap,
            });
        }
    }

    private static TextBlock Header(string text, Thickness? margin = null)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 8,
            Foreground = (Brush)Application.Current.FindResource("DsTextMuted"),
            Margin = margin ?? new Thickness(0, 0, 0, 8),
        };
        return block;
    }

    private static Grid Row(string keys, string what)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(146) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var key = new TextBlock
        {
            Text = keys,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.FindResource("DsAccent"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(key, 0);
        grid.Children.Add(key);

        var desc = new TextBlock
        {
            Text = what,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = (Brush)Application.Current.FindResource("DsTextMuted"),
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(desc, 1);
        grid.Children.Add(desc);

        return grid;
    }
}



