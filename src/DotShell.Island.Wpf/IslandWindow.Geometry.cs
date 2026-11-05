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
    /// <summary>
    /// Где должно быть окно по вертикали для текущей кромки. Для верхней — сохранённая
    /// «нормаль», для нижней — отсчёт от низа рабочей области. Раньше всё считалось от
    /// _normalTop, и на нижней кромке остров при каждом сворачивании прыгал наверх.
    /// </summary>
    private double EdgeTop(double windowHeight)
    {
        var s = ShellServices.Instance.Settings.Current;
        double offset = Math.Clamp(s.IslandTopOffset, 0, 24);

        if (string.Equals(s.IslandEdge, "bottom", StringComparison.OrdinalIgnoreCase))
        {
            var work = SystemParameters.WorkArea;
            return work.Bottom - windowHeight - offset;
        }

        return _normalTop;
    }

    /// <summary>Центрируем таблетку по верхнему краю рабочей области основного монитора.</summary>
    private void PositionAtTopCenter()
    {
        var wa = SystemParameters.WorkArea; // экран минус таскбар, в WPF-единицах
        var s = _services.Settings.Current;

        // Восстанавливаем последнее положение: обычное или "утоплен за кромку".
        if (s.IslandLeft > -30000 && s.IslandTop > -30000)
        {
            Left = s.IslandLeft;
            Top = s.IslandTop;
        }
        else
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top - 4; // чуть наезжаем на самую кромку, как настоящий Dynamic Island
        }

        if (Top <= wa.Top - 34)
        {
            // Стартовали сразу в доке: "нормаль" для выглядывания — центр кромки.
            _normalLeft = Left;
            _normalTop = wa.Top - 4;
        }
        else
        {
            _normalLeft = Left;
            _normalTop = Top;
        }
        EvaluateDock(instant: true);
    }

    // ================================================================
    //  Докинг у кромки экрана: виден только эквалайзер, наведение = выглянуть
    // ================================================================

    /// <summary>
    /// После перетаскивания решаем, не утопил ли пользователь остров за верхнюю
    /// кромку экрана. Если да — включаем режим дока: таблетка прячется за экран,
    /// снаружи остаются только сталактиты эквалайзера и невидимая зона наведения.
    /// </summary>
    private void EvaluateDock(bool instant = false)
    {
        var wa = SystemParameters.WorkArea;
        _dockTop = wa.Top - 54; // таблетка (10+46) вся за кромкой, сталктиты (54..98) наружу

        if (Top <= wa.Top - 34)
        {
            if (!_docked)
            {
                _docked = true;
                _peeked = false;
            }
            DockHit.Visibility = Visibility.Visible;
            if (instant) Top = _dockTop;
            else if (!_peeked) AnimateTop(_dockTop);
        }
        else
        {
            _docked = false;
            _peeked = false;
            DockHit.Visibility = Visibility.Collapsed;
            _normalLeft = Left;
            _normalTop = Top;
        }
        SavePosition();
    }

    /// <summary>Плавно высунуть остров из-за кромки (наведение мыши).</summary>
    private void PeekIn()
    {
        if (!_docked || _peeked) return;
        _peeked = true;
        DockHit.Visibility = Visibility.Collapsed; // вся таблетка видна и кликабельна сама
        AnimateTop(_normalTop);
    }

    /// <summary>Плавно уйти обратно за кромку, оставив только эквалайзер.</summary>
    private void PeekOut()
    {
        if (!_docked || !_peeked) return;
        _peeked = false;
        DockHit.Visibility = Visibility.Visible;
        AnimateTop(_dockTop);
    }

    /// <summary>
    /// MouseLeave во время скольжения обманчив: курсор мог просто остаться на месте,
    /// пока остров уезжает из-под него. Даём 200 мс форы и перепроверяем.
    /// </summary>
    private void PeekOutDelayed()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_docked && _peeked && !_isExpanded && !RootGrid.IsMouseOver) PeekOut();
        };
        timer.Start();
    }

    private void AnimateTop(double to, Action? onDone = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            // Критично: без снятия анимации её "замороженное" значение продолжает
            // переопределять Top, и прямые присваивания (перетаскивание окна)
            // молча игнорируются — остров становится неуправляемым.
            BeginAnimation(TopProperty, null);
            Top = to;
            onDone?.Invoke();
        };
        BeginAnimation(TopProperty, anim);
    }

    /// <summary>
    /// Аварийный люк: вернуть остров в центр верхнего края и выключить док.
    /// Вызывается из трея ("Показать остров"), когда остров утащили так,
    /// что его не найти ни мышью, ни здравым смыслом.
    /// </summary>
    /// <summary>Разрешение или набор мониторов изменились — вернуть остров в кадр.</summary>
    private void OnDisplayChanged()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            _dockTop = wa.Top - 54;

            if (_docked)
            {
                // В доке остров привязан к кромке — ставим её заново.
                Top = _dockTop;
                Left = Math.Clamp(Left, wa.Left - 40, wa.Right - Width + 40);
            }
            else
            {
                // Стоял по центру — держим центр и после смены режима.
                bool centered = Math.Abs(Left + Width / 2 - (wa.Left + wa.Width / 2)) < 80;
                if (centered) Left = wa.Left + (wa.Width - Width) / 2;
                if (Top < wa.Top - 34) Top = wa.Top - 4;
            }

            ClampToScreen();
            EvaluateDock(instant: true);
            SavePosition();
            Log.Info("display changed: island repositioned");
        }
        catch (Exception ex) { Log.Info($"display change failed: {ex.Message}"); }
    }

    public void ResetPosition()
    {
        BeginAnimation(TopProperty, null);
        _docked = false;
        _peeked = false;
        DockHit.Visibility = Visibility.Collapsed;

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top - 4;
        _normalLeft = Left;
        _normalTop = Top;
        SavePosition();
        Log.Info("position reset from tray");
    }

    /// <summary>
    /// Страховка от "утащил и потерял": после перетаскивания хотя бы кусок острова
    /// (80 px по горизонтали, полоска дока по вертикали) обязан остаться на экране.
    /// </summary>
    private void ClampToScreen()
    {
        // Клампим по ВИРТУАЛЬНОМУ экрану, а не по основному монитору: иначе остров,
        // перенесённый на второй монитор, после отпускания мыши утаскивало назад.
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        Left = Math.Clamp(Left, vLeft - Width + 80, vRight - 80);

        // Нижняя граница с запасом на док: верхнюю оставляем с минусом, там живёт
        // «утоплен за кромку» режим.
        double waTop = SystemParameters.WorkArea.Top;
        Top = Math.Clamp(Top, Math.Min(waTop, vTop) - 54, vBottom - 60);
    }

    /// <summary>Положение переживает перезапуск: пишем в settings.json.</summary>
    private void SavePosition()
    {
        // Сохраняем положение в «свёрткой» системе координат: окно растёт под
        // вкладку, и без нормализации остров уезжал бы влево на каждом старте
        // (в настройках лежала бы ширина вкладки, а старт — со свёрнутой).
        var l = (int)(Left + (Width - CollapsedWidth) / 2);
        var t = (int)Top;
        _services.Settings.Update(s =>
        {
            s.IslandLeft = l;
            s.IslandTop = t;
        });
    }

    /// <summary>
    /// Подогнать окно под размер таблетки, сохранив её центр на экране.
    ///
    /// Окно острова — это буквально таблетка: прозрачных «полей» быть не должно,
    /// иначе они начнут ловить клики мимо. Поэтому при смене вкладки окно
    /// меняет размер, а не просто даёт контенту обрезаться.
    /// </summary>
    private void ResizeWindowTo(double pillWidth, double pillHeight)
    {
        double width = Math.Max(CollapsedWidth, pillWidth);
        double height = Math.Max(CollapsedHeight + 56, pillHeight + 12);

        if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1) return;

        double centerX = Left + Width / 2;
        Width = width;
        Height = height;
        Left = centerX - width / 2;

        // Нижняя кромка: остров растёт вверх, поэтому при изменении высоты
        // пересчитываем Top от низа рабочей области.
        if (ShellServices.Instance.Settings.Current.IslandEdge == "bottom")
        {
            var wa = SystemParameters.WorkArea;
            Top = wa.Bottom - height - Math.Clamp(ShellServices.Instance.Settings.Current.IslandTopOffset, 0, 24);
        }
    }

    private void OnDockHitClick(object sender, MouseButtonEventArgs e)
    {
        PeekIn();
        Expand();
    }
}
