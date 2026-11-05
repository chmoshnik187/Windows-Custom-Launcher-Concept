namespace DotShell.Island.Wpf;

// WinForms подключён в проекте ради Screen, и его глобальные using приносят
// System.Windows.Forms.* — из-за этого MouseEventArgs/KeyEventArgs/DragEventArgs
// становятся неоднозначными. Псевдонимы возвращают привычные WPF-типы.
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

/// <summary>
/// Главное (и единственное видимое) окно острова.
///
/// Как устроена анимация раскрытия — это самое интересное место файла:
/// окно ВСЕГДА одного размера (700×380), а анимируется только Width/Height
/// бордера-таблетки. Раскрытое содержимое лежит в слое фиксированного размера
/// и во время анимации просто обрезается краями таблетки (ClipToBounds).
/// Поэтому WPF не пересчитывает layout 60 раз в секунду и анимация плавная
/// даже на слабом железе.
///
/// Окно создано с AllowsTransparency=True: клики по полностью прозрачным
/// пикселям проходят сквозь окно сами, без всякой магии с hit-test.
/// </summary>
public partial class IslandWindow : Window
{
    // ---- геометрия таблетки (совпадает со стартовыми значениями в XAML) ----
    private const double CollapsedWidth = 440;
    /// <summary>
    /// Высота свёрнутой капсулы: живёт в настройках (36…64), поэтому не const.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _autoHideTimer;

    /// <summary>Таймер автоскрытия: подписка на тик живёт вместе с окном, а не с каждым раскрытием.</summary>
    private System.Windows.Threading.DispatcherTimer CreateAutoHideTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_isExpanded && !IsMouseOver) Collapse();
        };

        return timer;
    }

    private static double CollapsedHeight =>
        Math.Clamp(ShellServices.Instance.Settings.Current.CollapsedHeight, 36, 64);
    private const double ExpandedWidth = 640;   // 620 содержимое + поля
    private const double ExpandedHeight = 306;  // 10 отступ + 296 содержимое
    /// <summary>Длительность анимаций с поправкой на пользовательскую скорость.</summary>
    private static int AnimationMs
    {
        get
        {
            double speed = Math.Clamp(ShellServices.Instance.Settings.Current.AnimationSpeed, 0.5, 2);
            return (int)Math.Clamp(300 / speed, 100, 900);
        }
    }

    /// <summary>Кривая движения из настроек: out / inout / linear / spring.</summary>
    private static IEasingFunction? Ease()
    {
        return ShellServices.Instance.Settings.Current.AnimationCurve switch
        {
            "linear" => null,
            "inout" => new CubicEase { EasingMode = EasingMode.EaseInOut },
            "spring" => new BackEase { Amplitude = 0.4, EasingMode = EasingMode.EaseOut },
            _ => new CubicEase { EasingMode = EasingMode.EaseOut },
        };
    }

    /// <summary>
    /// Размер раскрытого острова под задачу вкладки.
    ///
    /// Раньше все вкладки жили в одном прямоугольнике 640×306: заметкам было
    /// тесно по высоте, главной с плитками программ — тесно по ширине, а чату
    /// приходилось вытягиваться вниз вручную. Теперь у каждой вкладки свой
    /// размер, и остров перетекает между ними той же анимацией, что и при
    /// раскрытии: капсула подстраивается под то, что ты делаешь.
    /// </summary>
    private static readonly Dictionary<string, (double Width, double Height)> TabSizes = new()
    {
        ["home"] = (780, 600),
        ["notes"] = (660, 520),
        ["clipboard"] = (560, 500),
        ["shelf"] = (560, 412),
        ["cmd"] = (720, 432),
        ["system"] = (660, 512),
        ["theme"] = (640, 472),
        ["chat"] = (700, 480),
    };

    private const int WM_MOUSEACTIVATE = 0x0021;

    private readonly ShellServices _services;
    private readonly IslandViewModel _vm;
    private readonly DispatcherTimer _tickTimer;

    private IntPtr _hwnd;
    private IntPtr _lastForeground = IntPtr.Zero; // куда возвращать фокус после вставки из буфера
    private bool _isExpanded;
    private bool _hiddenByFullscreen;

    /// <summary>Остров сжат в полоску из-за развёрнутого чужого окна.</summary>
    private bool _quiet;

    private System.Windows.Threading.DispatcherTimer? _quietRestoreTimer;
    private string? _lastSpectrumDiag;
    private bool _updateShown;

    /// <summary>Точки «в группе что-то запущено» — обновляются тиком.</summary>
    private readonly List<(System.Windows.Shapes.Ellipse Dot, DotShell.Core.Launcher.PinnedGroup Group)> _groupDots = new();

    /// <summary>Насколько заметки не влезают в базовую высоту вкладки.</summary>
    private double _notesExtra;
    private readonly DispatcherTimer _topmostWatchdog;
    private TrayIcon? _tray;

    // ---- докинг у края экрана: остров утоплен за верхнюю кромку, торчит только
    //      эквалайзер-сталактит; наведение мыши плавно вытаскивает остров обратно ----
    private bool _docked;
    private bool _peeked;
    private double _dockTop;
    private double _normalLeft;
    private double _normalTop;

    // ---- раскрытие переменной высоты (чат растёт вместе с перепиской) ----
    private double _expandedH = ExpandedHeight;
    private readonly DispatcherTimer _growTimer;
    private readonly DispatcherTimer _volChipTimer;
    private bool _volSync;
    private WidgetGalleryWindow? _widgetGallery;

    private HotkeyManager? _pinHotkeys;

    private KeyProbeWindow? _keysWindow;

    // ---- перетаскивание окна ----
    private Point _dragScreenStart;
    private Point _dragWindowOrigin;
    private bool _dragArmed;   // кнопка нажата на "не-интерактивном" месте
    private bool _dragMoved;   // сдвинулись дальше порога — это уже драг, а не клик

    public IslandWindow()
    {
        InitializeComponent();

        _services = ShellServices.Instance;
        _vm = new IslandViewModel(_services);

        // Город погоды: список частых городов, свой можно вписать руками.
        foreach (var city in new[] { "Москва", "Санкт-Петербург", "Иркутск", "Новосибирск", "Екатеринбург", "Казань", "Владивосток" })
            WeatherCityBox.Items.Add(city);

        WeatherCityBox.Text = _services.Settings.Current.WeatherCity;
        DataContext = _vm;

        BuildAccentPresets();
        BuildSkinChips();
        BuildBackdropChips();
        BuildPlayerChips();
        ApplyPlayerStyle();
        ApplyBackdrop();
        // Рисунок — общая тема: меняется на острове, значит меняется и в окне
        // настроек, и в виджетах. Иначе «тема» перестаёт быть темой.

        if (Program.ChatDemo)
        {
            _vm.ChatBusy = true;
            if (AiThinkingEq is not null) AiThinkingEq.IsRunning = true;
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("user", "покажи, как теперь выглядит ответ"));
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("qwen", "Ответ остаётся обычным текстом — точками набраны только метки и заголовок."));
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("user", "а если ответ длиннее?"));
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("qwen", "Длинный ответ проверяет, как остров ведёт себя, когда сообщений больше трёх: растёт ли окно, появляется ли прокрутка, не уезжает ли поле ввода."));
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("user", "и ещё одно"));
            _vm.ChatLines.Add(new IslandViewModel.ChatLine("qwen", "Третья пара реплик нужна, чтобы точно выйти за предел и увидеть, где заканчивается место."));
        }

        _vm.BackdropChanged += () =>
        {
            ApplyBackdrop();
            _settingsWindow?.RefreshBackdrop();
            WidgetsManager.RefreshAll();
        };
        _vm.PlayerChanged += ApplyPlayerStyle;

        // Скругление рисунка должно совпадать со скруглением капсулы:
        // свёрнутая — почти таблетка, раскрытая — мягкий прямоугольник.
        Pill.SizeChanged += (_, _) =>
        {
            if (Backdrop is not null) Backdrop.Corner = Math.Min(Pill.ActualHeight / 2, 23);
        };

        // Секундный тик: телеметрия, прогресс трека, "возраст" записей буфера.
        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        // Тик раз в секунду двигает часы, телеметрию и заодно решает, жить ли
        // сталактитам. Без этого вызова эквалайзер под свёрнутой пилюлей
        // включался только после первого раскрытия острова.
        _tickTimer.Tick += (_, _) =>
        {
            _vm.Tick();
            RefreshGroupDots();
            UpdateQuietMode();
            UpdateMiniEq();
        };

        // Страховка от "остров пропал": периодически возвращаем себя наверх
        // topmost-стопки. Дёшево (один SetWindowPos), зато оверлеи и DWM-глюки
        // не способны спрятать шелл надолго.
        _topmostWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _topmostWatchdog.Tick += (_, _) => ReassertTopmost();

        // Сохранённое устройство эквалайзера применяем сразу: иначе захват
        // останется на системном, и полоска будет молчать при музыке в наушниках.
        if (AudioSpectrum.Current is { } savedSpectrum && _services.Settings.Current.AudioDeviceId.Length > 0)
        {
            savedSpectrum.DeviceId = _services.Settings.Current.AudioDeviceId;
            savedSpectrum.Stop();
            savedSpectrum.Start();
        }

        Loaded += OnWindowLoaded;
        // Тихий режим: наведение на 5-пиксельную полоску должно вытаскивать остров.
        // Раньше метод был ни к чему не привязан, и помогало только нажатие.
        Pill.MouseEnter += (_, _) => OnQuietHover();

        Pill.MouseLeftButtonDown += OnPillDoubleDown;

        Loaded += (_, _) => { if (Program.OpenSettingsOnStart) OpenSettings(); };
        Loaded += (_, _) => ApplyCapsuleGeometry();

        // Настройки меняются ползунками в другом окне — капсула должна
        // подхватывать это сразу, а не после перезапуска.
        ShellServices.Instance.Theme.SettingsChanged += _ => Dispatcher.BeginInvoke(ApplyCapsuleGeometry);

        // Слой уведомлений добавляем в капсулу из кода: правки разметки острова
        // по номерам строк четыре раза ломали XAML, и это уже не смешно.
        if (Pill.Parent is Grid rootLayer)
        {
            // Обрезка текста идёт по фактической ширине капсулы: анимация ширины
            // меняет её каждый кадр, и буквы проступают ровно по краю.
            Pill.SizeChanged += (_, _) => _notice?.SetVisibleWidth(Pill.ActualWidth);

            // Кладём уведомление НАД капсулой, а не внутрь: у пилюли ClipToBounds,
            // и «пасть», которая должна выходить за края, иначе обрежется.
            _notice = new IslandNotification
            {
                Visibility = Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 10, 0, 0),
                Height = CollapsedHeight,
            };
            rootLayer.Children.Add(_notice);
        }

        Current = this;
        Loaded += (_, _) => { if (LauncherHost.OpenOnce) Dispatcher.BeginInvoke(LauncherHost.Toggle); };

        // Первая отрисовка переписки: прокрутка в конец, иначе чат открывается
        // на самых старых репликах, а свежие висят ниже без движения.
        Loaded += (_, _) => Dispatcher.BeginInvoke(new Action(() => ChatScroller?.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Loaded);
        SourceInitialized += OnSourceInitialized;

        // Смена разрешения или мониторов: позиция считалась один раз при старте,
        // и остров оставался в координатах старого экрана.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            Dispatcher.BeginInvoke(OnDisplayChanged);
        Closed += (_, _) => DotShell.Core.Native.DisplayBrightness.Restore();

        Closed += (_, _) => { _tickTimer.Stop(); _topmostWatchdog.Stop(); _pinHotkeys?.Dispose(); _tray?.Dispose(); };

        // Перетаскивание: подписываемся вручную (а не в XAML), чтобы контролировать
        // порядок: сначала решаем "это драг?", и только потом — "это клик?".
        Pill.PreviewMouseLeftButtonDown += OnDragCandidateDown;
        Pill.MouseMove += OnDragMove;
        Pill.PreviewMouseLeftButtonUp += OnDragUp;
        DockHit.PreviewMouseLeftButtonDown += OnDragCandidateDown;

        _services.WindowWatcher.FullscreenChanged += OnFullscreenChanged;

        // Мини-EQ оживает и гаснет вместе со звуком в системе
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(IslandViewModel.HasMedia) or nameof(IslandViewModel.IsPlaying))
                UpdateMiniEq();
        };
        if (AudioSpectrum.Current is { } spectrum)
            spectrum.SignalChanged += () => Dispatcher.BeginInvoke(UpdateMiniEq);

        // Тост вместо пузыря: напоминание должно догнать человека, даже если трей
        // свёрнут в стрелку и пузыря не видно.
        _vm.TimerFired += () =>
        {
            if (!Toast.Show("Таймер DotShell", "Время вышло!"))
                _tray?.Notify("Таймер DotShell", "Время вышло!");
        };
        // Содержимое заметок выросло — подрастим и остров.
        _services.Tasks.Changed += () => Dispatcher.BeginInvoke(() => UpdateNotesHeight());
        _services.Reminders.Changed += () => Dispatcher.BeginInvoke(() => UpdateNotesHeight());

        _services.Reminders.Fired += r => Dispatcher.BeginInvoke(() =>
        {
            if (!Toast.Show("Напоминание", r.Text, "reminder"))
                _tray?.Notify("Напоминание", r.Text);
            _vm.StatusLine = $"напоминание: {r.Text}";
        });

        // Консоль: автопрокрутка к последней строке вывода
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IslandViewModel.CmdLog))
                Dispatcher.BeginInvoke(() => CmdScroller?.ScrollToEnd());
        };

        // Полка: drag-out строки списка в любое окно
        ShelfList.PreviewMouseLeftButtonDown += OnShelfDragCandidate;
        ShelfList.PreviewMouseMove += OnShelfDragMove;

        // Автопрокрутка чата к последней реплике + остров растёт вслед за перепиской
        _vm.ChatLines.CollectionChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() => ChatScroller?.ScrollToEnd());
            ScheduleGrowChat();
        };

        // Чат растёт ПОСЛЕ окончания анимации раскрытия: мерить viewport во время
        // анимации — значит получить ложную высоту.
        _growTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(360) };
        _growTimer.Tick += (_, _) => { _growTimer.Stop(); GrowChat(); };

        // Громкость: ползунок на главной, чип при кручении колеса, внешние изменения
        _volChipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _volChipTimer.Tick += (_, _) => { _volChipTimer.Stop(); VolChip.Visibility = Visibility.Collapsed; };
        _services.Volume.Changed += () => Dispatcher.BeginInvoke(SyncVolumeUI);
        Pill.MouseWheel += OnPillWheel;

        // Плитки «мои программы» перестраиваются при любом изменении списка
        _vm.PinnedChanged += RebuildPinnedTiles;

        // В поиске программ: Enter — закрепить, Esc — закрыть панель
        PickerSearch.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && PickerList.SelectedItem is LauncherRow row)
            {
                _vm.PinFromPicker(row);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _isExpanded)
            {
                // Явный способ свернуть: после фикса ползунков клик по содержимому
                // больше не сворачивает остров (он промахивался мимо дорожки).
                Collapse();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                ClosePicker();
                e.Handled = true;
            }
        };

        // Esc сворачивает раскрытый остров. Раньше этот обработчик висел только на
        // PickerSearch (поле поиска программ), поэтому Esc срабатывал лишь когда
        // фокус был внутри этого поля — почти никогда. Остров при клике становится
        // foreground-окном (проверено через GetForegroundWindow), поэтому Esc
        // долетает до окна, если слушать его здесь, а не на внутреннем поле.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && _isExpanded)
            {
                Collapse();
                e.Handled = true;
            }
        };

        WidgetsManager.Changed += () => Dispatcher.BeginInvoke(RefreshWidgetsLabel);

        // Алхимия буфера: плашки умных действий перестраиваются при каждой копии
        _vm.SmartActionsChanged += RebuildSmartRow;

        // Наведение на торчащий из-за экрана кусок острова = "выгляни";
        // увёл мышь со всего видимого содержимого = "спрячься обратно".
        RootGrid.MouseEnter += (_, _) => { if (_docked && !_peeked) PeekIn(); };
        RootGrid.MouseLeave += (_, _) => { if (_docked && _peeked && !_isExpanded) PeekOutDelayed(); };
    }

    /// <summary>
    /// Фокус ушёл в другое окно — сворачиваем остров в таблетку.
    /// Раньше раскрытый остров в этот момент просто оставался висеть поверх
    /// чужого окна (или исчезал из-за ложного фуллскрина) — оба варианта плохие:
    /// человек ожидает именно "свернулся и не мешаю".
    /// </summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Collapse();
        // Другие topmost-окна (оверлеи, мессенджеры) при активации встают НАД нашим
        // topmost-окном — со стороны это выглядит как "остров пропал".
        // Вежливо возвращаем себя наверх топmost-стопки.
        ReassertTopmost();
    }

    /// <summary>
    /// Вернуть окно наверх topmost-стопки без активации и без мерцания.
    /// Плюс страховка: watchdog-таймер делает это же раз в 10 секунд —
    /// если DWM или чужой оверлей всё же задвинул остров под себя.
    /// </summary>
    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero || _hiddenByFullscreen) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Полоска мини-EQ крутится, когда остров свёрнут и в системе реально что-то
    /// звучит: приоритет у настоящего сигнала из loopback-захвата, а статус
    /// медиа-сессии — запасной вариант для систем без захвата.
    /// </summary>
    private void UpdateMiniEq()
    {
        // Сторож захвата: при смене устройства вывода (наушники, HDMI, BT)
        // WASAPI-запись умирает молча, и полоска застывала до перезапуска.
        var spectrum = AudioSpectrum.Current;
        spectrum?.EnsureAlive();

        // Диагностика «эквалайзер умер»: пишем, когда захват перезапускается
        // или когда он жив, но давно молчит. Без этого жалоба «не работает»
        // не оставляет следов в логе.
        if (spectrum is not null)
        {
            var diag = spectrum.IsRunning
                ? $"захват идёт, тишина {spectrum.SilentForMs} мс, сигнал {spectrum.HasSignal}"
                : $"захват стоит: {spectrum.Error ?? "нет устройства"}";

            if (diag != _lastSpectrumDiag)
            {
                _lastSpectrumDiag = diag;
                Log.Info($"spectrum: {diag}");
            }
        }

        bool live = spectrum?.HasSignal == true;

        // В свёрнутой пилюле полоска живёт, когда есть сигнал ИЛИ играет медиа.
        // Если захват сломан, HasSignal вечно false — но медиа-статус остаётся,
        // поэтому полоска не «умирает» окончательно, а показывает тихое дыхание.
        MiniEq.IsRunning = !_isExpanded && (live || (_vm.HasMedia && _vm.IsPlaying));

        // В сжатом режиме эквалайзер не прячем, а превращаем в саму полоску:
        // её длина и яркость идут от спектра. Иначе «пропал эквалайзер» — ровно
        // то, на что пожаловался владелец.
        if (_quiet)
        {
            double peak = 0;
            if (spectrum is { HasSignal: true })
            {
                var bands = spectrum.Levels;
                for (int i = 0; i < bands.Length; i++)
                    if (bands[i] > peak) peak = bands[i];
            }

            peak = Math.Clamp(peak, 0, 1);
            QuietLine.Width = 70 + 150 * peak;
            QuietLine.Opacity = 0.45 + 0.55 * peak;
        }
    }



    // ================================================================
    //  Инициализация
    // ================================================================

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Говорим вотчеру полноэкранного режима: "вот это окно — наше,
        // не считай его чужим фуллскрином".
        _services.OwnWindows.Add(_hwnd);

        // Хук WndProc нужен ради WM_MOUSEACTIVATE: это сообщение приходит
        // ДО того, как Windows сделает наше окно активным — идеальный
        // момент, чтобы запомнить, в каком приложении сидел пользователь.
        // Потом (вставка из буфера) мы вернём фокус именно туда.
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_MOUSEACTIVATE)
        {
            var fg = Win32.GetForegroundWindow();
            if (fg != IntPtr.Zero && fg != _hwnd) _lastForeground = fg;
        }
        return IntPtr.Zero;
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        // Сначала ширина свёрнутой пилюли, потом позиция. ResizeWindowTo сохраняет
        // центр окна, а стартовая ширина XAML (700) шире свёрнутой (440): если
        // сначала восстановить Left из настроек, а потом подогнать размер, остров
        // на каждом перезапуске уезжал вправо на (700−440)/2 = 130 DIP (~163 px).
        Width = CollapsedWidth;

        PositionAtTopCenter();

        // Окно острова = ровно таблетка, без прозрачных полей: иначе невидимая
        // рамка ловила бы клики мимо. Стартовый размер в XAML больше свёрнутой
        // пилюли, поэтому подгоняем сразу.
        ResizeWindowTo(CollapsedWidth, CollapsedHeight);

        RegisterHotkeys();

        // Трей-иконка: точка входа, когда остров не видно (спрятан, утоплен за
        // экран или потерялся). События NotifyIcon приходят из пула потоков,
        // поэтому всё заворачиваем в Dispatcher.
        _tray = new TrayIcon(
            () => Dispatcher.BeginInvoke(ToggleExpanded),
            () => Dispatcher.BeginInvoke(ResetPosition),
            () => Dispatcher.BeginInvoke(OpenSettingsFolder),
            () => Dispatcher.BeginInvoke(() => Application.Current.Shutdown()),
            extend: menu =>
            {
                // Виджеты: открываем витрину с карточками, а не список в меню —
                // в меню не видно, что ставишь, и нельзя выбрать место на столе.
                menu.Add("Центр управления", () => Dispatcher.BeginInvoke(() => ControlCenterWindow.Toggle()));
                menu.Add("Док слева", () => Dispatcher.BeginInvoke(() => DockWindow.Toggle()));
                menu.Add("Виджеты на обоях", () => Dispatcher.BeginInvoke(() =>
                {
                    var now = ShellServices.Instance.Settings.Current.DesktopWidgets;
                    ShellServices.Instance.Settings.Update(s => s.DesktopWidgets = !now);
                }));
                menu.Add("Обзор", () => Dispatcher.BeginInvoke(() => DashboardWindow.Toggle()));
                menu.Add("Витрина виджетов", () => Dispatcher.BeginInvoke(OpenWidgetGallery));
                menu.Add("Убрать все виджеты", () => Dispatcher.BeginInvoke(Widgets.WidgetsManager.CloseAll));
                menu.Add("Клавиши и сочетания", () => Dispatcher.BeginInvoke(OpenKeysWindow));

                // Обновления: одна строка «проверить» и, если есть новее,
                // вторая — «скачать». Никаких авто-перезапусков без спроса.
                menu.Add("Проверить обновления", () => Dispatcher.BeginInvoke(() => _ = CheckUpdatesAsync(manual: true)));

                var download = menu.Add("Скачать обновление", () => Dispatcher.BeginInvoke(OpenUpdatePage));
                download.IsVisible = false;

                menu.Opening += () =>
                {
                    var info = _available;
                    download.IsVisible = info is not null;
                    download.Update(item =>
                    {
                        if (info is not null) item.Header = $"Скачать обновление {info.Version.ToString(3)}";
                    });
                };
            });

        _ = DelayedUpdateCheckAsync();

        MenuAutostart.IsChecked = _vm.RunAtStartup;
        CoverAccentCheck.IsChecked = _services.Settings.Current.PlayerAccentFromCover;

        RebuildPinnedTiles();

        RebuildSmartRow();
        RefreshToolLabels();
        RefreshWidgetsLabel();
        RefreshPinHotkeys();

        WidgetsManager.Restore(); // виджеты прошлого запуска возвращаются на стол
        if (_vm.HotkeyHudEnabled) HotkeyHud.SetEnabled(true);

        // Нормализуем стартовое состояние: Checked для первой вкладки прилетает
        // во время InitializeComponent, когда окно ещё не готово, и без этого
        // все вкладки остаются видимыми одновременно (заметки поверх главной).
        ShowTab(_vm.ActiveTab);

        _vm.Tick();
        _tickTimer.Start();
        _topmostWatchdog.Start();

        // Старт "свёрнутым" (в т.ч. при --minimized из автозапуска) — ничего делать не надо,
        // таблетка и так в свёрнутом состоянии. Просто логируем старт.
        Log.Info($"island started (minimized={StartupManager.StartedMinimized})");

        // --tab=chat: открыть сразу на нужной вкладке, не трогая мышью.
        if (!string.IsNullOrWhiteSpace(Program.StartupTab))
            Log.Info($"startup tab requested: {Program.StartupTab}");
    }

    /// <summary>Раскрыть остров на конкретной вкладке (флаг --tab=…).
    /// Вызывается после Show(): окно уже существует и умеет анимироваться.</summary>
    /// <summary>
    /// Радиус и отступ капсулы из настроек. Радиус не больше половины высоты —
    /// иначе WPF рисует «скругление с изломом».
    /// </summary>
    private void ApplyCapsuleGeometry()
    {
        var settings = ShellServices.Instance.Settings.Current;

        double height = Math.Clamp(settings.CollapsedHeight, 36, 64);
        double radius = Math.Clamp(settings.IslandRadius, 0, 24);
        double offset = Math.Clamp(settings.IslandTopOffset, 0, 24);

        double liveHeight = _isExpanded ? Math.Max(height, _expandedH) : height;

        Pill.CornerRadius = new CornerRadius(Math.Min(radius, liveHeight / 2));

        bool bottom = string.Equals(settings.IslandEdge, "bottom", StringComparison.OrdinalIgnoreCase);

        // Кто живёт в капсуле: глазки или персонаж (готка/скелетик).
        string unitStyle = settings.UnitStyle ?? "goth";

        if (Unit is not null && Units is not null)
        {
            bool eyes = unitStyle.Equals("eyes", StringComparison.OrdinalIgnoreCase);

            Units.Visibility = eyes ? Visibility.Visible : Visibility.Collapsed;
            Unit.Visibility = eyes ? Visibility.Collapsed : Visibility.Visible;

            // Юнит растёт вместе с капсулой: при высоте 46 он мелкий, при 56–64 —
            // крупный и с читаемой мимикой. Пропорции сетки 12×16 сохраняем,
            // иначе фигурка сплющится.
            double unitHeight = Math.Clamp(height - 6, 34, 58);
            Unit.Height = unitHeight;
            Unit.Width = Math.Round(unitHeight * 12.0 / 16.0);
            Unit.Kind = unitStyle.Equals("skeleton", StringComparison.OrdinalIgnoreCase)
                ? Controls.UnitCharacter.UnitKind.Skeleton
                : Controls.UnitCharacter.UnitKind.Goth;
        }

        // Капсула прижимается к своей кромке: сверху — верхний отступ, снизу — нижний.
        Pill.VerticalAlignment = bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        Pill.Margin = bottom
            ? new Thickness(0, 0, 0, offset)
            : new Thickness(0, offset, 0, 0);

        if (!_isExpanded && Math.Abs(Pill.Height - height) > 0.5)
        {
            Pill.Height = height;
            ResizeWindowTo(CollapsedWidth, height);
        }

        // Ставим окно к выбранной кромке (сверху или снизу).
        var work = SystemParameters.WorkArea;

        if (bottom)
        {
            Top = work.Bottom - Math.Max(Height, height + 56) - offset;
        }
        else if (Top < work.Top || Top > work.Bottom - 40)
        {
            Top = work.Top + offset - 10;
        }
    }

    public void OpenStartupTab()
    {
        var tab = Program.StartupTab;

        if (Program.OpenGallery)
        {
            Dispatcher.BeginInvoke(OpenWidgetGallery);
        }

        if (Program.OpenKeys)
        {
            Dispatcher.BeginInvoke(OpenKeysWindow);
        }

        if (Program.OpenDashboard)
        {
            Dispatcher.BeginInvoke(() => DashboardWindow.Toggle());
        }

        if (Program.OpenControlCenter)
        {
            Dispatcher.BeginInvoke(() => ControlCenterWindow.Toggle());
        }

        // Яркость из прошлого запуска.
        double savedBrightness = Math.Clamp(ShellServices.Instance.Settings.Current.ScreenBrightness, 0.3, 1.0);
        if (savedBrightness < 0.99)
        {
            DotShell.Core.Native.DisplayBrightness.Set(savedBrightness);
        }

        // Док слева: либо по флагу, либо потому что был включён в прошлый раз.
        if (Program.OpenDock || ShellServices.Instance.Settings.Current.DockEnabled)
        {
            Dispatcher.BeginInvoke(() => DockWindow.Toggle());
        }

        if (Program.OpenSliderDemo)
        {
            Dispatcher.BeginInvoke(() => new SliderDemoWindow().Show());
        }

        if (Program.OpenDotDemo)
        {
            Dispatcher.BeginInvoke(() => new DotDemoWindow().Show());
        }

        if (Program.OpenUnitDemo)
        {
            Dispatcher.BeginInvoke(() => new UnitDemoWindow().Show());
        }

        if (Program.ClampTest)
        {
            // Уводим окно далеко за виртуальный экран, затем просим кламп и пишем
            // результат: так проверяется мультимониторная логика без второго монитора.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                double farLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 900;
                double farTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 500;

                Left = farLeft;
                Top = farTop;

                ClampToScreen();

                double vLeft = SystemParameters.VirtualScreenLeft;
                double vTop = SystemParameters.VirtualScreenTop;
                double vRight = vLeft + SystemParameters.VirtualScreenWidth;
                double vBottom = vTop + SystemParameters.VirtualScreenHeight;

                bool inside = Left >= vLeft - Width && Left <= vRight
                              && Top >= vTop - 60 && Top <= vBottom;

                try
                {
                    var path = System.IO.Path.Combine(
                        DotShell.Core.Settings.AppSettings.SettingsDirectory, "clamp-selftest.txt");

                    System.IO.File.WriteAllLines(path, new[]
                    {
                        $"farLeft={farLeft:0} farTop={farTop:0}",
                        $"afterLeft={Left:0} afterTop={Top:0}",
                        $"virtual={vLeft:0},{vTop:0}-{vRight:0},{vBottom:0}",
                        $"inside={inside}",
                        inside ? "CLAMP SELFTEST PASS" : "CLAMP SELFTEST FAILED",
                    });
                }
                catch { }
            }));

            Log.Info("clamp test: прогон ручной установки за экран");
        }

        if (Program.PlayerFixture)
        {
            ShellServices.Instance.Media.ApplyFixture();
            Log.Info("player fixture: подставлен тестовый трек «Ночной дождь»");
        }

        if (Program.OpenNotifyTest)
        {
            // Даём острову собраться, потом показываем уведомление.
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2),
            };

            timer.Tick += (_, _) =>
            {
                timer.Stop();
                NotificationHub.Show("DotShell", "Проверка уведомлений: режим из настроек.");
            };

            timer.Start();
        }

        if (Program.OpenWidgetType is { } widgetType)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Widgets.WidgetsManager.OpenWidget(widgetType);

                if (Program.WidgetSize is { } size && Widgets.WidgetsManager.Get(widgetType) is { } window)
                {
                    window.Width = size.W;
                    window.Height = size.H;

                    // Размер, заданный флагом, тоже сохраняем: иначе в widgets.json
                    // оставались нули, и следующий запуск открывал виджет по размеру
                    // содержимого.
                    Widgets.WidgetsManager.OnResized(window);
                }
            });
        }

        if (string.IsNullOrWhiteSpace(tab)) return;

        Dispatcher.BeginInvoke(() =>
        {
            Log.Info($"expanding at startup to tab {tab}");
            ExpandAndGoToTab(tab!);
        });
    }


    private void RegisterHotkeys()
    {
        // Ctrl+Shift+Space — раскрыть/свернуть. Не берём популярные сочетания
        // вроде Ctrl+Space: они заняты раскладками ввода.
        var ok1 = _services.Hotkeys.Register("island.toggle",
            Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, 0x20,
            () => Dispatcher.BeginInvoke(ToggleExpanded));

        // Ctrl+Shift+V — сразу открыть вкладку буфера (поверх Win+V не встаём,
        // это системное сочетание).
        var ok2 = _services.Hotkeys.Register("island.clipboard",
            Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, 0x56,
            () => Dispatcher.BeginInvoke(() => { ExpandAndGoToTab("clipboard"); ClipSearch.Focus(); }));

        // Ctrl+Shift+L — лаунчер приложений из любого места.
        var ok3 = _services.Hotkeys.Register("launcher",
            Win32.MOD_CONTROL | Win32.MOD_SHIFT | Win32.MOD_NOREPEAT, 0x4C,
            () => Dispatcher.BeginInvoke(LauncherHost.Toggle));

        if (!ok1 || !ok2 || !ok3)
            Log.Info($"hotkey registration incomplete: toggle={ok1} clipboard={ok2} launcher={ok3}");
    }


    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;

            foreach (var nested in FindDescendants<T>(node)) yield return nested;
        }
    }















}