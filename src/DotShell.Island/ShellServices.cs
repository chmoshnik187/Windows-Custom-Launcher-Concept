namespace DotShell.Island;

using DotShell.Core;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Settings;
using DotShell.Core.Theming;
using DotShell.Core.Windowing;

/// <summary>
/// "Композиционный корень": здесь создаются все сервисы ядра и передаются в UI.
/// Держим их в одном месте, чтобы в любой точке программы можно было написать
/// ShellServices.Instance.Media и не таскать параметры через десяток слоёв.
///
/// Порядок создания ВАЖЕН:
///   Settings -> ThemeBus -> NativeMessageWindow -> (Clipboard, Hotkeys) -> Media -> Watchers
/// </summary>
public sealed class ShellServices : IAsyncDisposable
{
    public static ShellServices Instance { get; private set; } = null!;

    public SettingsStore Settings { get; }
    public ThemeBus Theme { get; }
    public NativeMessageWindow MessageWindow { get; }
    public IClipboardHistoryStore ClipboardStore { get; }
    public ClipboardWatcher Clipboard { get; }
    public HotkeyManager Hotkeys { get; }
    public MediaBridge Media { get; }
    public WindowWatcher WindowWatcher { get; }
    public AccentSampler AccentSampler { get; }
    public SystemStats Stats { get; }

    /// <summary>HWND окон, которые сэмплер цвета должен игнорировать (иначе цвет закольцуется сам на себя).</summary>
    public List<IntPtr> OwnWindows { get; } = new();

    private ShellServices()
    {
        Settings = SettingsStore.Load();
        Theme = new ThemeBus(Settings);

        // Одно невидимое окно на все системные уведомления: буфер обмена + горячие клавиши.
        MessageWindow = new NativeMessageWindow();

        ClipboardStore = new InMemoryClipboardHistory(
            maxItems: Settings.Current.ClipboardMaxItems,
            persistAllowed: () => Settings.Current.ClipboardPersist);

        Clipboard = new ClipboardWatcher(ClipboardStore, MessageWindow);
        Hotkeys = new HotkeyManager(MessageWindow);
        Media = new MediaBridge();
        WindowWatcher = new WindowWatcher();
        Stats = new SystemStats();

        AccentSampler = new AccentSampler(
            Theme,
            intervalMs: Settings.Current.AccentSampleIntervalMs,
            // Не берём кадр, когда десктоп не виден (игра/видео на весь экран).
            shouldSample: () => !WindowWatcher.IsFullscreenActive,
            windowsToIgnore: () => OwnWindows.ToArray());
    }

    public static async Task<ShellServices> InitializeAsync()
    {
        var services = new ShellServices();
        Instance = services;

        await services.Media.StartAsync();
        services.AccentSampler.Start();
        services.Stats.Start();

        // Первичный замер цвета — сразу, чтобы не ждать первого тика таймера.
        services.AccentSampler.SampleOnce();

        return services;
    }

    public async ValueTask DisposeAsync()
    {
        AccentSampler.Dispose();
        Stats.Dispose();
        WindowWatcher.Dispose();
        Clipboard.Dispose();
        Hotkeys.Dispose();
        await Media.DisposeAsync();
        MessageWindow.Dispose();
    }
}
