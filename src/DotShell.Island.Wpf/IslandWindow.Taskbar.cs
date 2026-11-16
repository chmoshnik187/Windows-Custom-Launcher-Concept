namespace DotShell.Island.Wpf;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DotShell.Core.Native;
using DotShell.Island.Wpf.Controls;
using DotShell.Island.Wpf.Taskbar;

/// <summary>
/// Режим «остров в панели задач».
///
/// Владелец хотел, чтобы остров не висел поверх панели, а был её частью и
/// исчезал вместе с ней в фуллскрине. Панель задач Windows 11 рисуется через
/// композицию XAML, и дочерние HWND (в том числе layered) ложатся ПОД неё —
/// проверено: окно создаётся, видимо, но не отображается. Поэтому «вписанность»
/// делаем поведением и формой:
///
///   • свёрнутый остров живёт ровно в свободном пробеге панели, его фон — это
///     затемнённый замеренный акрил панели, поэтому он выглядит влитым, а не
///     наклеенным; при скрытии панели (фуллскрин/автоскрытие) остров прячется
///     вместе с ней;
///   • раскрытие — слоистая «металлическая дверь» (TaskbarBlinds), поднимающаяся
///     из панели вверх; панель остаётся на месте, интерфейс выезжает из неё;
///   • точки рабочих столов — в свёрнутой капсуле, переключение COM-ом.
/// </summary>
public partial class IslandWindow
{
    private DispatcherTimer? _taskbarTimer;
    private DispatcherTimer? _workspaceTimer;
    private TaskbarProbe.Snapshot? _taskbar;
    private TaskbarProbe.GapCandidate? _taskbarGap;
    private WorkspaceChips? _workspaceChips;
    private TaskbarBlinds? _blinds;
    private Brush? _savedPillBackground;
    private bool _taskbarChrome;
    private bool _taskbarFallback;
    private int _lastDesktopIndex = -1;
    private int _probeMisses;
    private int _lastDesktopCount = -1;
    private bool _workspacesReady;

    private static bool IsTaskbarEdge(DotShell.Core.Settings.AppSettings s) =>
        string.Equals(s.IslandEdge, "taskbar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Точка отсчёта: центр пробега и низ окна (DIP).</summary>
    private (double CenterX, double AnchorBottom)? TaskbarAnchor()
    {
        var snap = _taskbar ?? ProbeTaskbar();
        if (snap is null || snap.Gaps.Count == 0) return null;
        _taskbar = snap;

        var gap = ChooseGap(snap);
        if (gap is null) return null;
        _taskbarGap = gap;

        double inside = Math.Max(0, (snap.Taskbar.Height - Math.Min(CollapsedHeight, snap.Taskbar.Height - 2)) / 2);
        double centerX = gap.Rect.Left + gap.Rect.Width / 2;
        return (centerX, snap.Taskbar.Bottom - inside);
    }

    private TaskbarProbe.GapCandidate? ChooseGap(TaskbarProbe.Snapshot snap)
    {
        if (snap.Gaps.Count == 0) return null;

        double currentCenter = Left + Width / 2;

        if (_taskbarGap is { } old)
        {
            foreach (var candidate in snap.Gaps)
            {
                double overlap = Math.Min(candidate.Rect.Right, old.Rect.Right)
                                 - Math.Max(candidate.Rect.Left, old.Rect.Left);
                if (overlap > old.Rect.Width * 0.5) return candidate;
            }
        }

        foreach (var candidate in snap.Gaps)
            if (candidate.Rect.Left <= currentCenter && currentCenter <= candidate.Rect.Right)
                return candidate;

        TaskbarProbe.GapCandidate? nearest = null;
        double best = double.MaxValue;
        foreach (var candidate in snap.Gaps)
        {
            double d = Math.Abs(candidate.Rect.Left + candidate.Rect.Width / 2 - currentCenter);
            if (d < best) { best = d; nearest = candidate; }
        }
        return nearest ?? snap.Gaps[0];
    }

    private TaskbarProbe.Snapshot? ProbeTaskbar()
    {
        Rect? exclude = null;
        if (IsLoaded && Top > 200) exclude = new Rect(Left, Top, Width, Height);
        return TaskbarProbe.Probe(this, exclude);
    }

    // ================================================================
    //  Инициализация
    // ================================================================

    private void InitTaskbarAndWorkspaces()
    {
        if (_workspacesReady) return;
        _workspacesReady = true;

        try
        {
            _workspaceChips = new WorkspaceChips
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            _workspaceChips.Refresh(force: true);
            if (RightChips is not null) RightChips.Children.Insert(0, _workspaceChips);

            _blinds = new TaskbarBlinds { Visibility = Visibility.Collapsed };
            if (RootGrid is not null)
            {
                Grid.SetRowSpan(_blinds, 99);
                Grid.SetColumnSpan(_blinds, 99);
                RootGrid.Children.Add(_blinds);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar init failed: {ex.Message}");
        }

        _workspaceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _workspaceTimer.Tick += (_, _) =>
        {
            try
            {
                _workspaceChips?.Refresh();
                FollowWorkspaceIfChanged();
                UpdateTaskbarStrip();
            }
            catch (Exception ex) { Log.Info($"workspace tick failed: {ex.Message}"); }
        };
        _workspaceTimer.Start();

        _taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _taskbarTimer.Tick += (_, _) => TaskbarTick();
        SyncTaskbarMode();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnTaskbarDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnTaskbarDisplayChanged;
    }

    private void OnTaskbarDisplayChanged(object? sender, EventArgs e)
    {
        try { Dispatcher.BeginInvoke(() => TaskbarTick(force: true)); }
        catch { }
    }

    private void SyncTaskbarMode()
    {
        bool wanted = IsTaskbarEdge(_services.Settings.Current);

        if (!wanted)
        {
            if (_taskbarTimer is { IsEnabled: true }) _taskbarTimer.Stop();
            _taskbar = null;
            _taskbarGap = null;
            _taskbarFallback = false;
            RestoreIslandChrome();
            if (!IsVisible) Show();
            return;
        }

        if (_taskbarTimer is { IsEnabled: false } or null) _taskbarTimer?.Start();
        TaskbarTick(force: true);
    }

    private void TaskbarTick(bool force = false)
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;

        var snap = ProbeTaskbar();
        if (snap is null || snap.Gaps.Count == 0)
        {
            // Одиночный сбой пробы (поиск развернулся, панель мигнула) не повод
            // уводить остров наверх — он должен оставаться в панели. Откат только
            // после трёх промахов подряд.
            _probeMisses++;
            if (_probeMisses >= 3 && !_taskbarFallback)
            {
                _taskbarFallback = true;
                ShowFallbackIsland();
                Log.Info("taskbar mode: панель не распознана трижды, остров откатился наверх");
            }
            return;
        }
        _probeMisses = 0;

        var chosen = ChooseGap(snap);
        if (chosen is null) return;

        bool changed = force || _taskbar is null
            || snap.Taskbar != _taskbar.Taskbar
            || chosen.Rect != _taskbarGap?.Rect
            || chosen.Back != _taskbarGap?.Back;

        _taskbar = snap;
        _taskbarGap = chosen;
        _taskbarFallback = false;

        if (changed || _taskbarChrome != !_isExpanded)
        {
            ApplyTaskbarPlacement(Width, Height);
            ApplyIslandChrome();
        }

        if (!IsVisible) Show();
        // Панель задач тоже topmost и при кликах поднимается над островом —
        // без частого reassert остров иногда «исчезал» при переключении вкладок.
        ReassertTopmost();
    }

    private void ApplyTaskbarPlacement(double winW, double winH)
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;

        var anchor = TaskbarAnchor();
        if (anchor is null) return;

        double left = anchor.Value.CenterX - winW / 2;
        double top = anchor.Value.AnchorBottom - winH;

        double screenLeft = SystemParameters.VirtualScreenLeft;
        double screenWidth = SystemParameters.VirtualScreenWidth;
        double screenTop = SystemParameters.VirtualScreenTop;
        double screenHeight = SystemParameters.VirtualScreenHeight;
        if (left < screenLeft + 4) left = screenLeft + 4;
        if (left + winW > screenLeft + screenWidth - 4)
            left = screenLeft + screenWidth - winW - 4;

        // Нижняя кромка окна не должна уходить под экран: при раскрытии/
        // анимации высота меняется, и если Top пересчитать раньше времени,
        // нижняя половина панели (громкость/футер) срезается.
        double screenBottom = screenTop + screenHeight;
        if (top + winH > screenBottom - 2) top = screenBottom - 2 - winH;
        if (top < screenTop + 2) top = screenTop + 2;

        if (Math.Abs(Left - left) > 0.5 || Math.Abs(Top - top) > 0.5)
        {
            Left = left;
            Top = top;
        }
    }

    /// <summary>Фон капсулы в тон панели: тёмная примесь, чтобы не было белой полосы.</summary>
    private void ApplyIslandChrome()
    {
        if (_isExpanded || _taskbarGap is null || Pill is null)
        {
            RestoreIslandChrome();
            return;
        }

        if (_savedPillBackground is null) _savedPillBackground = Pill.Background;

        var back = _taskbarGap.Back;
        double keep = _taskbarGap.IsLight ? 0.16 : 0.42;
        byte r = (byte)Math.Clamp(back.R * keep, 8, 255);
        byte g = (byte)Math.Clamp(back.G * keep, 8, 255);
        byte b = (byte)Math.Clamp(back.B * keep, 8, 255);

        var brush = new SolidColorBrush(Color.FromArgb(0xF2, r, g, b));
        brush.Freeze();
        Pill.Background = brush;
        _taskbarChrome = true;

        // В свёрнутом виде не тащим декор острова в панель: сегмент должен быть
        // тихим и плоским, а не «островом в острове». Скругление — как у панели.
        if (Backdrop is not null) Backdrop.Visibility = Visibility.Collapsed;
        if (MiniEq is not null) MiniEq.Visibility = Visibility.Collapsed;
        try
        {
            double radius = Math.Clamp(_services.Settings.Current.IslandRadius, 0, 24);
            Pill.CornerRadius = new CornerRadius(Math.Min(radius, 9));
        }
        catch { }
    }

    private void RestoreIslandChrome()
    {
        if (!_taskbarChrome || Pill is null) return;
        if (_savedPillBackground is not null) Pill.Background = _savedPillBackground;
        if (Backdrop is not null) Backdrop.Visibility = Visibility.Visible;
        try { UpdateMiniEq(); } catch { }
        try
        {
            double radius = Math.Clamp(_services.Settings.Current.IslandRadius, 0, 24);
            Pill.CornerRadius = new CornerRadius(Math.Min(radius, Pill.ActualHeight / 2));
        }
        catch { }
        _taskbarChrome = false;
    }

    /// <summary>После анимации высоты пересчитать привязку к панели.</summary>
    internal void ScheduleTaskbarReapply()
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(340) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                if (IsTaskbarEdge(_services.Settings.Current)) ApplyTaskbarPlacement(Width, Height);
            }
            catch { }
        };
        timer.Start();
    }

    private void ShowFallbackIsland()
    {
        try
        {
            var wa = GetCurrentWorkArea();
            Width = Math.Max(Width, CollapsedWidth);
            Height = Math.Max(Height, CollapsedHeight + 56);
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top + 8;
            if (!IsVisible) Show();
        }
        catch (Exception ex) { Log.Info($"taskbar fallback failed: {ex.Message}"); }
    }

    /// <summary>Проявить окно после того, как проба поставила его в панель.</summary>
    internal void RevealAfterTaskbarPlacement()
    {
        if (!IsTaskbarEdge(_services.Settings.Current))
        {
            Opacity = 1;
            return;
        }

        try
        {
            TaskbarTick(force: true);
            var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            BeginAnimation(OpacityProperty, anim);
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar reveal failed: {ex.Message}");
            Opacity = 1;
        }
    }

    /// <summary>
    /// Раскрытие в две фазы: сначала остров вытягивается по ширине под будущую
    /// панель (оставаясь в панели задач), потом из него выпадает панель вверх.
    /// Владелец жаловался, что ширина и высота едут одновременно и выходит рвано.
    /// </summary>
    internal void AnimateTaskbarOpen(double targetW, double targetH)
    {
        var anchor = TaskbarAnchor();
        if (anchor is null)
        {
            AnimateShellTo(targetW, targetH);
            return;
        }

        double winW = Math.Max(CollapsedWidth, targetW);
        double winH = Math.Max(CollapsedHeight + 56, targetH + 12);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        int phase1 = 120, phase2 = 260;

        // Фаза 1 — ширина.
        AnimateTo(this, WidthProperty, winW, phase1, ease);
        AnimateTo(this, LeftProperty, anchor.Value.CenterX - winW / 2, phase1, ease);
        AnimateTo(Pill, WidthProperty, targetW, phase1, ease);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(phase1) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                // Фаза 2 — высота вверх, «дверь» открывается.
                _blinds?.Open();
                AnimateTo(this, HeightProperty, winH, phase2, ease);
                AnimateTo(this, TopProperty, anchor.Value.AnchorBottom - winH, phase2, ease);
                AnimateTo(Pill, HeightProperty, targetH, phase2, ease);
                AnimateTo(ExpandedLayer, HeightProperty, targetH - 10, phase2, ease);
                ScheduleTaskbarReapply();
            }
            catch (Exception ex) { Log.Info($"taskbar open phase2 failed: {ex.Message}"); }
        };
        timer.Start();
    }

    /// <summary>Сворачивание в обратном порядке: сначала высота, потом ширина.</summary>
    internal void AnimateTaskbarClose()
    {
        double minH = CollapsedHeight + 56;
        double centerX = Left + Width / 2;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        int phase1 = 240, phase2 = 150;

        _blinds?.Close();
        AnimateTo(this, HeightProperty, minH, phase1, ease);
        AnimateTo(this, TopProperty, (TaskbarAnchor()?.AnchorBottom ?? Top + minH) - minH, phase1, ease);
        AnimateTo(Pill, HeightProperty, CollapsedHeight, phase1, ease);
        AnimateTo(ExpandedLayer, HeightProperty, CollapsedHeight - 10, phase1, ease);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(phase1) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                AnimateTo(this, WidthProperty, CollapsedWidth, phase2, ease);
                AnimateTo(this, LeftProperty, centerX - CollapsedWidth / 2, phase2, ease);
                AnimateTo(Pill, WidthProperty, CollapsedWidth, phase2, ease);
                TaskbarTick(force: true);
            }
            catch (Exception ex) { Log.Info($"taskbar close phase2 failed: {ex.Message}"); }
        };
        timer.Start();
    }

    /// <summary>Пересчитать состояние в свёрнутом виде (цвет, точки столов).</summary>
    private void UpdateTaskbarStrip()
    {
        _workspaceChips?.Refresh();
        if (!IsTaskbarEdge(_services.Settings.Current)) return;
        ApplyIslandChrome();
        if (!_isExpanded) ApplyTaskbarPlacement(Width, Height);
    }

    // ================================================================
    //  Рабочие столы
    // ================================================================

    private void FollowWorkspaceIfChanged()
    {
        int count = VirtualDesktops.Count;
        int index = VirtualDesktops.CurrentIndex;

        if (index == _lastDesktopIndex && count == _lastDesktopCount) return;

        bool wasKnown = _lastDesktopIndex >= 0;
        _lastDesktopIndex = index;
        _lastDesktopCount = count;

        Log.Info($"workspaces: count={count} index={index}");

        if (!wasKnown) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                MoveOurWindowsToCurrentDesktop();
                if (IsTaskbarEdge(_services.Settings.Current)) TaskbarTick(force: true);
            }
            catch (Exception ex) { Log.Info($"workspace follow failed: {ex.Message}"); }
        };
        timer.Start();
    }

    private void MoveOurWindowsToCurrentDesktop()
    {
        try
        {
            foreach (Window window in Application.Current.Windows)
            {
                try
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    if (hwnd != IntPtr.Zero) VirtualDesktops.MoveWindowToCurrent(hwnd);
                }
                catch { }
            }
        }
        catch (Exception ex) { Log.Info($"move windows to desktop failed: {ex.Message}"); }
    }

    // ================================================================
    //  Уведомления: остров всегда на месте, попап над панелью не нужен
    // ================================================================

    /// <summary>
    /// Показать уведомление, когда остров раскрыт (капсула занята): отдельная
    /// тост-капсула над сегментом панели, а не карточка в углу экрана.
    /// </summary>
    internal bool ShowTaskbarNoticeFromPanel(string title, string message)
    {
        try
        {
            if (!IsTaskbarEdge(_services.Settings.Current)) return false;

            var anchor = TaskbarAnchor();
            if (anchor is null) return false;

            double taskbarTop = _taskbar?.Taskbar.Top ?? (anchor.Value.AnchorBottom - 48);
            return TaskbarNoticeWindow.Show(anchor.Value.CenterX, taskbarTop, title, message);
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar notice failed: {ex.Message}");
            return false;
        }
    }

    private void PrepareTaskbarNotification()
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;
        try { if (!IsVisible) Show(); } catch { }
    }

    private void FinishTaskbarNotification()
    {
        // окно и так живёт в панели — прятать нечего
    }
}
