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
    // ================================================================
    //  Клик и перетаскивание таблетки
    // ================================================================

    /// <summary>
    /// Клик по "пустому" месту таблетки = раскрыть/свернуть.
    /// Клик по кнопке, вкладке, полю ввода и т.п. игнорируем — у них своё поведение.
    /// </summary>
    // Чем был нажат пилюль: кнопкой внутри или пустым местом. Проверять это
    // только на MouseUp нельзя: обработчик кнопки (например, смена темы) успевает
    // пересобрать содержимое, элемент отваливается от дерева, IsInteractive
    // возвращает false — и остров сворачивался вместо действия кнопки.
    private bool _pressWasInteractive;

    /// <summary>После двойного клика (Down свернул) Up не должен раскрывать обратно.</summary>
    private bool _suppressNextPillClick;

    private void OnPillClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragMoved) return;              // это было перетаскивание

        // Up после двойного Down: остров уже свёрнут, раскрывать обратно нельзя.
        // ClickCount у Up почти всегда 1, поэтому опираемся на флаг, а не на счётчик.
        if (_suppressNextPillClick)
        {
            _suppressNextPillClick = false;
            return;
        }


        if (_pressWasInteractive)
        {
            _pressWasInteractive = false;
            return;
        }

        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        // Раскрытый остров: клик по его содержимому НЕ сворачивает капсулу.
        // Раньше промах мимо ползунка попадал сюда и сворачивал остров — человек
        // повторял попытку пять раз, прежде чем попадал в дорожку.
        if (_isExpanded && ExpandedLayer.Visibility == Visibility.Visible)
        {
            var point = e.GetPosition(ExpandedLayer);
            if (point.X >= 0 && point.Y >= 0
                && point.X <= ExpandedLayer.ActualWidth && point.Y <= ExpandedLayer.ActualHeight)
            {
                return;
            }
        }

        // Действие клика — настройка: раскрыть, лаунчер или панель-обзор.
        switch (ShellServices.Instance.Settings.Current.ClickAction)
        {
            case "launcher":
                _ = Dispatcher.BeginInvoke(LauncherHost.Toggle);
                break;
            case "dashboard":
                _ = Dispatcher.BeginInvoke(() => DashboardWindow.Toggle());
                break;
            default:
                ToggleExpanded();
                break;
        }
    }

    /// <summary>
    /// Двойной клик по пустому месту раскрытого острова сворачивает его.
    ///
    /// Ловим на MouseDown: у MouseUp ClickCount почти всегда 1, поэтому проверка
    /// в OnPillClick не срабатывала (проверено тестом — остров оставался раскрытым).
    /// </summary>
    private void OnPillDoubleDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isExpanded) return;
        if (e.ClickCount < 2) return;
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

        _suppressNextPillClick = true;
        Collapse();
        e.Handled = true;
    }

    private void OnPillRightClick(object sender, MouseButtonEventArgs e)
    {
        MenuAutostart.IsChecked = _vm.RunAtStartup;
        CoverAccentCheck.IsChecked = _services.Settings.Current.PlayerAccentFromCover; // синхронизируем галочку перед показом меню
    }

    private void OnDragCandidateDown(object sender, MouseButtonEventArgs e)
    {
        _pressWasInteractive = IsInteractive(e.OriginalSource as DependencyObject);
        if (_pressWasInteractive) { _dragArmed = false; return; }

        _dragArmed = true;
        _dragMoved = false;
        // Если Top/Left ещё держит завершённая анимация, прямые присваивания
        // в OnDragMove будут проигнорированы — снимаем её до начала драга.
        BeginAnimation(TopProperty, null);
        BeginAnimation(LeftProperty, null);
        _dragScreenStart = PointToScreen(e.GetPosition(this));
        _dragWindowOrigin = new Point(Left, Top);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = PointToScreen(e.GetPosition(this));
        var dx = pos.X - _dragScreenStart.X;
        var dy = pos.Y - _dragScreenStart.Y;

        // Порог в пару пикселей: дрожание руки не должно превращать клик в драг.
        if (!_dragMoved && Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;

        _dragMoved = true;
        Left = _dragWindowOrigin.X + dx;
        Top = _dragWindowOrigin.Y + dy;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragArmed) return;
        _dragArmed = false;
        Mouse.Capture(null);

        if (_dragMoved)
        {
            // Отпустили после перетаскивания: не даём острову улететь за экран
            // целиком (иначе его не достать), запоминаем позицию и проверяем док.
            ClampToScreen();
            SavePosition();
            EvaluateDock();
            // После драга клик-хендлер (OnPillClick) сработает следом —
            // он увидит _dragMoved и промолчит. Сбрасываем флаг на следующем тике.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => _dragMoved = false);
        }
    }

    /// <summary>
    /// Поднимаемся от источника события вверх по дереву до таблетки и смотрим,
    /// не попал ли клик в интерактивный контрол. Так один обработчик на всю
    /// таблетку заменяет десяток подписок на отдельные кнопки.
    /// </summary>
    private bool IsInteractive(DependencyObject? source)
    {
        var node = source;
        while (node is not null && !ReferenceEquals(node, Pill))
        {
            switch (node)
            {
                // Юнит — тоже интерактивный: клик по нему гладит персонажа,
                // а не сворачивает остров.
                case ButtonBase or TextBox or Slider or Selector or ScrollBar or Controls.UnitCharacter:
                    return true;
            }
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
}
