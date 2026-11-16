namespace DotShell.Island.Wpf.Taskbar;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Уведомление-капсула над сегментом панели задач.
///
/// Зачем отдельное окно: когда остров раскрыт, капсула занята панелью, и
/// уведомление раньше улетало в отдельную карточку в углу экрана. Владелец
/// справедливо спросил «почему улетели». Теперь уведомление всегда остаётся
/// у острова: маленькая капсула-тост появляется над сегментом панели, в том же
/// языке (точки, юнит, текст), и гаснет сама.
/// </summary>
internal static class TaskbarNoticeWindow
{
    private static Window? _window;
    private static DispatcherTimer? _timer;

    public static bool Show(double centerX, double taskbarTopDip, string title, string message)
    {
        try
        {
            Close();

            var notice = new IslandNotification
            {
                Height = 46,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            notice.Prepare(title, message);

            double width = Math.Clamp(notice.MeasureWidth(title, message) + 150, 340, 860);
            notice.SetVisibleWidth(width);
            notice.StartReading();

            var border = new Border
            {
                CornerRadius = new CornerRadius(23),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0xF7, 0x0B, 0x0B, 0x0E)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)),
                Child = notice,
                ClipToBounds = true,
            };

            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Manual,
                Width = width,
                Height = 50,
                Content = border,
                Left = centerX - width / 2,
                Top = Math.Max(8, taskbarTopDip - 58),
            };

            window.MouseLeftButtonDown += (_, _) => Close();
            _window = window;
            window.Show();
            window.Activate(); // не для фокуса, а чтобы быть поверх панели; ShowActivated=false
            window.Owner = null;

            int seconds = Math.Clamp(ShellServices.Instance.Settings.Current.NotifySeconds, 3, 10);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            _timer.Tick += (_, _) => Close();
            _timer.Start();

            return true;
        }
        catch
        {
            Close();
            return false;
        }
    }

    public static void Close()
    {
        try { _timer?.Stop(); } catch { }
        _timer = null;

        try
        {
            if (_window is { IsLoaded: true }) _window.Close();
            else _window?.Close();
        }
        catch { }
        _window = null;
    }
}
