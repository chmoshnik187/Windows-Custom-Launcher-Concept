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
    private void UpdateNotesHeight(bool animate = true)
    {
        if (!_isExpanded || _vm.ActiveTab != "notes") return;

        NotesScroll.UpdateLayout();
        double viewport = NotesScroll.ViewportHeight;
        double extent = NotesScroll.ExtentHeight;
        if (viewport <= 0 || extent <= 0) return;

        double chrome = Height - viewport;          // обвязка вокруг колонки
        double baseHeight = TabMinHeight("notes");
        const double Minimal = 420;                 // ниже не сжимаем: станет некрасиво

        double target = Math.Clamp(chrome + extent + 2, Minimal, MaxExpandedHeight());
        double extra = target - baseHeight;

        if (Math.Abs(extra - _notesExtra) < 6) return;

        _notesExtra = extra;

        // Рост заметок тоже анимируем: иначе остров «прыгал» в момент, когда
        // вкладка пересчитывала высоту под содержимое.
        ApplyTabSize("notes", animate: true);
        Log.Info($"notes: содержимое {extent:F0}, видно {viewport:F0}, обвязка {chrome:F0} → высота {target:F0}");
    }
    private void SetExpandedHeight(double h, bool animate)
    {
        _expandedH = h;
        ExpandedLayer.Height = h - 10;
        // Окно обязано расти вместе с таблеткой: раньше менялись только
        // таблетка и слой, а окно оставалось прежним — всё ниже края
        // (в чате это поле ввода) обрезалось.
        ResizeWindowTo(Math.Max(CollapsedWidth, Width), h);
        if (animate) Animate(Pill, HeightProperty, h);
        else Pill.Height = h;
    }

    /// <summary>
    /// Перевести остров в размер конкретной вкладки. Ширина и высота едут
    /// вместе: иначе получилось бы «широкое окно с узкой колонкой» — ровно
    /// то, на что жаловался владелец.
    /// </summary>

    /// <summary>
    /// Окно и капсула одним движением. Раньше окно получало новый размер сразу,
    /// а капсула догоняла его анимацией — из-за этого на раскрытии и на смене
    /// вкладок виднелась полоса «широкое окно с узкой капсулой».
    /// </summary>
    private void AnimateShellTo(double width, double height, int ms = 260)
    {
        double winW = Math.Max(CollapsedWidth, width);
        double winH = Math.Max(CollapsedHeight + 56, height + 12);
        double centerX = Left + Width / 2;

        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        AnimateTo(this, WidthProperty, winW, ms, ease);
        AnimateTo(this, HeightProperty, winH, ms, ease);
        AnimateTo(this, LeftProperty, centerX - winW / 2, ms, ease);

        // Нижняя кромка: окно растёт вверх, значит Top обязан уезжать вверх вместе с
        // высотой. Раньше Top оставался от свёрнутого состояния и раскрытый остров
        // целиком уходил под панель задач.
        if (string.Equals(ShellServices.Instance.Settings.Current.IslandEdge, "bottom", StringComparison.OrdinalIgnoreCase))
        {
            var work = SystemParameters.WorkArea;
            double offset = Math.Clamp(ShellServices.Instance.Settings.Current.IslandTopOffset, 0, 24);
            AnimateTo(this, TopProperty, work.Bottom - winH - offset, ms, ease);
        }
        AnimateTo(Pill, WidthProperty, width, ms, ease);
        AnimateTo(Pill, HeightProperty, height, ms, ease);
    }

    private void ApplyTabSize(string tag, bool animate = true)
    {
        if (!_isExpanded) return;

        var (width, height) = TargetTabSize(tag);

        double contentW = width - 20;       // по 10 полей слева и справа
        double contentH = height - 10;
        double winW = Math.Max(CollapsedWidth, width);
        double winH = Math.Max(CollapsedHeight + 56, height + 12);

        if (!animate)
        {
            ResizeWindowTo(width, height);
            ExpandedLayer.Width = contentW;
            ExpandedLayer.Height = contentH;
            _expandedH = height;
            Pill.Width = width;
            Pill.Height = height;
            return;
        }

        // Окно, капсула и слой едут одним движением. Раньше окно и слой прыгали
        // сразу, а капсула догоняла их анимацией — отсюда рваный переход между
        // вкладками: на миг виднелось «широкое окно с узкой колонкой».
        const int ms = 260;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        double centerX = Left + Width / 2;

        AnimateTo(this, WidthProperty, winW, ms, ease);
        AnimateTo(this, HeightProperty, winH, ms, ease);
        AnimateTo(this, LeftProperty, centerX - winW / 2, ms, ease);

        AnimateTo(Pill, WidthProperty, width, ms, ease);
        AnimateTo(Pill, HeightProperty, height, ms, ease);

        AnimateTo(ExpandedLayer, WidthProperty, contentW, ms, ease);
        AnimateTo(ExpandedLayer, HeightProperty, contentH, ms, ease);

        _expandedH = height;
    }

    /// <summary>
    /// Анимация свойства с фиксацией конечного значения: без сброса анимации
    /// следующая мгновенная установка размера (ResizeWindowTo) игнорировалась бы —
    /// анимация приоритетнее локального значения.
    /// </summary>
    private static void AnimateTo(DependencyObject target, DependencyProperty property,
        double to, int ms, IEasingFunction ease)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease };

        if (target is UIElement element)
        {
            anim.Completed += (_, _) =>
            {
                element.BeginAnimation(property, null);
                element.SetValue(property, to);
            };
        }

        if (target is UIElement el)
        {
            el.BeginAnimation(property, anim);
        }
        else if (target is Window window)
        {
            // Окну анимацию тоже НАДО снимать по завершении: иначе её замороженное
            // значение продолжает переопределять Width/Height/Left, и прямые
            // присваивания игнорируются — окно перестаёт расти под содержимое
            // (обрезался чат, уведомления, «Показать остров» не возвращал позицию).
            anim.Completed += (_, _) =>
            {
                window.BeginAnimation(property, null);
                window.SetValue(property, to);
            };

            window.BeginAnimation(property, anim);
        }
    }

    /// <summary>Размер вкладки с поправкой на нижний край экрана.</summary>
    private (double Width, double Height) TargetTabSize(string tag)
    {
        var (width, height) = TabSizes.TryGetValue(tag, out var size)
            ? size
            : (ExpandedWidth, ExpandedHeight);

        // Высокую вкладку не выпускаем за нижний край экрана.
            // Заметки растут под содержимое: добавка считается по фактическому
            // дефициту места в списке задач и напоминаний.
            if (tag == "notes") height = Math.Max(420, height + _notesExtra);

        return (width, Math.Min(height, MaxExpandedHeight()));
    }

    private double TabMinHeight(string tag) =>
        TabSizes.TryGetValue(tag, out var size) ? size.Height : ExpandedHeight;

    /// <summary>
    /// Чат растёт вместе с перепиской: остров начинается компактным и вытягивается
    /// вниз по мере реплик — до нижнего края рабочей области. Раньше высота чата
    /// задавалась один раз при раскрытии, и длинная переписка жило в крошечном
    /// окошке или в пустой простыне — оба варианта бесили владельца.
    /// </summary>
    private double MaxExpandedHeight()
    {
        var wa = SystemParameters.WorkArea;

        // Нижняя кромка: остров растёт вверх, поэтому запас — это место над ним
        // (от верхнего края рабочей области до его низа), а не под ним. Раньше
        // здесь всегда считалось «место под Top», и на нижней кромке раскрытие
        // ужималось до минимальных 306 DIP — главная вкладка обрезалась.
        bool bottom = string.Equals(
            ShellServices.Instance.Settings.Current.IslandEdge, "bottom", StringComparison.OrdinalIgnoreCase);

        double headroom = bottom
            ? (wa.Bottom - wa.Top) - Math.Clamp(ShellServices.Instance.Settings.Current.IslandTopOffset, 0, 24) - 10
            : wa.Bottom - 16 - (Top + 10);

        return Math.Clamp(headroom, ExpandedHeight, 1400);
    }

    private void ScheduleGrowChat()
    {
        if (!_isExpanded || _vm.ActiveTab != "chat") return;
        _growTimer.Stop();
        _growTimer.Start();
    }

    private void GrowChat()
    {
        if (!_isExpanded || _vm.ActiveTab != "chat" || ChatScroller is null) return;
        if (ChatScroller.Content is not FrameworkElement content) return;

        ChatScroller.UpdateLayout();
        double width = ChatScroller.ViewportWidth > 10 ? ChatScroller.ViewportWidth : 560;
        content.Measure(new Size(width, double.PositiveInfinity));
        double needed = content.DesiredSize.Height;

        // "Хром" вокруг скроллера (плеер, вкладки, настройка, ввод, нижняя строка)
        // не зависит от высоты острова — по нему и пересчитываем полную высоту.
        double chrome = _expandedH - ChatScroller.ViewportHeight;

        // Потолок роста: 62% рабочей области. Без него переписка вытягивает остров
        // почти на весь экран — проверено: шесть реплик точками дали окно 1094 px
        // при экране 1152 px. Дальше пусть работает прокрутка, а не само окно.
        double share = SystemParameters.WorkArea.Height * 0.62;
        double ceiling = Math.Min(MaxExpandedHeight(), Math.Max(TabMinHeight("chat"), share));

        double target = Math.Clamp(needed + chrome + 6, TabMinHeight("chat"), ceiling);
        if (Math.Abs(target - _expandedH) <= 2) return;

        SetExpandedHeight(target, animate: true);

        // Страховка: окно обязано соответствовать новой высоте, иначе
        // содержимое чата рисуется за краем, а поле ввода недосягаемо.
        ResizeWindowTo(Math.Max(CollapsedWidth, Width), target);
    }

    private static void Animate(FrameworkElement el, DependencyProperty prop, double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(AnimationMs))
        {
            // Кривая и скорость берутся из настроек: раньше они были зашиты, и
            // «пружины» с «линейно» владелец выбрать не мог.
            EasingFunction = Ease(),
        };
        el.BeginAnimation(prop, anim);
    }

    private static void Fade(UIElement el, double to, int ms, int delayMs = 0, Action? onComplete = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
        if (onComplete is not null)
            anim.Completed += (_, _) => onComplete();
        el.BeginAnimation(OpacityProperty, anim);
    }

    // ================================================================
    //  Раскрытие / свёртывание
    // ================================================================

    private void ToggleExpanded()
    {
        if (_isExpanded) Collapse(); else Expand();
    }

    private void Expand()
    {
        if (_isExpanded) return;
        _isExpanded = true;
        _vm.IsExpanded = true;

        if (_docked && !_peeked) PeekIn(); // раскрытие из дока сначала вытаскивает остров

        var (targetW, targetH) = TargetTabSize(_vm.ActiveTab);
        ExpandedLayer.Width = targetW - 20;
        _expandedH = targetH;
        ExpandedLayer.Height = targetH - 10;
        ExpandedLayer.Visibility = Visibility.Visible;

        AnimateShellTo(targetW, targetH);
        Fade(CollapsedRow, to: 0, ms: 120, onComplete: () => CollapsedRow.Visibility = Visibility.Collapsed);
        Fade(ExpandedLayer, to: 1, ms: 220, delayMs: 90);

        Units.SetPaused(true); // юниты видны только в свёрнутой таблетке
        UpdateMiniEq();
        if (_vm.ActiveTab == "chat") ScheduleGrowChat();
        _vm.Tick();

        // Автоскрытие: если включено, раскрытый остров уходит сам через 30 секунд.
        _autoHideTimer?.Stop();

        if (ShellServices.Instance.Settings.Current.AutoHide == "timer")
        {
            // Обработчик подписывается ОДИН раз в конструкторе. Раньше += стоял здесь,
            // и каждый Expand добавлял ещё одну подписку: таймер срабатывал N раз за
            // тик, а старые обработчики держали окно в памяти.
            _autoHideTimer ??= CreateAutoHideTimer();
            _autoHideTimer.Start();
        }
    }

    private void Collapse()
    {
        if (!_isExpanded) return;
        _isExpanded = false;
        _vm.IsExpanded = false;

        CollapsedRow.Visibility = Visibility.Visible;

        AnimateShellTo(CollapsedWidth, CollapsedHeight);
        Fade(ExpandedLayer, to: 0, ms: 120, onComplete: () => ExpandedLayer.Visibility = Visibility.Collapsed);
        Fade(CollapsedRow, to: 1, ms: 220, delayMs: 90);

        Units.SetPaused(false);
        UpdateMiniEq();
        if (_docked && _peeked) PeekOut();
    }

    private void ExpandAndGoToTab(string tag)
    {
        // Неизвестный тег (например, `--tab=media`: такой вкладки нет) раньше доезжал
        // до ApplyTabSize, размер брался по умолчанию, и домашняя страница обрезалась
        // поперёк плиток — выглядело как ряд пустых прямоугольников.
        if (!TabSizes.ContainsKey(tag)) tag = "home";

        Expand();

        // Вкладки лежат внутри StackPanel, а не прямо в TabsRow: раньше здесь
        // стоял Cast<RadioButton>, и «раскрыть на вкладке» падало
        // InvalidCastException (ловилось только при броске файла на остров).
        foreach (var button in FindDescendants<RadioButton>(TabsRow))
        {
            if (button.Tag is string t && t == tag)
            {
                button.IsChecked = true;
                break;
            }
        }

        // RadioButton могла уже быть нажата (тогда Checked не выстрелит) —
        // поэтому состояние вкладок доводим руками, а не надеемся на событие.
        _vm.ActiveTab = tag;
        ShowTab(tag);
        ApplyTabSize(tag);
    }
    /// <summary>Высота раскрытия задаётся одним местом: слой и таблетка вместе.</summary>
    /// <summary>
    /// Подогнать высоту острова под содержимое заметок: сначала растём, пока есть
    /// место на экране, и только потом появляется прокрутка. Раньше высота была
    /// константой, и список задач просто обрезался — на это и жаловался владелец.
    /// </summary>
    /// <summary>
    /// Высота вкладки заметок подстраивается под содержимое в обе стороны.
    ///
    /// Раньше считалась только «добавка» к базовым 520: с одной задачей остров
    /// оставался высоким с пустотой снизу. Теперь целевая высота = «обвязка»
    /// (плеер, вкладки, отступы) + высота содержимого, где обвязка измеряется как
    /// Height − viewport — эта разница не меняется при ресайзе, поэтому формула
    /// устойчива и не требует итераций.
    /// </summary>
}
