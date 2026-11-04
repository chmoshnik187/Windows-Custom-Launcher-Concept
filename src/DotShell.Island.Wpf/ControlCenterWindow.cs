namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Native;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Центр управления: выезжает справа, как в Linux-шеллах из видео владельца.
///
/// Внутри — то, ради чего обычно лезут в трей Windows: громкость, быстрые
/// переключатели, питание и вход в настройки. Сделано на тех же контролах, что и
/// остров (DotSeekBar), поэтому стиль общий, а не «ещё одно окно».
/// </summary>
public sealed class ControlCenterWindow : Window
{
    private static ControlCenterWindow? _open;

    private readonly SystemVolume _volume = new();
    private readonly DispatcherTimer _statsTimer;
    private TextBlock? _statsLine;
    private TextBlock? _batteryLine;
    private TextBlock? _clockLine;
    private readonly DotSeekBar _volumeBar;
    private readonly TextBlock _volumeValue;

    public static void Toggle()
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }

        var window = new ControlCenterWindow();
        _open = window;
        window.Show();
        window.Activate();
        window.SlideIn();
    }

    public ControlCenterWindow()
    {
        Title = "DotShell — центр управления";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 340;

        // Высота по содержимому: раньше окно было 520–650 px с пустотой в треть экрана.
        SizeToContent = SizeToContent.Height;
        UiScale.ApplyToContent(this);

        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Top + 24;

        var root = new StackPanel { Margin = new Thickness(20, 18, 20, 18) };

        root.Children.Add(new TextBlock
        {
            Text = $"ЦЕНТР УПРАВЛЕНИЯ · {DateTime.Now:dd.MM} · {DateTime.Now:HH:mm}",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 14),
        });

        // --- громкость ---
        root.Children.Add(Section("ГРОМКОСТЬ"));

        _volumeValue = new TextBlock
        {
            Text = "—",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 22,
            Foreground = new SolidColorBrush(ColorPalette.Accent),
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(_volumeValue);

        _volumeBar = new DotSeekBar
        {
            Variant = ShellServices.Instance.Settings.Current.SliderStyle,
            Height = 26,
            ShowTimeHint = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _volumeBar.SeekRequested += fraction => SetVolume(fraction);
        root.Children.Add(_volumeBar);

        _volume.Changed += () => Dispatcher.BeginInvoke(SyncVolume);
        SyncVolume();

        // --- переключатели ---
        root.Children.Add(Section("БЫСТРО"));

        // Сетка 2×2 вместо WrapPanel: раньше «ЗВУК» переносился на вторую строку
        // один и висел под тремя — выглядело как ошибка вёрстки.
        var toggles = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        void AddToggle(int row, int column, UIElement chip)
        {
            var cell = new Border
            {
                Margin = new Thickness(0, 0, column == 0 ? 6 : 0, 6),
                Child = chip,
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            toggles.Children.Add(cell);
        }

        AddToggle(0, 0, Toggle("ТЁМНЫЕ ЗАГОЛОВКИ", () => ShellServices.Instance.Settings.Current.SystemStyleFull,
            value => ShellServices.Instance.Settings.Update(s => s.SystemStyleFull = value)));
        AddToggle(0, 1, Toggle("ИКОНКИ СТОЛА", () => ShellServices.Instance.Settings.Current.HideDesktopIcons,
            value => ShellServices.Instance.Settings.Update(s => s.HideDesktopIcons = value)));
        AddToggle(1, 0, Toggle("НЕ МЕШАТЬ", () => ShellServices.Instance.Settings.Current.QuietThreshold != "off",
            value => ShellServices.Instance.Settings.Update(s => s.QuietThreshold = value ? "maximized" : "off")));
        AddToggle(1, 1, Toggle("ЗВУК", () => !_volume.Muted, value => _volume.SetMute(!value)));

        root.Children.Add(toggles);

        // --- питание и настройки ---
        root.Children.Add(Section("ПИТАНИЕ"));

        var power = new WrapPanel();
        power.Children.Add(Chip("ЗАБЛОКИРОВАТЬ", () => { Win32.LockWorkStation(); Close(); }));
        power.Children.Add(Chip("СОН", () => { Win32.SetSuspendState(false, true, false); Close(); }));
        power.Children.Add(Chip("НАСТРОЙКИ", () => { Close(); OpenSettings(); }));
        power.Children.Add(Chip("ВЫЙТИ", () => Application.Current.Shutdown()));
        root.Children.Add(power);

        // --- яркость ---
        root.Children.Add(Section("ЯРКОСТЬ"));

        var brightnessValue = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 22,
            Foreground = new SolidColorBrush(ColorPalette.Accent),
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(brightnessValue);

        var brightnessBar = new DotSeekBar
        {
            Variant = ShellServices.Instance.Settings.Current.SliderStyle,
            Height = 26,
            ShowTimeHint = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        double initial = Math.Clamp(ShellServices.Instance.Settings.Current.ScreenBrightness, 0.3, 1.0);
        brightnessBar.Value = (initial - 0.3) / 0.7 * 100;
        brightnessValue.Text = $"{initial * 100:0}%";

        brightnessBar.SeekRequested += fraction =>
        {
            // 0 % ползунка = 30 % гаммы: полностью чёрный экран — не «яркость», а поломка.
            double value = 0.3 + Math.Clamp(fraction, 0, 1) * 0.7;
            DotShell.Core.Native.DisplayBrightness.Set(value);

            brightnessValue.Text = $"{value * 100:0}%";
            ShellServices.Instance.Settings.Update(s => s.ScreenBrightness = value);
        };
        root.Children.Add(brightnessBar);

        // Показатели системы: раньше центр управления показывал только громкость,
        // и «сколько RAM занято» приходилось смотреть в другом месте.
        root.Children.Add(Section("СИСТЕМА"));

        _clockLine = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 26,
            Foreground = new SolidColorBrush(ColorPalette.Accent),
            Margin = new Thickness(0, 0, 0, 2),
        };
        root.Children.Add(_clockLine);

        _statsLine = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 2),
        };
        root.Children.Add(_statsLine);

        _batteryLine = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        root.Children.Add(_batteryLine);

        var actions = new WrapPanel();
        actions.Children.Add(Chip("УВЕДОМЛЕНИЯ", () => OpenUri("ms-actioncenter:")));
        actions.Children.Add(Chip("ДИСПЕТЧЕР", () => OpenUri("taskmgr.exe")));
        root.Children.Add(actions);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();
        RefreshStats();

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        bool activated = false;
        Activated += (_, _) => activated = true;

        Deactivated += (_, _) =>
        {
            if (!activated) return;

            // Не закрываемся, если фокус ушёл к нашему же окну (док, виджет, остров):
            // иначе панель захлопывалась сразу после открытия, стоило доку получить
            // активацию. Закрываем только при уходе в другое приложение.
            if (ForegroundProcessId() == Environment.ProcessId) return;

            Close();
        };
        Closed += (_, _) =>
        {
            _statsTimer.Stop();

            // Гамму возвращаем, чтобы не оставить рабочий стол тёмным.
            DotShell.Core.Native.DisplayBrightness.Set(
                Math.Clamp(ShellServices.Instance.Settings.Current.ScreenBrightness, 0.3, 1.0));
            try { _volume.Dispose(); } catch { }
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    /// <summary>Часы и показатели системы раз в две секунды.</summary>
    private void RefreshStats()
    {
        if (_clockLine is null) return;

        var now = DateTime.Now;
        _clockLine.Text = now.ToString("HH:mm");

        var stats = ShellServices.Instance.Stats;

        if (_statsLine is not null)
        {
            double totalGb = stats.TotalMemoryMb / 1024.0;
            double usedGb = stats.UsedMemoryMb / 1024.0;

            _statsLine.Text = $"CPU {stats.Cpu:0}%   ·   RAM {usedGb:0,0} из {totalGb:0,0} ГБ";
        }

        if (_batteryLine is not null)
        {
            _batteryLine.Text = stats.HasBattery
                ? $"БАТАРЕЯ {(stats.Charging ? "+" : "")}{stats.Battery}%   ·   {now:dd.MM.dddd}"
                : $"ПИТАНИЕ ОТ СЕТИ   ·   {now:dd.MM.dddd}";
        }
    }

    private static void OpenUri(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void SyncVolume()
    {
        if (!_volume.Available)
        {
            _volumeValue.Text = "нет устройства";
            _volumeBar.IsEnabled = false;
            return;
        }

        _volumeBar.Value = _volume.Level * 100;
        _volumeValue.Text = $"{_volume.Level * 100:0}%";
    }

    private void SetVolume(double fraction)
    {
        try
        {
            _volume.SetLevel((float)Math.Clamp(fraction, 0, 1));
            _volumeValue.Text = $"{fraction * 100:0}%";
        }
        catch { }
    }

    private static void OpenSettings()
    {
        try
        {
            var island = IslandWindow.Current;
            if (island is not null) island.OpenSettings();
        }
        catch { }
    }

    // ---------------------------------------------------------------- мелочи

    private static TextBlock Section(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 9,
        Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66)),
        Margin = new Thickness(0, 14, 0, 6),
    };

    private static Border Chip(string text, Action action)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE)),
        };

        var border = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 6, 6),
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1A)),
            Child = label,
            Cursor = Cursors.Hand,
        };

        border.MouseLeftButtonUp += (_, _) => action();
        return border;
    }

    /// <summary>Переключатель: акцентная рамка и цвет текста, когда включён.</summary>
    private static Border Toggle(string text, Func<bool> read, Action<bool> write)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
        };

        var border = new Border
        {
            Padding = new Thickness(9, 7, 9, 7),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Child = label,
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

        void Render()
        {
            bool on = read();
            var accent = ColorPalette.Accent;

            border.BorderBrush = new SolidColorBrush(on ? accent : Color.FromRgb(0x26, 0x26, 0x2B));
            border.Background = new SolidColorBrush(on
                ? Color.FromArgb(0x22, accent.R, accent.G, accent.B)
                : Color.FromRgb(0x12, 0x12, 0x14));
            label.Foreground = new SolidColorBrush(on ? accent : Color.FromRgb(0x8A, 0x8A, 0x93));
        }

        border.MouseLeftButtonUp += (_, _) =>
        {
            write(!read());
            Render();
        };

        Render();
        return border;
    }

    private void SlideIn()
    {
        var from = SystemParameters.WorkArea.Right;
        Left = from;

        var slide = new DoubleAnimation(from - Width - 12, TimeSpan.FromMilliseconds(Motion.Ms(Motion.Slow)))
        {
            EasingFunction = Motion.Ease(),
        };

        BeginAnimation(LeftProperty, slide);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static uint ForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;

            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }
}
