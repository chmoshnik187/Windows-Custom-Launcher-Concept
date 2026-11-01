namespace DotShell.Island.Wpf;

using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Launcher;
using DotShell.Core.Shelf;
using DotShell.Core.Time;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Settings;
using DotShell.Core.Theming;
using DotShell.Core.Windowing;
using DotShell.Core.Widgets;

/// <summary>
/// "Композиционный корень": здесь создаются все сервисы ядра.
/// Держим их в одном месте, чтобы из любой точки программы можно было написать
/// ShellServices.Instance.Media и не таскать параметры через десяток слоёв.
///
/// Порядок создания ВАЖЕН:
///   Settings → ThemeBus → NativeMessageWindow → (Clipboard, Hotkeys) → Media → Watchers
///
/// NativeMessageWindow обязан создаваться в UI-потоке: системные уведомления о буфере
/// обмена и о горячих клавишах приходят сообщениями окна, и обрабатывать их нужно там же,
/// где живёт интерфейс.
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
    public AudioSpectrum Spectrum { get; }
    public AppIndex Launcher { get; }
    public ShelfStore Shelf { get; }
    public ReminderStore Reminders { get; }
    public SystemVolume Volume { get; }
    public PinnedStore Pinned { get; }

    /// <summary>Задачи вкладки «Заметки».</summary>
    public DotShell.Core.Tasks.TaskStore Tasks { get; }
    public WidgetStore Widgets { get; }

    /// <summary>Аудио-роутер: какие приложения звучат и на каком устройстве.</summary>
    public AudioRouter Audio { get; }

    /// <summary>HWND окон, которые сэмплер цвета должен игнорировать (иначе цвет закольцуется сам на себя).</summary>
    public List<IntPtr> OwnWindows { get; } = new();

    private ShellServices()
    {
        Settings = SettingsStore.Load();
        Theme = new ThemeBus(Settings);

        MessageWindow = new NativeMessageWindow();

        ClipboardStore = new InMemoryClipboardHistory(
            maxItems: Settings.Current.ClipboardMaxItems,
            persistAllowed: () => Settings.Current.ClipboardPersist);

        Clipboard = new ClipboardWatcher(ClipboardStore, MessageWindow);
        Hotkeys = new HotkeyManager(MessageWindow);
        Media = new MediaBridge();
        WindowWatcher = new WindowWatcher();
        Stats = new SystemStats();

        // Настоящий спектр системного аудио (WASAPI loopback). Стартует мягко:
        // если захват недоступен (RDP, нет устройства), визуализации остаются на имитации.
        Spectrum = new AudioSpectrum();
        AudioSpectrum.Current = Spectrum;

        // Индекс ярлыков ленивый: файлы читает только при первом открытии лаунчера.
        Launcher = new AppIndex();

        Shelf = new ShelfStore();
        Reminders = new ReminderStore();

        // Системная громкость для главной страницы острова и виджет-набор рабочего стола.
        Volume = new SystemVolume();
        Audio = new AudioRouter();
        Tasks = new DotShell.Core.Tasks.TaskStore();

        Pinned = new PinnedStore();
        Pinned.StartWatching();   // правки pinned.json подхватываются на лету

        WindowsAccentSync.Attach(Settings, Theme);

        // Свои курсоры — если человек их включал.
        CursorStyler.Attach(Settings);
        if (Settings.Current.CustomCursor) CursorStyler.Enable();

        // Полное оформление Windows — только если человек его включал.
        if (Settings.Current.SystemStyleFull)
        {
            DotShell.Core.Native.WindowsTheme.SetDark(true);
            DotShell.Core.Native.DarkTitleBars.Start();
        }
        Widgets = new WidgetStore();

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

        // Миграция на 0.1.8: чат теперь только Ollama, но в settings.json
        // могли остаться облачные адреса из прошлых версий (dashscope и т.п.) —
        // без этой правки остров молча опрашивал бы мёртвый сервер и список
        // моделей был бы пустым.
        if (!services.Settings.Current.AiBaseUrl.Contains("11434"))
        {
            services.Settings.Update(s =>
            {
                s.AiProvider = "Ollama";
                s.AiBaseUrl = "http://localhost:11434/v1";
            });
        }

        await services.Media.StartAsync();
        services.AccentSampler.Start();
        services.Stats.Start();
        services.Spectrum.Start();

        // Первичный замер цвета — сразу, чтобы не ждать первого тика таймера.
        services.AccentSampler.SampleOnce();

        return services;
    }

    public async ValueTask DisposeAsync()
    {
        Volume.Dispose();
        Audio.Dispose();
        Reminders.Dispose();
        Spectrum.Dispose();
        AccentSampler.Dispose();
        Stats.Dispose();
        WindowWatcher.Dispose();
        Clipboard.Dispose();
        Hotkeys.Dispose();
        await Media.DisposeAsync();
        MessageWindow.Dispose();
    }
}
