namespace DotShell.Island.Wpf;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Time;
using DotShell.Core.Settings;
using DotShell.Core.Launcher;
using DotShell.Island.Wpf.Widgets;

public partial class IslandWindow
{
    // ==================================================================
    //  Режим «не мешать»
    // ==================================================================

    /// <summary>Ширина капсулы, когда в ней остаётся только юнит.</summary>
    private const double UnitOnlyWidth = 84;

    private const double QuietWidth = 260;
    private const double QuietHeight = 5;

    /// <summary>
    /// Проверить, не мешаем ли мы: если на переднем плане чужое развёрнутое окно,
    /// сжимаемся в тонкую полоску. Проверка дешёвая (одно окно + прямоугольник),
    /// поэтому её можно делать каждый тик.
    /// </summary>
    private IslandNotification? _notice;
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;
    private double _noticeSavedLeft;
    private double _noticeSavedWidth;

    /// <summary>
    /// Номер текущего показа. Анимация уведомления может идти, когда его уже
    /// спрятали (остров раскрыли или пришло новое) — по номеру она понимает,
    /// что её отменили, и не трогает ширину капсулы.
    /// </summary>
    private int _noticeGeneration;

    /// <summary>Живой остров: уведомления показываем в нём, если он на виду.</summary>
    public static IslandWindow? Current { get; private set; }

    /// <summary>
    /// Показать уведомление внутри капсулы.
    ///
    /// Возвращает false, если сейчас неудобно (остров раскрыт, утоплен или скрыт
    /// полноэкранным окном) — тогда вызывающий код покажет обычную карточку.
    ///
    /// Ширина: окно расширяем сразу (оно прозрачное, лишнее не видно), а капсула
    /// анимируется внутри. Так текст не дёргается, пока едет ширина.
    /// </summary>
    public bool ShowNotification(string title, string text)
    {
        PrepareTaskbarNotification();
        if (_isExpanded || _hiddenByFullscreen || _docked || _quiet) return false;
        if (_notice is null) return false;

        try
        {
            _noticeTimer?.Stop();

            double needed = _notice.MeasureWidth(title, text);
            // Даже короткое уведомление должно заметно раскрыть капсулу: иначе режим
            // «капсула» визуально не отличается от обычного свёрнутого острова.
            double target = Math.Clamp(needed + 120, CollapsedWidth + 100, 1100);

            _noticeSavedLeft = Left;
            _noticeSavedWidth = Width;

            // Окно сразу по максимальной ширине (оно прозрачное): капсуле будет куда
            // расти, и текст не дёрнется, пока едет ширина. Центр держим там, где
            // стоял остров: так уведомление не прыгает на другой монитор.
            Width = target + 40;
            double center = _noticeSavedLeft + _noticeSavedWidth / 2;
            double virtualLeft = SystemParameters.VirtualScreenLeft;
            double virtualWidth = SystemParameters.VirtualScreenWidth;
            Left = Math.Clamp(center - Width / 2, virtualLeft, virtualLeft + virtualWidth - Width);

            CollapsedRow.Visibility = Visibility.Collapsed;
            _notice.Prepare(title, text);
            _notice.SetVisibleWidth(UnitOnlyWidth);

            int generation = ++_noticeGeneration;
            _ = PlayNoticeAsync(target, generation);

            int seconds = Math.Clamp(ShellServices.Instance.Settings.Current.NotifySeconds, 3, 10);

            _noticeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds),
            };
            _noticeTimer.Tick += (_, _) =>
            {
                try { HideNotification(); }
                catch (Exception ex) { Log.Info($"notice timer hide failed: {ex.Message}"); }
            };
            _noticeTimer.Start();

            return true;
        }
        catch (Exception ex)
        {
            // Что-то не сложилось: возвращаем капсулу как была и отдаём показ карточке.
            try { HideNotification(); } catch (Exception hideEx) { Log.Info($"island notification rollback failed: {hideEx.Message}"); }
            Log.Info($"island notification failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Три шага: сжаться к юниту, раскрыть пасть, показать текст с ростом капсулы.
    /// Паузы — чтобы шаги читались глазом, а не сливались в один рывок.
    /// </summary>
    private async Task PlayNoticeAsync(double target, int generation)
    {
        try
        {
            await AnimateWidthAsync(UnitOnlyWidth, 300);
            if (generation != _noticeGeneration) return;
            await Task.Delay(140);
            if (generation != _noticeGeneration) return;

            _notice?.StartReading();
            await Task.Delay(200);
            if (generation != _noticeGeneration) return;

            await AnimateWidthAsync(target, 460);
        }
        catch (Exception ex)
        {
            Log.Info($"notice animation failed: {ex.Message}");
        }
    }

    /// <summary>Анимация ширины капсулы с ожиданием завершения.</summary>
    private Task AnimateWidthAsync(double width, int milliseconds)
    {
        var done = new TaskCompletionSource();

        var animation = new DoubleAnimation(width, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) => done.TrySetResult();

        Pill.BeginAnimation(FrameworkElement.WidthProperty, animation);
        return done.Task;
    }
    private async void HideNotification()
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;
        ++_noticeGeneration;

        if (_notice is null) return;

        // Если остров уже раскрыли (хоткей, трей), таймер уведомления не имеет права
        // сжимать капсулу: иначе содержимое обрезалось, а следующий клик «сворачивал»
        // вместо раскрытия. Убираем только слой уведомления.
        //
        // ВАЖНО: сам контрол не обнуляем. Раньше здесь стоял `_notice = null`, и после
        // первого раскрытия острова уведомления больше никогда не показывались в капсуле —
        // все уходили в карточку до перезапуска программы.
        if (_isExpanded)
        {
            try { _notice.Hide(); } catch (Exception ex) { Log.Info($"notice hide (expanded) failed: {ex.Message}"); }
            return;
        }

        try
        {
            // Обратный ход: текст гаснет, пасть закрывается, капсула снова сжимается
            // к юниту и только потом возвращается обычное содержимое.
            _notice.HideText();
            await Task.Delay(180);

            _notice.StopReading();
            await AnimateWidthAsync(UnitOnlyWidth, 260);
            await Task.Delay(100);

            _notice.Hide();

            Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            Pill.Width = CollapsedWidth;
            CollapsedRow.Visibility = Visibility.Visible;

            // Возвращаем окно штатным путём: он сам центрирует капсулу и знает про
            // рабочую область. Ручные Width/Left давали уезд вправо на 600 px.
            ResizeWindowTo(CollapsedWidth, CollapsedHeight);
        }
        catch (Exception ex)
        {
            Log.Info($"notice hide failed: {ex.Message}");
            try
            {
                Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
                Pill.Width = CollapsedWidth;
                CollapsedRow.Visibility = Visibility.Visible;
                ResizeWindowTo(CollapsedWidth, CollapsedHeight);
            }
            catch (Exception restoreEx)
            {
                Log.Info($"notice hide recovery failed: {restoreEx.Message}");
            }
        }

        FinishTaskbarNotification();
    }
    private void UpdateQuietMode()
    {
        // Порог: только развёрнутые окна, любые крупные, или совсем выкл.
        string threshold = _services.Settings.Current.QuietThreshold;

        if (threshold == "off")
        {
            if (_quiet) ExitQuiet();
            return;
        }

        if (_isExpanded || _hiddenByFullscreen || _docked)
        {
            if (_quiet) ExitQuiet();
            return;
        }

        // Мы сами или наша «ручка» под курсором — не сжимаемся, иначе остров
        // будет уворачиваться от собственной мыши.
        if (IsMouseOver || _quietRestoreTimer?.IsEnabled == true) return;

        bool busy = threshold == "any"
            ? DotShell.Core.Windowing.ForegroundProbe.IsForegroundCovering()
            : DotShell.Core.Windowing.ForegroundProbe.IsForegroundMaximized();
        if (busy && !_quiet) EnterQuiet();
        else if (!busy && _quiet) ExitQuiet();
    }

    private void EnterQuiet()
    {
        _quiet = true;

        CollapsedRow.Visibility = Visibility.Collapsed;
        MiniEq.Visibility = Visibility.Collapsed;
        QuietLine.Visibility = Visibility.Visible;

        // В тихом режиме капсула становится почти прямоугольной, но не спорит с
        // настройкой: если радиус меньше 3, оставляем настройку.
        double quietRadius = Math.Min(ShellServices.Instance.Settings.Current.IslandRadius, 3);
        Pill.CornerRadius = new CornerRadius(quietRadius);
        Pill.Opacity = 0.9;

        // Раньше окно и капсула прыгали в новый размер одним кадром. Теперь то же
        // движение, что у раскрытия: капсула сжимается в тонкую полоску плавно.
        AnimateQuietShellTo(QuietWidth, QuietHeight);
    }

    private void ExitQuiet()
    {
        _quiet = false;

        CollapsedRow.Visibility = Visibility.Visible;
        MiniEq.Visibility = Visibility.Visible;
        QuietLine.Visibility = Visibility.Collapsed;

        Pill.Opacity = 1;

        // Возвращаем геометрию из настроек, а не «как было зашито»: раньше здесь
        // стоял радиус 23 и высота 46, и после выхода из «не мешать» остров
        // переставал соответствовать настройкам (радиус 4 «прыгал» на 23).
        ApplyCapsuleGeometry();
        AnimateShellTo(CollapsedWidth, Pill.Height);
    }

    /// <summary>
    /// Тихое состояние — не обычная капсула: окно тоньше (QuietHeight + 26), поэтому
    /// AnimateShellTo не подходит. Центр по горизонтали сохраняем, а не считаем от
    /// основного монитора: остров может жить на втором экране.
    /// </summary>
    private void AnimateQuietShellTo(double pillWidth, double pillHeight)
    {
        const int ms = 220;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        double winW = pillWidth + 40;
        double winH = pillHeight + 26;
        double centerX = Left + Width / 2;
        double targetLeft = centerX - winW / 2;

        AnimateTo(this, WidthProperty, winW, ms, ease);
        AnimateTo(this, HeightProperty, winH, ms, ease);
        AnimateTo(this, LeftProperty, targetLeft, ms, ease);
        AnimateTo(this, TopProperty, EdgeTop(winH), ms, ease);
        AnimateTo(Pill, WidthProperty, pillWidth, ms, ease);
        AnimateTo(Pill, HeightProperty, pillHeight, ms, ease);
    }

    /// <summary>Наведение на полоску: показать остров и не сжиматься, пока курсор рядом.</summary>
    private void OnQuietHover()
    {
        if (!_quiet) return;

        ExitQuiet();

        _quietRestoreTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _quietRestoreTimer.Stop();
        _quietRestoreTimer.Tick -= OnQuietRestoreTick;
        _quietRestoreTimer.Tick += OnQuietRestoreTick;
        _quietRestoreTimer.Start();
    }

    private void OnQuietRestoreTick(object? sender, EventArgs e)
    {
        _quietRestoreTimer?.Stop();
        if (IsMouseOver || _isExpanded) return;
        if (DotShell.Core.Windowing.ForegroundProbe.IsForegroundMaximized()) EnterQuiet();
    }
}
