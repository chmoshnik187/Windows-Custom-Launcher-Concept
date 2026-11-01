namespace DotShell.Island;

using DotShell.Core.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

/// <summary>
/// Само окно острова.
///
/// Логика такая:
///   1. Создаём обычное WinUI-окно и сразу превращаем его Win32-стилями
///      в "таблетку поверх всего" (IslandChrome.Apply).
///   2. Окно нарочно БОЛЬШЕ самой таблетки (700×380 против 372×46):
///      внутри него будет расти раскрытая панель, и нам не придётся дёргать
///      SetWindowPos на каждый кадр анимации — а это главный источник дёрганья
///      и мерцания в таких поделках.
///   3. Раз окно большое, его нельзя оставлять кликабельным целиком.
///      Таймер раз в 40 мс смотрит, где курсор: над таблеткой — клики наши,
///      мимо — проходят насквозь к обычным окнам.
///   4. Второй таймер следит за полноэкранным режимом и убирает остров,
///      чтобы он не висел поверх игры.
/// </summary>
public sealed class IslandWindow : Window
{
    private readonly MainPage _page;
    private DispatcherQueueTimer? _hitTestTimer;
    private DispatcherQueueTimer? _fullscreenTimer;
    private bool _visibleByUser = true;
    private bool _chromeApplied;

    public IslandWindow()
    {
        _page = new MainPage(ShellServices.Instance);
        Content = _page;

        // Гасим системный фон окна (Mica/Acrylic). Иначе вокруг таблетки
        // будет виден серый прямоугольник — весь эффект пропадёт.
        TrySetTransparentBackdrop();

        Closed += OnClosed;

        // У Window нет события Loaded (оно у FrameworkElement),
        // поэтому подписываемся на Loaded корневого содержимого.
        if (Content is FrameworkElement root) root.Loaded += OnRootLoaded;
    }

    private void TrySetTransparentBackdrop()
    {
        // Мика/акрил включаются через DWM-атрибут окна. Ставим "нет подложки".
        try
        {
            var hwnd = WindowHelper.GetHwnd(this);
            if (hwnd == IntPtr.Zero) return;
            // DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMSBT_NONE = 0
            Win32.DwmSetWindowAttribute(hwnd, 38, 0);
        }
        catch
        {
            // На старых сборках Windows атрибута может не быть — не смертельно.
        }
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_chromeApplied) return;
        _chromeApplied = true;
        if (sender is FrameworkElement root) root.Loaded -= OnRootLoaded;

        var offset = ShellServices.Instance.Settings.Current.IslandTopOffset;
        IslandChrome.Apply(this, offset);

        // Регистрируем своё окно как "своё": сэмплер авто-цвета будет его игнорировать,
        // иначе акцент начнёт усиливать сам себя (петля обратной связи).
        ShellServices.Instance.OwnWindows.Add(IslandChrome.Hwnd);

        ColorHelper.Bind(ShellServices.Instance.Theme);

        StartHitTestTimer();
        StartFullscreenTimer();
        RegisterHotkeys();

        // Приветственное раскрытие: показываем один раз, чтобы человек понял,
        // что остров кликабельный. Но НЕ показываем, если мы стартовали вместе
        // с Windows (--minimized) — при входе в систему это только мешает.
        if (!Core.StartupManager.StartedMinimized)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _page.SetExpanded(true);
                var timer = DispatcherQueue.CreateTimer();
                timer.Interval = TimeSpan.FromSeconds(3.5);
                timer.IsRepeating = false;
                timer.Tick += (_, _) => _page.SetExpanded(false);
                timer.Start();
            });
        }
    }

    private void StartHitTestTimer()
    {
        int interval = ShellServices.Instance.Settings.Current.HitTestPollMs;
        if (interval <= 0) return;

        _hitTestTimer = DispatcherQueue.CreateTimer();
        _hitTestTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(16, interval));
        _hitTestTimer.IsRepeating = true;
        _hitTestTimer.Tick += (_, _) =>
        {
            var (w, h) = _page.CurrentPillSize;
            IslandChrome.UpdateHitTest(w, h);
        };
        _hitTestTimer.Start();
    }

    private void StartFullscreenTimer()
    {
        _fullscreenTimer = DispatcherQueue.CreateTimer();
        _fullscreenTimer.Interval = TimeSpan.FromMilliseconds(400);
        _fullscreenTimer.IsRepeating = true;
        _fullscreenTimer.Tick += (_, _) =>
        {
            bool hide = ShellServices.Instance.Settings.Current.HideInFullscreen
                        && ShellServices.Instance.WindowWatcher.IsFullscreenActive;

            if (hide && _visibleByUser)
            {
                _visibleByUser = false;
                _page.SetExpanded(false);
                IslandChrome.SetVisible(false);
            }
            else if (!hide && !_visibleByUser)
            {
                _visibleByUser = true;
                IslandChrome.SetVisible(true);
                IslandChrome.Reposition(); // на случай переезда на другой монитор
            }
        };
        _fullscreenTimer.Start();
    }

    private void RegisterHotkeys()
    {
        var hotkeys = ShellServices.Instance.Hotkeys;

        // Ctrl+Shift+Space — раскрыть/свернуть остров.
        // (Чистый Ctrl+Space часто занят переключением раскладки, поэтому берём с Shift.)
        bool toggle = hotkeys.Register("island.toggle",
            Win32.MOD_CONTROL | Win32.MOD_SHIFT, (uint)' ',
            () => DispatcherQueue.TryEnqueue(() => _page.SetExpanded(!_page.VM.IsExpanded)));

        // Ctrl+Shift+V — сразу открыть вкладку буфера обмена.
        bool clipboard = hotkeys.Register("clipboard.open",
            Win32.MOD_CONTROL | Win32.MOD_SHIFT, (uint)'V',
            () => DispatcherQueue.TryEnqueue(() =>
            {
                IslandChrome.SetVisible(true);
                _visibleByUser = true;
                _page.OpenTab("clipboard");
            }));

        if (!toggle || !clipboard)
            Log.Info($"Hotkeys: toggle={toggle}, clipboard={clipboard} — возможно, заняты другой программой");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _hitTestTimer?.Stop();
        _fullscreenTimer?.Stop();
        ShellServices.Instance.OwnWindows.Remove(IslandChrome.Hwnd);
    }
}
