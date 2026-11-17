namespace DotShell.Island.Wpf;

using System.Linq;
using System.Runtime.InteropServices;
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
    private bool _modActive;
    private TaskbarIslandHostWindow? _hostWindow;
    private bool _taskbarHosted;
    private bool _panelBusy;
    private DispatcherTimer? _panelBusyTimer;
    private int _lastDesktopCount = -1;
    private bool _workspacesReady;

    private static bool IsTaskbarEdge(DotShell.Core.Settings.AppSettings s) =>
        string.Equals(s.IslandEdge, "taskbar", StringComparison.OrdinalIgnoreCase);

    /// <summary>Точка отсчёта: центр пробега и низ окна (DIP).</summary>
    private (double CenterX, double AnchorBottom)? TaskbarAnchor()
    {
        // Встроенный остров: большая панель открывается над панелью задач,
        // по центру экрана — как меню Пуск.
        if (_taskbarHosted)
        {
            var wa = SystemParameters.WorkArea;
            return (SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth / 2, wa.Bottom);
        }

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

        TaskbarIslandBridge.Start();
        TaskbarIslandBridge.CommandReceived += action =>
        {
            try { Dispatcher.BeginInvoke(new Action(() => HandleTaskbarBridgeCommand(action))); }
            catch { }
        };

        _taskbarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _taskbarTimer.Tick += (_, _) => TaskbarTick();
        SyncTaskbarMode();
        StartHostedIsland();

        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnTaskbarDisplayChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnTaskbarDisplayChanged;
            StopHostedIsland();
        };
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
            StopHostedIsland();
            RestoreIslandChrome();
            if (!IsVisible) Show();
            return;
        }

        if (_taskbarTimer is { IsEnabled: false } or null) _taskbarTimer?.Start();
        TaskbarTick(force: true);
    }

    private void HandleTaskbarBridgeCommand(string action)
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;

        switch (action)
        {
            case "open-settings":
                OpenSettings();
                break;
            default: // toggle-panel
                if (_isExpanded) Collapse();
                else
                {
                    if (!IsVisible) Show();
                    Expand();
                }
                break;
        }
    }

    /// <summary>Мод рисует остров внутри панели — наш оверлей тогда не нужен.</summary>
    private bool ModDrawsIsland => IsTaskbarEdge(_services.Settings.Current) && _modActive;

    private void TaskbarTick(bool force = false)
    {
        if (!IsTaskbarEdge(_services.Settings.Current)) return;

        _modActive = TaskbarIslandBridge.ModActive;

        // Интегрированный режим: свёрнутый остров рисует мод внутри панели,
        // поэтому наше окно прячем (иначе оно дублировало бы остров оверлеем).
        if (_modActive)
        {
            StopHostedIsland();
            if (!_isExpanded && IsVisible) Hide();
            WriteBridgeState(expanded: _isExpanded);
            return;
        }

        // Настоящая интеграция: остров встроен в панель задач через child HWND.
        // Наше большое окно в свёрнутом состоянии прячем, оно нужно только для
        // раскрытой панели и уведомлений.
        if (_taskbarHosted)
        {
            if (!_isExpanded && !_panelBusy && IsVisible) Hide();
            UpdateHostedIsland();
            WriteBridgeState(expanded: _isExpanded);
            return;
        }

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

        WriteBridgeState(expanded: _isExpanded);

        if (!IsVisible) Show();
        // Панель задач тоже topmost и при кликах поднимается над островом —
        // без частого reassert остров иногда «исчезал» при переключении вкладок.
        ReassertTopmost();
    }

    /// <summary>Запустить встроенный в панель остров (Deskband11Lib).</summary>
    private void StartHostedIsland()
    {
        if (_hostWindow is not null || !IsTaskbarEdge(_services.Settings.Current)) return;
        if (string.Equals(Environment.GetEnvironmentVariable("DOTSHELL_TASKBAR_HOST"), "0", StringComparison.Ordinal))
        {
            Log.Info("taskbar host: отключён переменной DOTSHELL_TASKBAR_HOST=0");
            return;
        }

        // Один замер панели — для цвета уголков и темы.
        var snap = ProbeTaskbar();
        if (snap is not null && snap.Gaps.Count > 0)
        {
            _taskbar = snap;
            _taskbarGap = ChooseGap(snap);
        }

        var window = new TaskbarIslandHostWindow(OpenFromHostedIsland, Log.Info);
        window.RecreateRequested += () =>
        {
            try { Dispatcher.BeginInvoke(new Action(RecreateHostedIsland)); } catch { }
        };
        _hostWindow = window;
        _ = AttachHostedIslandAsync(window);
    }

    private async Task AttachHostedIslandAsync(TaskbarIslandHostWindow window)
    {
        try
        {
            bool ok = await window.AttachAsync();
            if (!ReferenceEquals(window, _hostWindow)) return;
            if (!ok)
            {
                StopHostedIsland();
                ShowFallbackIsland();
                Log.Info("taskbar host: не удалось встроиться, остаёмся оверлеем");
                return;
            }

            _taskbarHosted = true;
            if (!_isExpanded && IsVisible) Hide();
            UpdateHostedIsland();
            Log.Info("taskbar host: остров в панели задач");
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar host attach exception: {ex.Message}");
            StopHostedIsland();
        }
    }

    private void RecreateHostedIsland()
    {
        Log.Info("taskbar host: пересоздание после explorer/DPI/монитора");
        StopHostedIsland();
        _ = Task.Delay(1200).ContinueWith(
            _ =>
            {
                try { Dispatcher.BeginInvoke(new Action(StartHostedIsland)); }
                catch { }
            },
            TaskScheduler.Default);
    }

    private void StopHostedIsland()
    {
        bool wasHosted = _taskbarHosted;
        _taskbarHosted = false;
        var window = _hostWindow;
        _hostWindow = null;
        if (window is null) return;
        try { window.Detach(); window.Close(); } catch { }
        if (wasHosted && !_isExpanded) TaskbarTick(force: true);
    }

    private void OpenFromHostedIsland()
    {
        try
        {
            Log.Info("taskbar host: клик по острову");
            if (!IsTaskbarEdge(_services.Settings.Current)) return;
            if (_isExpanded) return;
            if (!IsVisible) Show();
            Activate();
            Expand();
        }
        catch (Exception ex) { Log.Info($"taskbar host click failed: {ex.Message}"); }
    }

    private void UpdateHostedIsland()
    {
        var window = _hostWindow;
        if (window is null) return;

        var accent = ColorPalette.Accent;
        var panel = _taskbarGap?.Back ?? Color.FromRgb(0x0B, 0x0B, 0x0E);
        bool light = _taskbarGap?.IsLight ?? false;
        var (count, index) = VirtualDesktops.ReadState();
        window.Update(_vm.TimeText, _vm.MediaLine ?? "", _vm.CpuText ?? "", _vm.HasMedia,
            count, index, accent, panel, light);
    }

    private void MarkPanelBusy(int milliseconds)
    {
        if (!_taskbarHosted) return;
        _panelBusy = true;
        _panelBusyTimer?.Stop();
        _panelBusyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        _panelBusyTimer.Tick += (_, _) =>
        {
            _panelBusy = false;
            _panelBusyTimer?.Stop();
            if (_taskbarHosted && !_isExpanded && IsVisible) Hide();
        };
        _panelBusyTimer.Start();
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
                if (IsTaskbarEdge(_services.Settings.Current) && !_isExpanded) ApplyTaskbarPlacement(Width, Height);
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

        // В режиме панели свёрнутый остров — это отдельное child-окно внутри
        // панели; большое окно не показываем, чтобы не было двойника.
        Opacity = 1;
        Hide();
    }

    /// <summary>
    /// Раскрытие в две фазы: сначала остров вытягивается по ширине под будущую
    /// панель (оставаясь в панели задач), потом из него выпадает панель вверх.
    /// Владелец жаловался, что ширина и высота едут одновременно и выходит рвано.
    /// </summary>
    // ================================================================
    //  Геометрия панели: раскрытие от реальной кромки панели задач
    // ================================================================

    private enum TaskbarPanelEdge { Bottom, Top, Left, Right }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Кромка, на которой реально стоит панель задач, и её прямоугольник (DIP).</summary>
    private (TaskbarPanelEdge Edge, Rect Taskbar, Rect Screen)? TaskbarPanelGeometry()
    {
        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);

        try
        {
            var hwnd = Win32.FindWindow("Shell_TrayWnd", null);
            if (hwnd != IntPtr.Zero && Win32.GetWindowRect(hwnd, out var r) && r.Width > 8 && r.Height > 8)
            {
                uint dpi = GetDpiForWindow(hwnd);
                double scale = (dpi > 0 ? dpi : 96) / 96.0;
                var taskbar = new Rect(r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

                TaskbarPanelEdge edge;
                if (taskbar.Width >= taskbar.Height)
                    edge = taskbar.Top <= screen.Top + 4 ? TaskbarPanelEdge.Top
                        : taskbar.Bottom >= screen.Bottom - 4 ? TaskbarPanelEdge.Bottom
                        : taskbar.Top < screen.Top + screen.Height / 2 ? TaskbarPanelEdge.Top : TaskbarPanelEdge.Bottom;
                else
                    edge = taskbar.Left <= screen.Left + 4 ? TaskbarPanelEdge.Left
                        : taskbar.Right >= screen.Right - 4 ? TaskbarPanelEdge.Right
                        : taskbar.Left < screen.Left + screen.Width / 2 ? TaskbarPanelEdge.Left : TaskbarPanelEdge.Right;

                return (edge, taskbar, screen);
            }
        }
        catch { }

        // Фолбэк: рабочая область уже исключает панель — кромка читается по отступу.
        var wa = SystemParameters.WorkArea;
        if (wa.Bottom < screen.Bottom - 4)
            return (TaskbarPanelEdge.Bottom, new Rect(screen.Left, wa.Bottom, screen.Width, screen.Bottom - wa.Bottom), screen);
        if (wa.Top > screen.Top + 4)
            return (TaskbarPanelEdge.Top, new Rect(screen.Left, screen.Top, screen.Width, wa.Top - screen.Top), screen);
        if (wa.Left > screen.Left + 4)
            return (TaskbarPanelEdge.Left, new Rect(screen.Left, screen.Top, wa.Left - screen.Left, screen.Height), screen);
        if (wa.Right < screen.Right - 4)
            return (TaskbarPanelEdge.Right, new Rect(wa.Right, screen.Top, screen.Right - wa.Right, screen.Height), screen);

        return null;
    }

    private static Rect PanelBounds(TaskbarPanelEdge edge, Rect taskbar, Rect screen, double winW, double winH)
    {
        const double gap = 8;
        double left, top;
        switch (edge)
        {
            case TaskbarPanelEdge.Top:
                left = screen.Left + (screen.Width - winW) / 2;
                top = taskbar.Bottom + gap;
                break;
            case TaskbarPanelEdge.Left:
                left = taskbar.Right + gap;
                top = screen.Top + (screen.Height - winH) / 2;
                break;
            case TaskbarPanelEdge.Right:
                left = taskbar.Left - gap - winW;
                top = screen.Top + (screen.Height - winH) / 2;
                break;
            default:
                left = screen.Left + (screen.Width - winW) / 2;
                top = taskbar.Top - gap - winH;
                break;
        }

        left = Math.Clamp(left, screen.Left + 4, screen.Right - winW - 4);
        top = Math.Clamp(top, screen.Top + 4, screen.Bottom - winH - 4);
        return new Rect(left, top, winW, winH);
    }

    private void ClearWindowAnimations()
    {
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
    }

    private void SetPanelStart(TaskbarPanelEdge edge, Rect taskbar, double minW, double minH)
    {
        ClearWindowAnimations();
        double cx = taskbar.Left + taskbar.Width / 2;
        double cy = taskbar.Top + taskbar.Height / 2;
        switch (edge)
        {
            case TaskbarPanelEdge.Top: Left = cx - minW / 2; Top = taskbar.Bottom; break;
            case TaskbarPanelEdge.Left: Left = taskbar.Right; Top = cy - minH / 2; break;
            case TaskbarPanelEdge.Right: Left = taskbar.Left - minW; Top = cy - minH / 2; break;
            default: Left = cx - minW / 2; Top = taskbar.Top - minH; break;
        }
        Width = minW;
        Height = minH;
        if (Pill is not null)
        {
            Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            Pill.BeginAnimation(FrameworkElement.HeightProperty, null);
            Pill.Width = CollapsedWidth;
            Pill.Height = CollapsedHeight;
        }
        if (!IsVisible)
        {
            Opacity = 1;
            Show();
        }
    }

    /// <summary>
    /// Раскрытие панели от реальной кромки панели задач: снизу — вверх, сверху —
    /// вниз, слева/справа — вбок. Больше не «выезжает снизу» при панели сверху.
    /// </summary>
    internal void AnimateTaskbarOpen(double targetW, double targetH)
    {
        var geo = TaskbarPanelGeometry();
        if (geo is null)
        {
            AnimateShellTo(targetW, targetH);
            return;
        }

        var (edge, taskbar, screen) = geo.Value;
        Log.Info($"taskbar open: edge={edge} taskbar={taskbar}");
        double winW = Math.Min(Math.Max(CollapsedWidth, targetW), screen.Width - 40);
        double winH = Math.Min(Math.Max(CollapsedHeight + 56, targetH + 12), screen.Height - 40);
        var bounds = PanelBounds(edge, taskbar, screen, winW, winH);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        SetPanelStart(edge, taskbar, CollapsedWidth, CollapsedHeight + 56);

        bool side = edge is TaskbarPanelEdge.Left or TaskbarPanelEdge.Right;
        if (side)
        {
            _blinds?.Open();
            AnimateTo(this, WidthProperty, bounds.Width, 320, ease);
            AnimateTo(this, HeightProperty, bounds.Height, 320, ease);
            AnimateTo(this, LeftProperty, bounds.Left, 320, ease);
            AnimateTo(this, TopProperty, bounds.Top, 320, ease);
            AnimateTo(Pill, WidthProperty, targetW, 320, ease);
            AnimateTo(Pill, HeightProperty, targetH, 320, ease);
            AnimateTo(ExpandedLayer, HeightProperty, targetH - 10, 320, ease);
            ScheduleTaskbarReapply();
            return;
        }

        int phase1 = 120, phase2 = 260;
        AnimateTo(this, WidthProperty, winW, phase1, ease);
        AnimateTo(this, LeftProperty, bounds.Left, phase1, ease);
        AnimateTo(Pill, WidthProperty, targetW, phase1, ease);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(phase1) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                _blinds?.Open();
                AnimateTo(this, HeightProperty, winH, phase2, ease);
                AnimateTo(this, TopProperty, bounds.Top, phase2, ease);
                AnimateTo(Pill, HeightProperty, targetH, phase2, ease);
                AnimateTo(ExpandedLayer, HeightProperty, targetH - 10, phase2, ease);
                ScheduleTaskbarReapply();
            }
            catch (Exception ex) { Log.Info($"taskbar open phase2 failed: {ex.Message}"); }
        };
        timer.Start();
    }

    /// <summary>Сворачивание обратно к реальной кромке панели задач.</summary>
    internal void AnimateTaskbarClose()
    {
        var geo = TaskbarPanelGeometry();
        if (geo is null)
        {
            AnimateShellTo(CollapsedWidth, CollapsedHeight);
            return;
        }

        var (edge, taskbar, _) = geo.Value;
        double minW = CollapsedWidth;
        double minH = CollapsedHeight + 56;
        double cx = taskbar.Left + taskbar.Width / 2;
        double cy = taskbar.Top + taskbar.Height / 2;
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        _blinds?.Close();

        bool side = edge is TaskbarPanelEdge.Left or TaskbarPanelEdge.Right;
        if (side)
        {
            double left = edge == TaskbarPanelEdge.Left ? taskbar.Right : taskbar.Left - minW;
            AnimateTo(this, WidthProperty, minW, 240, ease);
            AnimateTo(this, HeightProperty, minH, 240, ease);
            AnimateTo(this, LeftProperty, left, 240, ease);
            AnimateTo(this, TopProperty, cy - minH / 2, 240, ease);
            AnimateTo(Pill, WidthProperty, minW, 240, ease);
            AnimateTo(Pill, HeightProperty, CollapsedHeight, 240, ease);
            AnimateTo(ExpandedLayer, HeightProperty, CollapsedHeight - 10, 240, ease);

            var sideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
            sideTimer.Tick += (_, _) =>
            {
                try { sideTimer.Stop(); TaskbarTick(force: true); }
                catch { }
            };
            sideTimer.Start();
            return;
        }

        AnimateTo(this, HeightProperty, minH, 240, ease);
        AnimateTo(this, TopProperty, edge == TaskbarPanelEdge.Top ? taskbar.Bottom : taskbar.Top - minH, 240, ease);
        AnimateTo(Pill, HeightProperty, CollapsedHeight, 240, ease);
        AnimateTo(ExpandedLayer, HeightProperty, CollapsedHeight - 10, 240, ease);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) =>
        {
            try
            {
                timer.Stop();
                AnimateTo(this, WidthProperty, minW, 150, ease);
                AnimateTo(this, LeftProperty, cx - minW / 2, 150, ease);
                AnimateTo(Pill, WidthProperty, minW, 150, ease);
                TaskbarTick(force: true);
            }
            catch (Exception ex) { Log.Info($"taskbar close phase2 failed: {ex.Message}"); }
        };
        timer.Start();
    }

    private void WriteBridgeState(bool expanded)
    {
        var (count, index) = VirtualDesktops.ReadState();
        var accent = ColorPalette.Accent;
        var gap = _taskbarGap;

        TaskbarIslandBridge.WriteState(w =>
        {
            w.WriteString("time", _vm.TimeText);
            w.WriteBoolean("hasMedia", _vm.HasMedia);
            w.WriteString("track", _vm.MediaLine);
            w.WriteString("cpu", _vm.CpuText);
            w.WriteNumber("workspaces", count);
            w.WriteNumber("workspace", index);
            w.WriteBoolean("expanded", expanded);
            w.WriteString("accent", $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            if (gap is not null)
            {
                w.WriteString("panelColor", $"#{gap.Back.R:X2}{gap.Back.G:X2}{gap.Back.B:X2}");
                w.WriteBoolean("light", gap.IsLight);
            }
            w.WriteString("updated", DateTime.Now.ToString("O"));
        });
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
