namespace DotShell.Island.Wpf;

using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using DotShell.Core;
using DotShell.Core.Clipboard;
using DotShell.Core.Launcher;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Shelf;
using DotShell.Core.Time;
using DotShell.Core.Theming;

/// <summary>
/// Состояние острова: всё, к чему привязан XAML.
///
/// Здесь намеренно нет ни Win32, ни работы с окном — только данные.
/// Это позволяет потом добавить лаунчер или панель обоев, которые будут читать
/// ту же ViewModel, не переписывая ядро.
/// </summary>
public sealed class IslandViewModel : INotifyPropertyChanged
{
    private readonly ShellServices _services;

    private bool _isExpanded;
    private string _activeTab = "home";
    private string _noteText = string.Empty;
    private string _statusLine = "готов";
    private string _mediaLine = "НИЧЕГО НЕ ИГРАЕТ";
    private string _cpuText = "CPU 0%";
    private string _batteryText = "";
    private bool _hasBattery;
    private bool _hasMedia;
    private string _trackTitle = "ничего не играет";
    private string _trackArtist = "";
    private double _progress;
    private bool _isPlaying;
    private bool _hideInFullscreen;
    private bool _autoAccent;
    private bool _showGhostDots;
    private bool _runAtStartup;
    private bool _hideDesk;
    private int _deskCheck;
    private double _cellSize = 4;
    private double _gap = 2;

    public MediaBridge Media => _services.Media;
    public ThemeBus Theme => _services.Theme;

    public ObservableCollection<ClipboardItem> ClipboardItems { get; } = new();

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }
    public string ActiveTab { get => _activeTab; set => Set(ref _activeTab, value); }
    public string StatusLine { get => _statusLine; set => Set(ref _statusLine, value); }

    public string NoteText
    {
        get => _noteText;
        set
        {
            if (!Set(ref _noteText, value)) return;
            NotesStore.Save(value);
            RaisePropertyChanged(nameof(NoteEmpty));
        }
    }

    /// <summary>Заметка пуста — показываем подсказку вместо пустого листа.</summary>
    public bool NoteEmpty => string.IsNullOrWhiteSpace(_noteText);

    // ---- свёрнутый остров ----
    public string MediaLine { get => _mediaLine; set => Set(ref _mediaLine, value); }
    public string TimeText { get => _timeText; set => Set(ref _timeText, value); }
    private string _timeText = string.Empty;
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public string BatteryText { get => _batteryText; set => Set(ref _batteryText, value); }
    public bool HasBattery { get => _hasBattery; set => Set(ref _hasBattery, value); }

    // ---- плеер ----
    public bool HasMedia { get => _hasMedia; set => Set(ref _hasMedia, value); }
    public string TrackTitle { get => _trackTitle; set => Set(ref _trackTitle, value); }
    public string TrackArtist { get => _trackArtist; set => Set(ref _trackArtist, value); }
    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public string PlayGlyph => IsPlaying ? "\uE769" : "\uE768"; // Fluent: пауза / играть

    // ---- настройки (двусторонние, пишутся сразу в settings.json) ----
    public bool HideInFullscreen
    {
        get => _hideInFullscreen;
        set { if (Set(ref _hideInFullscreen, value)) _services.Settings.Update(s => s.HideInFullscreen = value); }
    }

    public bool AutoAccent
    {
        get => _autoAccent;
        set { if (Set(ref _autoAccent, value)) _services.Settings.Update(s => s.AutoAccent = value); }
    }

    public bool ShowGhostDots
    {
        get => _showGhostDots;
        set { if (Set(ref _showGhostDots, value)) _services.Settings.Update(s => s.ShowGhostDots = value); }
    }

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            // Пишем в реестр и только при успехе обновляем состояние,
            // иначе тумблер будет показывать то, чего на самом деле нет.
            if (StartupManager.SetEnabled(value))
            {
                _services.Settings.Update(s => s.RunAtStartup = value);
                Set(ref _runAtStartup, value);
                StatusLine = value ? "автозапуск включён" : "автозапуск выключен";
            }
            else
            {
                StatusLine = "не удалось изменить автозапуск (нет доступа к реестру?)";
                RaisePropertyChanged();
            }
        }
    }

    public double CellSize
    {
        get => _cellSize;
        set { if (Set(ref _cellSize, value)) _services.Settings.Update(s => s.CellSize = (int)value); }
    }

    /// <summary>Прозрачность чёрной подложки острова, 30..100 %. Акцент не трогает.</summary>
    public double Radius
    {
        get => _services.Settings.Current.IslandRadius;
        set { _services.Settings.Update(s => s.IslandRadius = (int)Math.Clamp(value, 0, 24)); RaisePropertyChanged(); }
    }

    public double CapsuleHeight
    {
        get => _services.Settings.Current.CollapsedHeight;
        set { _services.Settings.Update(s => s.CollapsedHeight = (int)Math.Clamp(value, 36, 64)); RaisePropertyChanged(); }
    }

    public double TopOffset
    {
        get => _services.Settings.Current.IslandTopOffset;
        set { _services.Settings.Update(s => s.IslandTopOffset = (int)Math.Clamp(value, 0, 24)); RaisePropertyChanged(); }
    }

    public double AccentTemperature
    {
        get => _services.Settings.Current.AccentTemperature;
        set { _services.Settings.Update(s => s.AccentTemperature = (int)Math.Clamp(value, -30, 30)); RaisePropertyChanged(); }
    }

    public double AccentSaturation
    {
        get => _services.Settings.Current.AccentSaturation;
        set { _services.Settings.Update(s => s.AccentSaturation = (int)Math.Clamp(value, 0, 100)); RaisePropertyChanged(); }
    }

    public double DotDensity
    {
        get => _services.Settings.Current.DotTextDensity;
        set { _services.Settings.Update(s => s.DotTextDensity = (int)Math.Clamp(value, 5, 7)); RaisePropertyChanged(); }
    }

    public double NotifySeconds
    {
        get => _services.Settings.Current.NotifySeconds;
        set { _services.Settings.Update(s => s.NotifySeconds = (int)Math.Clamp(value, 3, 10)); RaisePropertyChanged(); }
    }

    public string DotLiveliness
    {
        get => _services.Settings.Current.DotLiveliness;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.DotLiveliness) return;
            _services.Settings.Update(s => s.DotLiveliness = value);
            RaisePropertyChanged();
        }
    }

    public string NotifyMode
    {
        get => _services.Settings.Current.NotifyMode;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.NotifyMode) return;
            _services.Settings.Update(s => s.NotifyMode = value);
            RaisePropertyChanged();
        }
    }

    public string NotifySound
    {
        get => _services.Settings.Current.NotifySound;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.NotifySound) return;
            _services.Settings.Update(s => s.NotifySound = value);
            RaisePropertyChanged();
        }
    }

    public string AccentFocus
    {
        get => _services.Settings.Current.AccentFocus;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.AccentFocus) return;
            _services.Settings.Update(s => s.AccentFocus = value);
            RaisePropertyChanged();
        }
    }

    public string AccentSecond
    {
        get => _services.Settings.Current.AccentSecondHex;
        set
        {
            if (value == _services.Settings.Current.AccentSecondHex) return;
            _services.Settings.Update(s => s.AccentSecondHex = value ?? string.Empty);
            RaisePropertyChanged();
        }
    }

    public string DashboardTabs
    {
        get => _services.Settings.Current.DashboardTabs;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.DashboardTabs) return;
            _services.Settings.Update(s => s.DashboardTabs = value);
            RaisePropertyChanged();
        }
    }

    public string OpenDirection
    {
        get => _services.Settings.Current.OpenDirection;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.OpenDirection) return;
            _services.Settings.Update(s => s.OpenDirection = value);
            RaisePropertyChanged();
        }
    }

    public string TabTransition
    {
        get => _services.Settings.Current.TabTransition;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.TabTransition) return;
            _services.Settings.Update(s => s.TabTransition = value);
            RaisePropertyChanged();
        }
    }

    public string AutoHide
    {
        get => _services.Settings.Current.AutoHide;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.AutoHide) return;
            _services.Settings.Update(s => s.AutoHide = value);
            RaisePropertyChanged();
        }
    }

    public string QuietThreshold
    {
        get => _services.Settings.Current.QuietThreshold;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.QuietThreshold) return;
            _services.Settings.Update(s => s.QuietThreshold = value);
            RaisePropertyChanged();
        }
    }

    public string ClickAction
    {
        get => _services.Settings.Current.ClickAction;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.ClickAction) return;
            _services.Settings.Update(s => s.ClickAction = value);
            RaisePropertyChanged();
        }
    }

    public string CoverShape
    {
        get => _services.Settings.Current.CoverShape;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.CoverShape) return;
            _services.Settings.Update(s => s.CoverShape = value);
            RaisePropertyChanged();
        }
    }

    public double WidgetGrid
    {
        get => _services.Settings.Current.WidgetGrid;
        set { _services.Settings.Update(s => s.WidgetGrid = (int)Math.Clamp(value, 0, 16)); RaisePropertyChanged(); }
    }

    public string AnimCurve
    {
        get => _services.Settings.Current.AnimationCurve;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.AnimationCurve) return;
            _services.Settings.Update(s => s.AnimationCurve = value);
            RaisePropertyChanged();
        }
    }

    public string DotShapeName
    {
        get => _services.Settings.Current.DotShape;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.DotShape) return;
            _services.Settings.Update(s => s.DotShape = value);
            RaisePropertyChanged();
        }
    }

    public bool DotStagger
    {
        get => _services.Settings.Current.DotStagger;
        set { _services.Settings.Update(s => s.DotStagger = value); RaisePropertyChanged(); }
    }

    public double AnimSpeed
    {
        get => _services.Settings.Current.AnimationSpeed;
        set { _services.Settings.Update(s => s.AnimationSpeed = Math.Clamp(value, 0.5, 2)); RaisePropertyChanged(); }
    }

    public double IslandOpacity
    {
        get => _islandOpacity;
        set { if (Set(ref _islandOpacity, value)) _services.Settings.Update(s => s.IslandOpacity = (int)value); }
    }
    private double _islandOpacity;

    /// <summary>Рисунок внутри острова (waves/topo/rays/aurora/grid/none).</summary>
    public string BackdropStyle
    {
        get => _services.Settings.Current.BackdropStyle;
        set
        {
            if (value == _services.Settings.Current.BackdropStyle) return;
            _services.Settings.Update(s => s.BackdropStyle = value);
            RaisePropertyChanged(nameof(BackdropStyle));
            BackdropChanged?.Invoke();
        }
    }

    /// <summary>Плотность рисунка, 0..100.</summary>
    public double BackdropOpacity
    {
        get => _backdropOpacity;
        set
        {
            if (!Set(ref _backdropOpacity, value)) return;
            _services.Settings.Update(s => s.BackdropOpacity = (int)value);
            BackdropChanged?.Invoke();
        }
    }
    private double _backdropOpacity;

    /// <summary>Раскладка плеера: cover / dots / row.</summary>
    public string PlayerStyle
    {
        get => _services.Settings.Current.PlayerStyle;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _services.Settings.Current.PlayerStyle) return;
            _services.Settings.Update(s => s.PlayerStyle = value);
            RaisePropertyChanged(nameof(PlayerStyle));
            PlayerChanged?.Invoke();
        }
    }

    /// <summary>Раскладку плеера поменяли — пора пересобрать шапку.</summary>
    public event Action? PlayerChanged;
    /// <summary>Рисунок поменяли — окну пора перерисовать подложку.</summary>
    public event Action? BackdropChanged;

    public double Gap
    {
        get => _gap;
        set { if (Set(ref _gap, value)) _services.Settings.Update(s => s.Gap = (int)value); }
    }

    public IslandViewModel(ShellServices services)
    {
        _services = services;

        // Список задач: перерисовываем при любом изменении хранилища.
        services.Tasks.Changed += () => RunOnUiThread(RebuildTasks);
        RebuildTasks();

        var s = services.Settings.Current;
        _hideInFullscreen = s.HideInFullscreen;
        _autoAccent = s.AutoAccent;
        _showGhostDots = s.ShowGhostDots;
        _runAtStartup = StartupManager.IsEnabled();
        _hideDesk = s.HideDesktopIcons;
        _hotkeyHud = s.HotkeyHudEnabled;
        _cellSize = s.CellSize;
        _gap = s.Gap;
        _islandOpacity = s.IslandOpacity;
        _backdropOpacity = s.BackdropOpacity;

        NoteText = NotesStore.Load();
        TimeText = DateTime.Now.ToString("HH:mm");

        if (_timerEnd is { } end)
        {
            var left = end - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero)
            {
                StopTimer();
                StatusLine = "таймер: время вышло!";
                TimerFired?.Invoke();
            }
            else
            {
                TimerText = left >= TimeSpan.FromHours(1)
                    ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
                    : $"{left.Minutes}:{left.Seconds:00}";

                // Прогресс для циферблата: прошло / вся длительность.
                if (_timerTotal > TimeSpan.Zero)
                    TimerProgress = Math.Clamp(1 - left.TotalSeconds / _timerTotal.TotalSeconds, 0, 1);
            }
        }

        services.ClipboardStore.Changed += RebuildClipboard;
        services.Media.PropertyChanged += OnMediaChanged;
        services.Shelf.Changed += RebuildShelf;
        services.Reminders.Changed += RebuildReminders;
        services.Pinned.Changed += RebuildPinned;
        services.Launcher.Changed += () => RunOnUiThread(() => SearchPicker(_pickerQuery));

        RebuildClipboard();
        RebuildShelf();
        RebuildReminders();
        RebuildPinned();

        // Рабочий стол без иконок: применяем сразу и поддерживаем состояние
        // тиком (Explorer может перезапуститься и "забыть" нашу команду).
        if (_hideDesk) DesktopIcons.SetHidden(true);
        OnMediaChanged(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>Раз в секунду из окна: обновляем телеметрию, прогресс трека и подписи.</summary>
    public void Tick()
    {
        _services.Media.Tick();

        TimeText = DateTime.Now.ToString("HH:mm");

        if (_timerEnd is { } end)
        {
            var left = end - DateTimeOffset.Now;
            if (left <= TimeSpan.Zero)
            {
                StopTimer();
                StatusLine = "таймер: время вышло!";
                TimerFired?.Invoke();
            }
            else
            {
                TimerText = left >= TimeSpan.FromHours(1)
                    ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
                    : $"{left.Minutes}:{left.Seconds:00}";

                // Прогресс для точечного циферблата. Считается здесь, в тике:
                // в конструкторе эта ветка выполняется один раз и круг стоит.
                if (_timerTotal > TimeSpan.Zero)
                    TimerProgress = Math.Clamp(1 - left.TotalSeconds / _timerTotal.TotalSeconds, 0, 1);
            }
        }
        _deskCheck = (_deskCheck + 1) % 5;
        if (_deskCheck == 0 && _hideDesk && !DesktopIcons.IsHidden()) DesktopIcons.SetHidden(true);

        CpuText = $"CPU {_services.Stats.Cpu:F0}%";
        HasBattery = _services.Stats.HasBattery;
        if (HasBattery)
            BatteryText = $"{(_services.Stats.Charging ? "+" : "")}BAT {_services.Stats.Battery}%";

        Progress = _services.Media.Duration > TimeSpan.Zero
            ? Math.Clamp(_services.Media.Position.TotalSeconds / _services.Media.Duration.TotalSeconds * 100, 0, 100)
            : 0;

        foreach (var item in ClipboardItems) item.RefreshAge();

        StatusLine =
            $"RAM {_services.Stats.UsedMemoryMb}/{_services.Stats.TotalMemoryMb} МБ   ·   " +
            $"AUTO-COLOR {(AutoAccent ? $"ON ({_services.AccentSampler.LastConfidence:F2})" : "OFF")} · {_services.AccentSampler.Source}   ·   " +
            $"ACCENT {_services.Theme.AccentHex}   ·   " +
            $"FOCUS {(string.IsNullOrEmpty(_services.WindowWatcher.ForegroundProcessName) ? "—" : _services.WindowWatcher.ForegroundProcessName)}";
    }

    private void OnMediaChanged(object? sender, PropertyChangedEventArgs e)
    {
        var media = _services.Media;
        RunOnUiThread(() =>
        {
            HasMedia = media.HasSession;
            IsPlaying = media.IsPlaying;
            TrackTitle = string.IsNullOrWhiteSpace(media.Title) ? "ничего не играет" : media.Title;
            TrackArtist = string.IsNullOrWhiteSpace(media.Artist)
                ? media.SourceApp
                : media.Artist;

            var line = string.IsNullOrWhiteSpace(media.Title) ? "НИЧЕГО НЕ ИГРАЕТ" : media.Title.ToUpperInvariant();
            MediaLine = line.Length > 30 ? line[..30] + "…" : line;

            RaisePropertyChanged(nameof(PlayGlyph));
        });
    }

    private void RebuildClipboard()
    {
        RunOnUiThread(() =>
        {
            var items = _services.ClipboardStore.Items;
            ClipboardItems.Clear();
            foreach (var item in items.Take(80)) ClipboardItems.Add(item);
        });
        RebuildSmartActions();
    }

    // ---- алхимия буфера: умные действия над свежей текстовой копией ----

    public ObservableCollection<SmartActionRow> SmartActions { get; } = new();

    public event Action? SmartActionsChanged;

    private void RebuildSmartActions()
    {
        var top = _services.ClipboardStore.Items.FirstOrDefault(i => i.Kind == ClipboardKind.Text);
        var detected = top is null ? new List<SmartAction>() : SmartClip.Detect(top.Text);
        var target = top?.Text ?? string.Empty;
        RunOnUiThread(() =>
        {
            SmartActions.Clear();
            foreach (var a in detected) SmartActions.Add(new SmartActionRow(a, target));
            SmartActionsChanged?.Invoke();
        });
    }

    public void ExecuteSmartAction(SmartActionRow row)
    {
        if (SmartClip.IsLaunch(row.Id))
        {
            try
            {
                Process.Start(new ProcessStartInfo(row.Target) { UseShellExecute = true });
                StatusLine = "открыто в браузере";
            }
            catch (Exception ex) { StatusLine = $"браузер не открылся: {ex.Message}"; }
            return;
        }

        var text = SmartClip.Transform(row.Id, row.Target);
        if (text is null) return;
        try { System.Windows.Clipboard.SetText(text); } catch { }
        var preview = text.Length > 48 ? text[..48] + "…" : text;
        StatusLine = $"алхимия: {row.Label} → {preview}";
    }

    /// <summary>Строка умного действия для привязки в окне.</summary>
    public sealed class SmartActionRow
    {
        public SmartActionRow(SmartAction action, string target)
        {
            Id = action.Id;
            Label = action.Label;
            Target = target;
        }

        public string Id { get; }
        public string Label { get; }
        public string Target { get; }
    }

    public void SearchClipboard(string query)
    {
        var found = _services.ClipboardStore.Search(query, 80);
        ClipboardItems.Clear();
        foreach (var item in found) ClipboardItems.Add(item);
    }

    public void UseClipboardItem(ClipboardItem item)
    {
        _services.Clipboard.PushToClipboard(item);
        StatusLine = $"в буфере: {item.Preview}";
    }

    public void TogglePin(ClipboardItem item)
    {
        _services.ClipboardStore.SetPinned(item.Id, !item.Pinned);
        StatusLine = item.Pinned ? "закреплено" : "откреплено";
    }

    public void ClearClipboard()
    {
        _services.ClipboardStore.ClearUnpinned();
        StatusLine = "буфер очищен (закреплённые остались)";
    }

    /// <summary>
    /// Применить скин: акцент + размеры точек + рисунок подложки и её плотность.
    /// Всё пишется в настройки, а ThemeBus рассылает изменения подписчикам,
    /// поэтому перекрашивается и остров, и виджеты, и часы — без перезапуска.
    /// </summary>
    public void ApplySkin(Skin skin)
    {
        _services.Settings.Update(s =>
        {
            s.Skin = skin.Id;
            s.AutoAccent = skin.AutoAccent;
            s.AccentHex = skin.AccentHex;
            s.CellSize = skin.CellSize;
            s.Gap = skin.Gap;
            s.BackdropStyle = skin.BackdropStyle;
            s.BackdropOpacity = skin.BackdropOpacity;
            s.IslandOpacity = skin.IslandOpacity;
        });

        _services.Theme.SetAccent(ThemeBus.ParseHex(skin.AccentHex), "skin");

        _autoAccent = skin.AutoAccent;
        _cellSize = skin.CellSize;
        _gap = skin.Gap;
        _backdropOpacity = skin.BackdropOpacity;
        _islandOpacity = skin.IslandOpacity;

        RaisePropertyChanged(nameof(AutoAccent));
        RaisePropertyChanged(nameof(CellSize));
        RaisePropertyChanged(nameof(Gap));
        RaisePropertyChanged(nameof(BackdropOpacity));
        RaisePropertyChanged(nameof(IslandOpacity));
        RaisePropertyChanged(nameof(BackdropStyle));

        BackdropChanged?.Invoke();
        StatusLine = $"скин: {skin.Name}";
    }
    public void ApplyAccentPreset(string hex)
    {
        // Ручной выбор цвета обязан выключать авто-подбор,
        // иначе обои перекрасят всё обратно через пять секунд.
        _services.Settings.Update(s =>
        {
            s.AutoAccent = false;
            s.AccentHex = hex;
        });
        AutoAccent = false;
        _services.Theme.SetAccent(ThemeBus.ParseHex(hex), "preset");
        StatusLine = $"акцент {hex}";
    }

    // ------------------------------------------------------------------
    //  Быстрый чат с ИИ (вкладка ИИ)
    //
    //  Без агента и инструментов: просто переписка с моделью через любой
    //  OpenAI-совместимый эндпоинт (Qwen / OpenRouter / OpenAI / Ollama /
    //  свой адрес). Ключ лежит локально в settings.json (AiApiKey)
    //  и уходит только на указанный адрес API.
    // ------------------------------------------------------------------

    private readonly AiChat _chat = new();

    public ObservableCollection<ChatLine> ChatLines { get; } = new();

    public bool ChatBusy { get => _chatBusy; set => Set(ref _chatBusy, value); }
    private bool _chatBusy;

    public string AiModel
    {
        get => _services.Settings.Current.AiModel;
        set => _services.Settings.Update(s => s.AiModel = value);
    }

    public async Task ChatSendAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || ChatBusy) return;

        ChatBusy = true;
        ChatLines.Add(new ChatLine("user", text));
        try
        {
            var s = _services.Settings.Current;
            if (string.IsNullOrWhiteSpace(s.AiModel))
                throw new AiChatException("Сначала выбери модель: кнопка ⟳ сверху вкладки ИИ.");
            var baseUrl = s.AiBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                baseUrl = AiChat.Providers.FirstOrDefault(p => p.Name == s.AiProvider).Url;
            var reply = await _chat.SendAsync(text, baseUrl, s.AiModel, s.AiApiKey);
            RunOnUiThread(() => ChatLines.Add(new ChatLine("qwen", reply)));
        }
        catch (Exception ex)
        {
            var msg = ex is AiChatException q ? q.Message : ex.Message;
            RunOnUiThread(() => ChatLines.Add(new ChatLine("error", msg)));
        }
        finally
        {
            RunOnUiThread(() => ChatBusy = false);
        }
    }

    // ------------------------------------------------------------------
    //  Главная страница: мои программы (плитки) и выбор из индекса
    // ------------------------------------------------------------------

    public ObservableCollection<PinnedRow> PinnedItems { get; } = new();

    /// <summary>Окно перестраивает плитки после любого изменения списка.</summary>
    public event Action? PinnedChanged;

    private void RebuildPinned()
    {
        RunOnUiThread(() =>
        {
            PinnedItems.Clear();
            foreach (var p in _services.Pinned.Items) PinnedItems.Add(new PinnedRow(p));
            PinnedChanged?.Invoke();
        });
    }

    public void LaunchPinned(PinnedRow row)
    {
        try
        {
            // Папку (и сам проводник) открываем вкладкой в уже открытом окне,
            // если Windows это умеет и человек не отключил поведение.
            bool isFolder = Directory.Exists(row.Target) ||
                            row.Target.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase);

            if (isFolder)
            {
                var folder = Directory.Exists(row.Target)
                    ? row.Target
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                ExplorerLauncher.Open(folder, _services.Settings.Current.ExplorerTabs);
                StatusLine = $"открываю: {row.Name}";
                return;
            }

            Process.Start(new ProcessStartInfo(row.Target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(row.Target) ?? string.Empty,
            });
            var entry = _services.Launcher.Entries.FirstOrDefault(e =>
                string.Equals(e.Target, row.Target, StringComparison.OrdinalIgnoreCase));
            if (entry is not null) _services.Launcher.Bump(entry); // частые — выше в лаунчере
            StatusLine = $"запуск: {row.Name}";
        }
        catch (Exception ex)
        {
            StatusLine = $"не удалось запустить: {row.Name}";
            Log.Info($"pinned launch failed for {row.Target}: {ex.Message}");
        }
    }

    public void Unpin(PinnedRow row)
    {
        _services.Pinned.Remove(row.Row);
        StatusLine = $"с острова: {row.Name}";
    }

    // ---- выбор программы из индекса меню «Пуск» ----

    public ObservableCollection<LauncherRow> PickerResults { get; } = new();
    private string _pickerQuery = string.Empty;

    public void PickerOpen()
    {
        _services.Launcher.EnsureReady();
        SearchPicker(_pickerQuery);
    }

    public void SearchPicker(string query)
    {
        _pickerQuery = query;
        PickerResults.Clear();
        foreach (var e in _services.Launcher.Search(query, 14)) PickerResults.Add(new LauncherRow(e));
    }

    public void PinFromPicker(LauncherRow row)
    {
        _services.Pinned.Add(row.Name, row.Target, row.Entry.Shortcut);
        StatusLine = $"закреплено: {row.Name}";
    }

    // ------------------------------------------------------------------
    //  Фокус-шторки и HUD хоткеев (переключаются из острова)
    // ------------------------------------------------------------------

    public bool FocusMode
    {
        get => FocusBlinders.IsOn;
        set
        {
            FocusBlinders.SetEnabled(value);
            RaisePropertyChanged();
            StatusLine = value ? "фокус: лишние мониторы затемнены" : "фокус выключен";
        }
    }

    public bool HotkeyHudEnabled
    {
        get => _hotkeyHud;
        set
        {
            if (!Set(ref _hotkeyHud, value)) return;
            _services.Settings.Update(s => s.HotkeyHudEnabled = value);
            HotkeyHud.SetEnabled(value);
            StatusLine = value ? "подсказки хоткеев включены" : "подсказки хоткеев выключены";
        }
    }
    private bool _hotkeyHud;

    // ------------------------------------------------------------------
    //  Иконки рабочего стола
    // ------------------------------------------------------------------

    public bool HideDesktopIcons
    {
        get => _hideDesk;
        set
        {
            if (!Set(ref _hideDesk, value)) return;
            _services.Settings.Update(s => s.HideDesktopIcons = value);
            DesktopIcons.SetHidden(value);
            StatusLine = value ? "рабочий стол: иконки спрятаны" : "рабочий стол: иконки на месте";
        }
    }

    // ------------------------------------------------------------------
    //  Полка для файлов
    // ------------------------------------------------------------------

    public ObservableCollection<ShelfRow> ShelfItems { get; } = new();

    private void RebuildShelf()
    {
        RunOnUiThread(() =>
        {
            ShelfItems.Clear();
            foreach (var f in _services.Shelf.Items) ShelfItems.Add(new ShelfRow(f));
        });
    }

    public void ShelfRemove(ShelfRow row)
    {
        _services.Shelf.Remove(row.File);
        StatusLine = $"с полки: {row.Name}";
    }

    public void ShelfClear()
    {
        _services.Shelf.Clear();
        StatusLine = "полка пуста";
    }

    // ------------------------------------------------------------------
    //  Таймер в пилюле
    // ------------------------------------------------------------------

    private DateTimeOffset? _timerEnd;
    private TimeSpan _timerTotal;

    public string TimerText
    {
        get => _timerText;
        set
        {
            if (!Set(ref _timerText, value)) return;
            RaisePropertyChanged(nameof(TimerLabel));
        }
    }

    /// <summary>Пройденная доля таймера 0..1 — для точечного циферблата.</summary>
    public double TimerProgress
    {
        get => _timerProgress;
        private set => Set(ref _timerProgress, value);
    }

    private double _timerProgress;

    /// <summary>Подпись таймера: когда он стоит, всё равно видно «00:00», а не пустоту.</summary>
    public string TimerLabel => string.IsNullOrWhiteSpace(_timerText) ? "00:00" : _timerText;
    private string _timerText = string.Empty;

    public bool TimerActive { get => _timerActive; set => Set(ref _timerActive, value); }
    private bool _timerActive;

    /// <summary>Время вышло — окно покажет пузырь над треем.</summary>
    public event Action? TimerFired;

    public void StartTimer(double minutes)
    {
        if (minutes <= 0 || minutes > 24 * 60) { StatusLine = "таймер: от 1 минуты до суток"; return; }
        // Без этого прогресс циферблата считался от нуля и всегда был 0:
        // круг стоял на месте, а цифры честно шли вниз.
        _timerTotal = TimeSpan.FromMinutes(minutes);
        _timerEnd = DateTimeOffset.Now.AddMinutes(minutes);
        TimerActive = true;
        StatusLine = $"таймер на {minutes:F0} мин";
    }

    public void StopTimer()
    {
        _timerEnd = null;
        TimerActive = false;
        TimerText = string.Empty;
    }

    // ------------------------------------------------------------------
    //  Напоминания одной фразой
    // ------------------------------------------------------------------

    public ObservableCollection<Reminder> Reminders { get; } = new();

    private void RebuildReminders()
    {
        RunOnUiThread(() =>
        {
            Reminders.Clear();
            foreach (var r in _services.Reminders.Items.OrderBy(r => r.FireAt)) Reminders.Add(r);
        });
    }

    public bool AddReminder(string phrase)
    {
        var ok = _services.Reminders.Add(phrase);
        StatusLine = ok ? "напоминание поставлено" : "не понял фразу: попробуй «каждый день в 23:00» или «через 20 минут»";
        return ok;
    }

    public void RemoveReminder(Reminder r) => _services.Reminders.Remove(r);

    // ------------------------------------------------------------------
    //  Командная строка
    // ------------------------------------------------------------------

    public string CmdLog { get => _cmdLog; set => Set(ref _cmdLog, value); }
    private string _cmdLog = string.Empty;

    public bool CmdBusy { get => _cmdBusy; set => Set(ref _cmdBusy, value); }
    private bool _cmdBusy;

    /// <summary>Разовая команда через cmd /c: без сессии, зато предсказуемо.</summary>
    public async Task RunCmdAsync(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || CmdBusy) return;
        CmdBusy = true;
        CmdLog += $"\n> {command}\n";

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var process = System.Diagnostics.Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            CmdLog += stdout;
            if (!string.IsNullOrWhiteSpace(stderr)) CmdLog += stderr;
        }
        catch (Exception ex)
        {
            CmdLog += $"ошибка: {ex.Message}\n";
        }

        // Хвост лога обрежаем: консоль в острове — не терминал истории.
        if (CmdLog.Length > 8000) CmdLog = CmdLog[^8000..];
        CmdBusy = false;
    }

    // ------------------------------------------------------------------
    //  Задачи (вкладка «Заметки»)
    // ------------------------------------------------------------------

    public ObservableCollection<DotShell.Core.Tasks.TaskItem> TaskList { get; } = new();

    public void AddTask(string text) => _services.Tasks.Add(text);

    public void ToggleTask(DotShell.Core.Tasks.TaskItem item) => _services.Tasks.Toggle(item);

    public void RemoveTask(DotShell.Core.Tasks.TaskItem item) => _services.Tasks.Remove(item);

    private void RebuildTasks()
    {
        TaskList.Clear();
        foreach (var task in _services.Tasks.Items) TaskList.Add(task);
    }
    // ------------------------------------------------------------------
    //  Тёмная тема Windows
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    //  Настройки из окна (оформление Windows, уведомления, обновления)
    // ------------------------------------------------------------------

    /// <summary>Оформление Windows: тёмные заголовки всех окон.</summary>
    public bool FullStyleOn
    {
        get => _services.Settings.Current.SystemStyleFull;
        set
        {
            _services.Settings.Update(s => s.SystemStyleFull = value);
            if (value)
            {
                DotShell.Core.Native.WindowsTheme.SetDark(true);
                DotShell.Core.Native.DarkTitleBars.Start();
            }
            else
            {
                DotShell.Core.Native.DarkTitleBars.Stop();
                DotShell.Core.Native.DarkTitleBars.RestoreAll();
            }

            RaisePropertyChanged();
            StatusLine = value ? "оформление Windows: тёмные заголовки" : "оформление Windows: выключено";
        }
    }

    /// <summary>Акцент Windows = цвет обоев.</summary>
    public bool WindowsAccentOn
    {
        get => _services.Settings.Current.WindowsAccentSync;
        set
        {
            if (value) WindowsAccentSync.Enable(); else WindowsAccentSync.Disable();
            RaisePropertyChanged();
            StatusLine = value ? "акцент Windows: цвет обоев" : "акцент Windows: прежний";
        }
    }

    /// <summary>Свои курсоры.</summary>
    public bool CustomCursorOn
    {
        get => _services.Settings.Current.CustomCursor;
        set
        {
            if (value) CursorStyler.Enable(); else CursorStyler.Disable();
            RaisePropertyChanged();
            StatusLine = value ? "курсор: точка" : "курсор: системный";
        }
    }

    /// <summary>Уведомления Windows (тосты).</summary>
    public bool ToastOn
    {
        get => _services.Settings.Current.ToastEnabled;
        set
        {
            _services.Settings.Update(s => s.ToastEnabled = value);
            Toast.Enabled = value;
            RaisePropertyChanged();
            StatusLine = value ? "уведомления: тосты Windows" : "уведомления: только пузырь трея";
        }
    }

    public bool UpdateCheckOnStart
    {
        get => _services.Settings.Current.UpdateCheckOnStart;
        set { _services.Settings.Update(s => s.UpdateCheckOnStart = value); RaisePropertyChanged(); }
    }

    public int UpdateCheckHours
    {
        get => _services.Settings.Current.UpdateCheckHours;
        set
        {
            var hours = Math.Clamp(value, 1, 24);
            _services.Settings.Update(s => s.UpdateCheckHours = hours);
            RaisePropertyChanged();
            RaisePropertyChanged(nameof(UpdateCheckHoursText));
        }
    }

    public string UpdateCheckHoursText => $"раз в {UpdateCheckHours} ч";

    public bool WeatherFahrenheit
    {
        get => _services.Settings.Current.WeatherFahrenheit;
        set
        {
            _services.Settings.Update(s => s.WeatherFahrenheit = value);
            DotShell.Core.Weather.WeatherService.ClearCache();
            DotShell.Island.Wpf.Widgets.WidgetsManager.RefreshAll();
            RaisePropertyChanged();
        }
    }

    public bool TrayHideHelpers
    {
        get => _services.Settings.Current.TrayHideHelpers;
        set
        {
            _services.Settings.Update(s => s.TrayHideHelpers = value);
            DotShell.Island.Wpf.Widgets.WidgetsManager.RefreshAll();
            RaisePropertyChanged();
        }
    }

    /// <summary>Перечитать настройки и обновить привязки (после сброса оформления).</summary>
    public void RefreshFromSettings()
    {
        AutoAccent = _services.Settings.Current.AutoAccent;
        ShowGhostDots = _services.Settings.Current.ShowGhostDots;
        CellSize = _services.Settings.Current.CellSize;
        Gap = _services.Settings.Current.Gap;
        IslandOpacity = _services.Settings.Current.IslandOpacity;
        BackdropOpacity = _services.Settings.Current.BackdropOpacity;
        RaisePropertyChanged(nameof(FullStyleOn));
        RaisePropertyChanged(nameof(WindowsAccentOn));
        RaisePropertyChanged(nameof(CustomCursorOn));
        RaisePropertyChanged(nameof(UpdateCheckOnStart));
        RaisePropertyChanged(nameof(UpdateCheckHours));
    }
    /// <summary>
    /// Устройство для эквалайзера: пусто — системное по умолчанию.
    /// Переключение перезапускает захват, иначе он остаётся на старом устройстве.
    /// </summary>
    public string AudioDeviceId
    {
        get => _services.Settings.Current.AudioDeviceId;
        set
        {
            var id = value ?? string.Empty;
            _services.Settings.Update(s => s.AudioDeviceId = id);

            if (DotShell.Core.Audio.AudioSpectrum.Current is { } spectrum)
            {
                spectrum.Stop();
                spectrum.DeviceId = id.Length == 0 ? null : id;
                spectrum.Start();
            }

            RaisePropertyChanged();
            StatusLine = id.Length == 0
                ? "эквалайзер: системное устройство"
                : "эквалайзер: выбранное устройство";
        }
    }
    /// <summary>Режим «не мешать»: сжиматься, когда на переднем плане чужое окно.</summary>
    public bool QuietOnMaximized
    {
        get => _services.Settings.Current.QuietOnMaximized;
        set
        {
            _services.Settings.Update(s => s.QuietOnMaximized = value);
            RaisePropertyChanged();
            StatusLine = value
                ? "не мешать: сжимаюсь при развёрнутом окне"
                : "не мешать: выключено, остров всегда на месте";
        }
    }
    /// <summary>Открывать папки вкладкой в уже открытом проводнике.</summary>
    public bool ExplorerTabs
    {
        get => _services.Settings.Current.ExplorerTabs;
        set
        {
            _services.Settings.Update(s => s.ExplorerTabs = value);
            RaisePropertyChanged();
            StatusLine = value
                ? "папки: вкладкой в проводнике (Windows 11)"
                : "папки: новым окном, как в системе";
        }
    }
    public bool SystemDark
    {
        get => DotShell.Core.Native.WindowsTheme.IsDarkApps();
        set
        {
            DotShell.Core.Native.WindowsTheme.SetDark(value);
            RaisePropertyChanged();
            StatusLine = value ? "windows: тёмная тема" : "windows: светлая тема";
        }
    }

    /// <summary>Плитка закреплённой программы для главной страницы.</summary>
    public sealed class PinnedRow
    {
        public PinnedRow(PinnedApp app)
        {
            Row = app;
            Name = app.Name;
            Target = app.Target;
            Hotkey = app.Hotkey;
            Icon = LoadIcon(app.IconPath);
        }

        public PinnedApp Row { get; }
        public string Name { get; }
        public string Target { get; }
        public string Hotkey { get; }
        public BitmapSource? Icon { get; }

        private static BitmapSource? LoadIcon(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 48;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
    }

    /// <summary>Файл на полке для привязки в XAML.</summary>
    public sealed class ShelfRow
    {
        public ShelfRow(ShelfFile file) { File = file; }
        public ShelfFile File { get; }
        public string Name => File.Name;
        public string Path => File.Path;
        public string SizeLabel => File.SizeLabel;
    }

    /// <summary>Одна реплика быстрого чата для привязки в XAML.</summary>
    public sealed class ChatLine
    {
        public ChatLine(string who, string text)
        {
            Who = who;
            Text = text;
            Stamp = DateTime.Now.ToString("HH:mm");
        }

        public string Who { get; }
        public string Text { get; }

        /// <summary>Время реплики — мелкой цифрой рядом с меткой автора.</summary>
        public string Stamp { get; }

        public bool IsUser => Who == "user";
        public bool IsError => Who == "error";
        public bool IsAi => Who == "qwen";

        /// <summary>Что показать слева вместо пузыря: подпись тоном ниже.</summary>
        public string WhoLabel => Who switch
        {
            "user" => "ТЫ",
            "qwen" => "ИИ",
            _ => "ОШИБКА"
        };
    }

    private void RunOnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Заметки = обычный markdown-файл.
/// Специально не база данных: файл открывается в любом редакторе, синхронизируется
/// через OneDrive/git и переживает переустановку программы.
/// </summary>
public static class NotesStore
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotShell", "notes.md");

    public static string Load()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path) : string.Empty; }
        catch { return string.Empty; }
    }

    public static void Save(string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, text);
        }
        catch { }
    }
}