namespace DotShell.Island.Wpf;

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
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
    private WorkspaceDots? _workspaceDots;
    private TaskbarBlinds? _blinds;
    private Brush? _savedPillBackground;
    private bool _taskbarChrome;
    private bool _taskbarFallback;
    private int _lastDesktopIndex = -1;
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
            _workspaceDots = new WorkspaceDots
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                Visibility = Visibility.Collapsed,
            };
            _workspaceDots.Refresh(force: true);
            if (RightChips is not null) RightChips.Children.Insert(0, _workspaceDots);

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
                _workspaceDots?.Refresh();
                FollowWorkspaceIfChanged();
                UpdateTaskbarStrip();
            }
            catch (Exception ex) { Log.Info($"workspace tick failed: {ex.Message}"); }
        };
        _workspaceTimer.Start();

        _taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
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
            if (!_taskbarFallback)
            {
                _taskbarFallback = true;
                ShowFallbackIsland();
                Log.Info("taskbar mode: панель не распознана, остров откатился наверх");
            }
            return;
        }

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
        if (left < screenLeft + 4) left = screenLeft + 4;
        if (left + winW > screenLeft + screenWidth - 4)
            left = screenLeft + screenWidth - winW - 4;

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

    /// <summary>Пересчитать состояние в свёрнутом виде (цвет, точки столов).</summary>
    private void UpdateTaskbarStrip()
    {
        _workspaceDots?.Refresh();
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
