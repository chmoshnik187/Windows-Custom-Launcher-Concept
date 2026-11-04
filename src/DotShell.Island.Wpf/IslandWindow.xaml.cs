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
            else if (e.Key == Key.Escape)
            {
                ClosePicker();
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

    /// <summary>
    /// Где должно быть окно по вертикали для текущей кромки. Для верхней — сохранённая
    /// «нормаль», для нижней — отсчёт от низа рабочей области. Раньше всё считалось от
    /// _normalTop, и на нижней кромке остров при каждом сворачивании прыгал наверх.
    /// </summary>
    private double EdgeTop(double windowHeight)
    {
        var s = ShellServices.Instance.Settings.Current;
        double offset = Math.Clamp(s.IslandTopOffset, 0, 24);

        if (string.Equals(s.IslandEdge, "bottom", StringComparison.OrdinalIgnoreCase))
        {
            var work = SystemParameters.WorkArea;
            return work.Bottom - windowHeight - offset;
        }

        return _normalTop;
    }

    /// <summary>Центрируем таблетку по верхнему краю рабочей области основного монитора.</summary>
    private void PositionAtTopCenter()
    {
        var wa = SystemParameters.WorkArea; // экран минус таскбар, в WPF-единицах
        var s = _services.Settings.Current;

        // Восстанавливаем последнее положение: обычное или "утоплен за кромку".
        if (s.IslandLeft > -30000 && s.IslandTop > -30000)
        {
            Left = s.IslandLeft;
            Top = s.IslandTop;
        }
        else
        {
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Top - 4; // чуть наезжаем на самую кромку, как настоящий Dynamic Island
        }

        if (Top <= wa.Top - 34)
        {
            // Стартовали сразу в доке: "нормаль" для выглядывания — центр кромки.
            _normalLeft = Left;
            _normalTop = wa.Top - 4;
        }
        else
        {
            _normalLeft = Left;
            _normalTop = Top;
        }
        EvaluateDock(instant: true);
    }

    // ================================================================
    //  Докинг у кромки экрана: виден только эквалайзер, наведение = выглянуть
    // ================================================================

    /// <summary>
    /// После перетаскивания решаем, не утопил ли пользователь остров за верхнюю
    /// кромку экрана. Если да — включаем режим дока: таблетка прячется за экран,
    /// снаружи остаются только сталактиты эквалайзера и невидимая зона наведения.
    /// </summary>
    private void EvaluateDock(bool instant = false)
    {
        var wa = SystemParameters.WorkArea;
        _dockTop = wa.Top - 54; // таблетка (10+46) вся за кромкой, сталктиты (54..98) наружу

        if (Top <= wa.Top - 34)
        {
            if (!_docked)
            {
                _docked = true;
                _peeked = false;
            }
            DockHit.Visibility = Visibility.Visible;
            if (instant) Top = _dockTop;
            else if (!_peeked) AnimateTop(_dockTop);
        }
        else
        {
            _docked = false;
            _peeked = false;
            DockHit.Visibility = Visibility.Collapsed;
            _normalLeft = Left;
            _normalTop = Top;
        }
        SavePosition();
    }

    /// <summary>Плавно высунуть остров из-за кромки (наведение мыши).</summary>
    private void PeekIn()
    {
        if (!_docked || _peeked) return;
        _peeked = true;
        DockHit.Visibility = Visibility.Collapsed; // вся таблетка видна и кликабельна сама
        AnimateTop(_normalTop);
    }

    /// <summary>Плавно уйти обратно за кромку, оставив только эквалайзер.</summary>
    private void PeekOut()
    {
        if (!_docked || !_peeked) return;
        _peeked = false;
        DockHit.Visibility = Visibility.Visible;
        AnimateTop(_dockTop);
    }

    /// <summary>
    /// MouseLeave во время скольжения обманчив: курсор мог просто остаться на месте,
    /// пока остров уезжает из-под него. Даём 200 мс форы и перепроверяем.
    /// </summary>
    private void PeekOutDelayed()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_docked && _peeked && !_isExpanded && !RootGrid.IsMouseOver) PeekOut();
        };
        timer.Start();
    }

    private void AnimateTop(double to, Action? onDone = null)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            // Критично: без снятия анимации её "замороженное" значение продолжает
            // переопределять Top, и прямые присваивания (перетаскивание окна)
            // молча игнорируются — остров становится неуправляемым.
            BeginAnimation(TopProperty, null);
            Top = to;
            onDone?.Invoke();
        };
        BeginAnimation(TopProperty, anim);
    }

    /// <summary>
    /// Аварийный люк: вернуть остров в центр верхнего края и выключить док.
    /// Вызывается из трея ("Показать остров"), когда остров утащили так,
    /// что его не найти ни мышью, ни здравым смыслом.
    /// </summary>
    /// <summary>Разрешение или набор мониторов изменились — вернуть остров в кадр.</summary>
    private void OnDisplayChanged()
    {
        try
        {
            var wa = SystemParameters.WorkArea;
            _dockTop = wa.Top - 54;

            if (_docked)
            {
                // В доке остров привязан к кромке — ставим её заново.
                Top = _dockTop;
                Left = Math.Clamp(Left, wa.Left - 40, wa.Right - Width + 40);
            }
            else
            {
                // Стоял по центру — держим центр и после смены режима.
                bool centered = Math.Abs(Left + Width / 2 - (wa.Left + wa.Width / 2)) < 80;
                if (centered) Left = wa.Left + (wa.Width - Width) / 2;
                if (Top < wa.Top - 34) Top = wa.Top - 4;
            }

            ClampToScreen();
            EvaluateDock(instant: true);
            SavePosition();
            Log.Info("display changed: island repositioned");
        }
        catch (Exception ex) { Log.Info($"display change failed: {ex.Message}"); }
    }

    public void ResetPosition()
    {
        BeginAnimation(TopProperty, null);
        _docked = false;
        _peeked = false;
        DockHit.Visibility = Visibility.Collapsed;

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top - 4;
        _normalLeft = Left;
        _normalTop = Top;
        SavePosition();
        Log.Info("position reset from tray");
    }

    /// <summary>
    /// Страховка от "утащил и потерял": после перетаскивания хотя бы кусок острова
    /// (80 px по горизонтали, полоска дока по вертикали) обязан остаться на экране.
    /// </summary>
    private void ClampToScreen()
    {
        // Клампим по ВИРТУАЛЬНОМУ экрану, а не по основному монитору: иначе остров,
        // перенесённый на второй монитор, после отпускания мыши утаскивало назад.
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        Left = Math.Clamp(Left, vLeft - Width + 80, vRight - 80);

        // Нижняя граница с запасом на док: верхнюю оставляем с минусом, там живёт
        // «утоплен за кромку» режим.
        double waTop = SystemParameters.WorkArea.Top;
        Top = Math.Clamp(Top, Math.Min(waTop, vTop) - 54, vBottom - 60);
    }

    /// <summary>Положение переживает перезапуск: пишем в settings.json.</summary>
    private void SavePosition()
    {
        // Сохраняем положение в «свёрткой» системе координат: окно растёт под
        // вкладку, и без нормализации остров уезжал бы влево на каждом старте
        // (в настройках лежала бы ширина вкладки, а старт — со свёрнутой).
        var l = (int)(Left + (Width - CollapsedWidth) / 2);
        var t = (int)Top;
        _services.Settings.Update(s =>
        {
            s.IslandLeft = l;
            s.IslandTop = t;
        });
    }

    /// <summary>
    /// Подогнать окно под размер таблетки, сохранив её центр на экране.
    ///
    /// Окно острова — это буквально таблетка: прозрачных «полей» быть не должно,
    /// иначе они начнут ловить клики мимо. Поэтому при смене вкладки окно
    /// меняет размер, а не просто даёт контенту обрезаться.
    /// </summary>
    private void ResizeWindowTo(double pillWidth, double pillHeight)
    {
        double width = Math.Max(CollapsedWidth, pillWidth);
        double height = Math.Max(CollapsedHeight + 56, pillHeight + 12);

        if (Math.Abs(Width - width) < 1 && Math.Abs(Height - height) < 1) return;

        double centerX = Left + Width / 2;
        Width = width;
        Height = height;
        Left = centerX - width / 2;

        // Нижняя кромка: остров растёт вверх, поэтому при изменении высоты
        // пересчитываем Top от низа рабочей области.
        if (ShellServices.Instance.Settings.Current.IslandEdge == "bottom")
        {
            var wa = SystemParameters.WorkArea;
            Top = wa.Bottom - height - Math.Clamp(ShellServices.Instance.Settings.Current.IslandTopOffset, 0, 24);
        }
    }

    private void OnDockHitClick(object sender, MouseButtonEventArgs e)
    {
        PeekIn();
        Expand();
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

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T match) yield return match;

            foreach (var nested in FindDescendants<T>(node)) yield return nested;
        }
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
    // ==================================================================
    //  Режим «не мешать»
    // ==================================================================

    /// <summary>Ширина капсулы, когда в ней остаётся только юнит.</summary>
    private const double UnitOnlyWidth = 84;

    private const double QuietWidth = 260;
    private const double QuietHeight = 5;

    /// <summary>
    /// Проверить, не мешаем ли мы: если на переднем плане чужое развёрнутое окно,
    /// сжимаемся в тонкую полоску. Проверка дешёвая (одно окно + прямоугольник),
    /// поэтому её можно делать каждый тик.
    /// </summary>
    private IslandNotification? _notice;
    private System.Windows.Threading.DispatcherTimer? _noticeTimer;
    private double _noticeSavedLeft;
    private double _noticeSavedWidth;

    /// <summary>Живой остров: уведомления показываем в нём, если он на виду.</summary>
    public static IslandWindow? Current { get; private set; }

    /// <summary>
    /// Показать уведомление внутри капсулы.
    ///
    /// Возвращает false, если сейчас неудобно (остров раскрыт, утоплен или скрыт
    /// полноэкранным окном) — тогда вызывающий код покажет обычную карточку.
    ///
    /// Ширина: окно расширяем сразу (оно прозрачное, лишнее не видно), а капсула
    /// анимируется внутри. Так текст не дёргается, пока едет ширина.
    /// </summary>
    public bool ShowNotification(string title, string text)
    {
        if (_isExpanded || _hiddenByFullscreen || _docked || _quiet) return false;
        if (_notice is null) return false;

        try
        {
            _noticeTimer?.Stop();

            double needed = _notice.MeasureWidth(title, text);
            double target = Math.Clamp(needed + 120, CollapsedWidth, 1100);

            _noticeSavedLeft = Left;
            _noticeSavedWidth = Width;

            // Окно сразу по максимальной ширине (оно прозрачное): капсуле будет куда
            // расти, и текст не дёрнется, пока едет ширина.
            Width = target + 40;
            Left = (SystemParameters.WorkArea.Width - Width) / 2;

            CollapsedRow.Visibility = Visibility.Collapsed;
            _notice.Prepare(title, text);
            _notice.SetVisibleWidth(UnitOnlyWidth);

            _ = PlayNoticeAsync(target);

            int seconds = Math.Clamp(ShellServices.Instance.Settings.Current.NotifySeconds, 3, 10);

            _noticeTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(seconds),
            };
            _noticeTimer.Tick += (_, _) => HideNotification();
            _noticeTimer.Start();

            return true;
        }
        catch (Exception ex)
        {
            // Что-то не сложилось: возвращаем капсулу как была и отдаём показ карточке.
            try { HideNotification(); } catch { }
            Log.Info($"island notification failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Три шага: сжаться к юниту, раскрыть пасть, показать текст с ростом капсулы.
    /// Паузы — чтобы шаги читались глазом, а не сливались в один рывок.
    /// </summary>
    private async Task PlayNoticeAsync(double target)
    {
        try
        {
            await AnimateWidthAsync(UnitOnlyWidth, 300);
            await Task.Delay(140);

            _notice?.StartReading();
            await Task.Delay(200);

            await AnimateWidthAsync(target, 460);
        }
        catch (Exception ex)
        {
            Log.Info($"notice animation failed: {ex.Message}");
        }
    }

    /// <summary>Анимация ширины капсулы с ожиданием завершения.</summary>
    private Task AnimateWidthAsync(double width, int milliseconds)
    {
        var done = new TaskCompletionSource();

        var animation = new DoubleAnimation(width, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
        };
        animation.Completed += (_, _) => done.TrySetResult();

        Pill.BeginAnimation(FrameworkElement.WidthProperty, animation);
        return done.Task;
    }
    private async void HideNotification()
    {
        _noticeTimer?.Stop();
        _noticeTimer = null;

        if (_notice is null) return;

        // Если остров уже раскрыли (хоткей, трей), таймер уведомления не имеет права
        // сжимать капсулу: иначе содержимое обрезалось, а следующий клик «сворачивал»
        // вместо раскрытия. Убираем только слой уведомления.
        if (_isExpanded)
        {
            try { _notice.Hide(); } catch { }
            _notice = null;
            return;
        }

        try
        {
            // Обратный ход: текст гаснет, пасть закрывается, капсула снова сжимается
            // к юниту и только потом возвращается обычное содержимое.
            _notice.HideText();
            await Task.Delay(180);

            _notice.StopReading();
            await AnimateWidthAsync(UnitOnlyWidth, 260);
            await Task.Delay(100);

            _notice.Hide();

            Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
            Pill.Width = CollapsedWidth;
            CollapsedRow.Visibility = Visibility.Visible;

            // Возвращаем окно штатным путём: он сам центрирует капсулу и знает про
            // рабочую область. Ручные Width/Left давали уезд вправо на 600 px.
            ResizeWindowTo(CollapsedWidth, CollapsedHeight);
        }
        catch (Exception ex)
        {
            Log.Info($"notice hide failed: {ex.Message}");
            try
            {
                Pill.BeginAnimation(FrameworkElement.WidthProperty, null);
                Pill.Width = CollapsedWidth;
                CollapsedRow.Visibility = Visibility.Visible;
                ResizeWindowTo(CollapsedWidth, CollapsedHeight);
            }
            catch { }
        }
    }
    private void UpdateQuietMode()
    {
        // Порог: только развёрнутые окна, любые крупные, или совсем выкл.
        string threshold = _services.Settings.Current.QuietThreshold;

        if (threshold == "off")
        {
            if (_quiet) ExitQuiet();
            return;
        }

        if (_isExpanded || _hiddenByFullscreen || _docked)
        {
            if (_quiet) ExitQuiet();
            return;
        }

        // Мы сами или наша «ручка» под курсором — не сжимаемся, иначе остров
        // будет уворачиваться от собственной мыши.
        if (IsMouseOver || _quietRestoreTimer?.IsEnabled == true) return;

        bool busy = threshold == "any"
            ? DotShell.Core.Windowing.ForegroundProbe.IsForegroundCovering()
            : DotShell.Core.Windowing.ForegroundProbe.IsForegroundMaximized();
        if (busy && !_quiet) EnterQuiet();
        else if (!busy && _quiet) ExitQuiet();
    }

    private void EnterQuiet()
    {
        _quiet = true;

        CollapsedRow.Visibility = Visibility.Collapsed;
        MiniEq.Visibility = Visibility.Collapsed;
        QuietLine.Visibility = Visibility.Visible;

        Pill.Width = QuietWidth;
        Pill.Height = QuietHeight;
        // В тихом режиме капсула становится почти прямоугольной, но не спорит с
        // настройкой: если радиус меньше 3, оставляем настройку.
        double quietRadius = Math.Min(ShellServices.Instance.Settings.Current.IslandRadius, 3);
        Pill.CornerRadius = new CornerRadius(quietRadius);
        Pill.Opacity = 0.9;

        // Окно подгоняем вручную: ResizeWindowTo держит минимальную ширину пилюли.
        Width = QuietWidth + 40;
        Height = QuietHeight + 26;
        Left = (SystemParameters.WorkArea.Width - Width) / 2;
        Top = EdgeTop(Height);
    }

    private void ExitQuiet()
    {
        _quiet = false;

        CollapsedRow.Visibility = Visibility.Visible;
        MiniEq.Visibility = Visibility.Visible;
        QuietLine.Visibility = Visibility.Collapsed;

        Pill.Width = CollapsedWidth;
        Pill.Opacity = 1;

        // Возвращаем геометрию из настроек, а не «как было зашито»: раньше здесь
        // стоял радиус 23 и высота 46, и после выхода из «не мешать» остров
        // переставал соответствовать настройкам (радиус 4 «прыгал» на 23).
        ApplyCapsuleGeometry();
        ResizeWindowTo(CollapsedWidth, Pill.Height);
    }

    /// <summary>Наведение на полоску: показать остров и не сжиматься, пока курсор рядом.</summary>
    private void OnQuietHover()
    {
        if (!_quiet) return;

        ExitQuiet();

        _quietRestoreTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _quietRestoreTimer.Stop();
        _quietRestoreTimer.Tick -= OnQuietRestoreTick;
        _quietRestoreTimer.Tick += OnQuietRestoreTick;
        _quietRestoreTimer.Start();
    }

    private void OnQuietRestoreTick(object? sender, EventArgs e)
    {
        _quietRestoreTimer?.Stop();
        if (IsMouseOver || _isExpanded) return;
        if (DotShell.Core.Windowing.ForegroundProbe.IsForegroundMaximized()) EnterQuiet();
    }
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
        return Math.Clamp(wa.Bottom - 16 - (Top + 10), ExpandedHeight, 1400);
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

    private void OnPillClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragMoved) return;              // это было перетаскивание

        if (_pressWasInteractive)
        {
            _pressWasInteractive = false;
            return;
        }

        if (IsInteractive(e.OriginalSource as DependencyObject)) return;

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
                case ButtonBase or TextBox or Slider or Selector or ScrollBar:
                    return true;
            }
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ================================================================
    //  Вкладки
    // ================================================================

    /// <summary>Показать ровно одну вкладку, остальные спрятать.</summary>
    private SettingsWindow? _settingsWindow;
    private string _lastTab = "home";

    /// <summary>
    /// Открыть окно настроек. Вкладка СИСТЕМА превратилась в кнопку-открывашку:
    /// настроек стало больше, чем помещается в полосу острова.
    /// </summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_vm, _services, this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>Вернуть вкладку, которая была выбрана до нажатия на «СИСТЕМА».</summary>
    private void RestoreLastTab()
    {
        foreach (var child in TabsRow.Children)
        {
            if (child is not System.Windows.Controls.RadioButton button) continue;
            if (button.Tag as string != _lastTab) continue;

            button.IsChecked = true;
            return;
        }
    }
    /// <summary>Вкладка, которая сейчас показана (для кросс-фейда).</summary>
    private FrameworkElement? _visibleTab;

    private void ShowTab(string tag)
    {
        var next = tag switch
        {
            "home" => (FrameworkElement)TabHome,
            "notes" => TabNotes,
            "clipboard" => TabClipboard,
            "system" => TabSystem,
            "theme" => TabTheme,
            "chat" => TabChat,
            "shelf" => TabShelf,
            "cmd" => TabCmd,
            _ => TabHome,
        };

        // Переключение вкладок — кросс-фейд, но с явным состоянием: раньше
        // завершение анимации старой вкладки могло не сработать, и она оставалась
        // висеть поверх новой (таймер заметок «жил» на всех страницах).
        var previous = _visibleTab;

        var all = new FrameworkElement[]
        {
            TabHome, TabNotes, TabClipboard, TabSystem, TabTheme, TabChat, TabShelf, TabCmd,
        };

        foreach (var tab in all)
        {
            if (ReferenceEquals(tab, next)) continue;

            if (ReferenceEquals(tab, previous) && tab.Visibility == Visibility.Visible)
            {
                tab.IsHitTestVisible = false;
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
                anim.Completed += (_, _) =>
                {
                    // Если за это время успели переключиться на другую — не трогаем.
                    if (!ReferenceEquals(_visibleTab, tab)) return;

                    tab.Visibility = Visibility.Collapsed;
                    tab.BeginAnimation(OpacityProperty, null);
                    tab.Opacity = 1;
                    tab.IsHitTestVisible = true;
                };
                tab.BeginAnimation(OpacityProperty, anim);
            }
            else
            {
                // Всё, что не участвует в переходе, гасим сразу и окончательно.
                tab.BeginAnimation(OpacityProperty, null);
                tab.Opacity = 1;
                tab.Visibility = Visibility.Collapsed;
                tab.IsHitTestVisible = true;
            }
        }

        string transition = ShellServices.Instance.Settings.Current.TabTransition;

        next.BeginAnimation(OpacityProperty, null);
        next.RenderTransform = null;
        next.Visibility = Visibility.Visible;

        if (transition == "none")
        {
            next.Opacity = 1;
        }
        else if (transition == "slide")
        {
            var shift = new TranslateTransform(38, 0);
            next.RenderTransform = shift;
            next.Opacity = 1;

            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else if (transition == "curtain")
        {
            var scale = new ScaleTransform(1, 0.82);
            next.RenderTransform = scale;
            next.RenderTransformOrigin = new Point(0.5, 0);
            next.Opacity = 1;

            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            next.Opacity = 0;
            next.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

        _visibleTab = next;

        // Guard: 300 ms after a switch everything except the active tab must be
        // hidden. Otherwise a fast switch leaves the previous tab as a translucent
        // ghost above the new one.
        var guard = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        guard.Tick += (_, _) =>
        {
            guard.Stop();

            foreach (var tab in all)
            {
                if (ReferenceEquals(tab, _visibleTab)) continue;
                if (tab.Visibility != Visibility.Visible) continue;

                tab.BeginAnimation(OpacityProperty, null);
                tab.Opacity = 1;
                tab.Visibility = Visibility.Collapsed;
                tab.IsHitTestVisible = true;
            }
        };
        guard.Start();

        if (tag == "chat") _ = CheckOllamaAsync();
        if (tag == "notes") Dispatcher.BeginInvoke(() => UpdateNotesHeight(false));
    }

    /// <summary>
    /// Памятка про Ollama, если её нет. Чат с локальной моделью — единственная часть
    /// острова, которой нужна сторонняя программа; без предупреждения это выглядит
    /// как «ИИ сломался».
    /// </summary>
    private async Task CheckOllamaAsync()
    {
        try
        {
            bool alive = await DotShell.Core.Ai.OllamaClient.IsRunningAsync();
            OllamaNote.Visibility = alive ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { OllamaNote.Visibility = Visibility.Visible; }
    }

    private void OnOpenOllamaSite(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ollama.com/download")
            {
                UseShellExecute = true,
            });
            ShowAiHint("ставим Ollama: после установки вернись сюда и нажми СКАЧАТЬ");
        }
        catch (Exception ex) { ShowAiHint($"не смог открыть сайт: {ex.Message}"); }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        // «СИСТЕМА» больше не вкладка, а вход в окно настроек: их стало больше,
        // чем помещается в полосу острова.
        if (tag == "system")
        {
            OpenSettings();
            Dispatcher.BeginInvoke(RestoreLastTab);
            return;
        }

        // Важно: Checked срабатывает ещё во время InitializeComponent (в XAML у
        // первой вкладки стоит IsChecked="True"), когда остальные поля окна ещё
        // не заполнены. Без этой проверки — NullReferenceException на старте.
        if (_vm is null || TabNotes is null) return;

        _vm.ActiveTab = tag;

        // Показ вкладок — одно место на всю программу. Раньше эта логика жила
        // только в OnTabChecked, а он для первой вкладки не срабатывает (она
        // уже IsChecked в XAML) — и заметки висели поверх главной с самого
        // старта. Отсюда и жалоба «места мало»: главную просто закрывало.
        ShowTab(tag);

        if (tag == "cmd") CmdInput.Focus();

        // Каждая вкладка получает свой размер, а не живёт в общем прямоугольнике.
        ApplyTabSize(tag);

        if (tag == "home")
        {
            SyncVolumeUI();
            RefreshToolLabels();
            RefreshWidgetsLabel();
            RebuildPinnedTiles();

        }
        else
        {
            PickerPanel.Visibility = Visibility.Collapsed;
        }

        if (tag == "chat") ScheduleGrowChat();

        if (tag == "chat")
        {
            // Блок настройки чата виден всегда: ключ, провайдер и модель можно
            // поменять в любой момент. Модель синхронизируем вручную (editable-комбобокс)
            // и молча подтягиваем список, если он ещё пуст.
            if (string.IsNullOrWhiteSpace(AiModelBox.Text)) AiModelBox.Text = _vm.AiModel;
            if (AiModelBox.ItemsSource is null) _ = RefreshModelsAsync();
            ChatInput.Focus();
        }

        if (tag == "system")
            StatsLine.Text =
                $"CPU {_services.Stats.Cpu:F0}%   RAM {_services.Stats.UsedMemoryMb}/{_services.Stats.TotalMemoryMb} МБ\n" +
                $"ВЕРСИЯ: {DotShell.Core.Updates.UpdateChecker.CurrentTextWithHint}\n" +
                $"FOCUS: {(_services.WindowWatcher.ForegroundProcessName is { Length: > 0 } p ? p : "—")}\n" +
                $"settings: {AppSettings.SettingsPath}";
    }

    // ================================================================
    //  Плеер
    // ================================================================

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.TogglePlayAsync());
    private void OnNextClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.NextAsync());
    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.PreviousAsync());

    private static async Task SafeMedia(Task t)
    {
        // Медиа-сессия могла закрыться между кликом и вызовом (плеер остановили).
        // Падение из-за "музыка закончилась" — недопустимо для шелла.
        try { await t; }
        catch (Exception ex) { Log.Info($"media command failed: {ex.Message}"); }
    }



    // ================================================================
    //  Буфер обмена
    // ================================================================

    private void OnClipboardSearch(object sender, TextChangedEventArgs e) =>
        _vm.SearchClipboard(ClipSearch.Text);

    private void OnClipboardItemUse(object sender, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipboardItem item) return;
        UseAndPaste(item);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ClipboardItem item) _vm.TogglePin(item);
    }

    private void OnClearClipboard(object sender, RoutedEventArgs e) => _vm.ClearClipboard();

    /// <summary>
    /// Главный фокус файла: вставить запись обратно в приложение, из которого
    /// пользователь пришёл. Остров — окно активируемое (иначе заметки и поиск
    /// по буферу не смогли бы принимать ввод с клавиатуры), поэтому:
    /// 1) кладём запись в буфер, 2) сворачиваем остров,
    /// 3) возвращаем фокус предыдущему окну (запомнено в WM_MOUSEACTIVATE),
    /// 4) через паузу шлём Ctrl+V — он прилетит уже в нужное приложение.
    /// </summary>
    private void UseAndPaste(ClipboardItem item)
    {
        _vm.UseClipboardItem(item);
        Collapse();

        var target = _lastForeground;
        if (target == IntPtr.Zero || target == _hwnd) return; // вставлять некуда

        var pasteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        pasteTimer.Tick += (_, _) =>
        {
            pasteTimer.Stop();
            try
            {
                Win32.SetForegroundWindow(target);
                SendKeys.Paste();
            }
            catch (Exception ex) { Log.Info($"paste-back failed: {ex.Message}"); }
        };
        pasteTimer.Start();
    }

    // ================================================================
    //  Тема: пресеты акцента
    // ================================================================

    private static readonly (string Hex, string Name)[] Presets =
    {
        ("#D71920", "Nothing Red"), ("#FF5A00", "Orange"),
        ("#FFC400", "Amber"),       ("#3DDC84", "Green"),
        ("#00C2FF", "Cyan"),        ("#7B61FF", "Violet"),
        ("#FF4D8D", "Pink"),        ("#E8E8EA", "White"),
    };

    private void BuildAccentPresets()
    {
        // Одна полоса образцов: восемь квадратов в ряд, как палитра на бумаге.
        // Круглые «конфеты» читались как кнопки — а это не кнопки, это цвета.
        AccentPresets.Columns = 8;
        AccentPresets.Rows = 1;
        AccentPresets.Height = 34;
        AccentPresets.HorizontalAlignment = HorizontalAlignment.Left;
        AccentPresets.Width = 8 * 30;

        foreach (var (hex, name) in Presets)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Width = 26,
                Height = 26,
                Margin = new Thickness(3),
                Background = new SolidColorBrush(color),
                BorderThickness = new Thickness(0),
                ToolTip = $"{name} {hex}",
                Cursor = Cursors.Hand,
            };
            swatch.Click += (_, _) => _vm.ApplyAccentPreset(hex);
            AccentPresets.Children.Add(swatch);
        }
    }

    // ================================================================
    //  Плеер: раскладка
    // ================================================================

    private static readonly (string Key, string Label)[] PlayerStyles =
    {
        ("cover", "ОБЛОЖКА"),
        ("dots", "ТОЧКИ"),
        ("row", "СТРОКА"),
    };

    /// <summary>Кнопки скинов: скин меняет акцент, точки, рисунок и плотность разом.</summary>
    private void BuildSkinChips()
    {
        if (SkinChips is null) return;

        SkinChips.Children.Clear();
        foreach (var skin in DotShell.Core.Theming.Skins.All)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = skin.Name,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = skin.Id,
                ToolTip = skin.Motto,
                Cursor = Cursors.Hand,
            };
            var captured = skin;
            chip.Click += (_, _) =>
            {
                _vm.ApplySkin(captured);
                ApplyBackdrop();
                ApplyPlayerStyle();
                RebuildPinnedTiles();

                RefreshSkinChips();
            };
            SkinChips.Children.Add(chip);
        }

        RefreshSkinChips();
    }

    /// <summary>Подсветить активный скин.</summary>
    private void RefreshSkinChips()
    {
        if (SkinChips is null) return;

        var current = _services.Settings.Current.Skin;
        foreach (var child in SkinChips.Children.OfType<Button>())
        {
            bool active = string.Equals((string?)child.Tag, current, StringComparison.OrdinalIgnoreCase);
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }
    private void BuildPlayerChips()
    {
        if (PlayerChips is null) return;

        PlayerChips.Children.Clear();
        foreach (var (key, label) in PlayerStyles)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = label,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = key,
                Cursor = Cursors.Hand,
            };
            chip.Click += (_, _) => _vm.PlayerStyle = key;
            PlayerChips.Children.Add(chip);
        }
    }

    /// <summary>Применить выбранную раскладку и подсветить её чип.</summary>
    private void ApplyPlayerStyle()
    {
        if (Player is null) return;

        var style = _services.Settings.Current.PlayerStyle;
        Player.Style = string.IsNullOrWhiteSpace(style) ? "cover" : style;

        if (PlayerChips is null) return;
        foreach (var child in PlayerChips.Children.OfType<Button>())
        {
            bool active = (string?)child.Tag == Player.Style;
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }
    // ================================================================
    //  Рисунок внутри острова
    // ================================================================

    private static readonly (string Key, string Label)[] Backdrops =
    {
        ("none", "ЧИСТО"),
        ("dots", "ТОЧКИ"),
        ("waves", "ВОЛНЫ"),
        ("topo", "ТОПО"),
        ("rays", "ЛУЧИ"),
        ("aurora", "АВРОРА"),
        ("grid", "СЕТКА"),
    };

    /// <summary>Кнопки выбора рисунка: один ряд чипов, активный — акцентом.</summary>
    private void BuildBackdropChips()
    {
        if (BackdropChips is null) return;

        BackdropChips.Children.Clear();
        foreach (var (key, label) in Backdrops)
        {
            var chip = new Button
            {
                Style = (Style)FindResource("DsButton"),
                Content = label,
                Margin = new Thickness(0, 0, 6, 6),
                FontSize = 9,
                Tag = key,
                Cursor = Cursors.Hand,
            };
            chip.Click += (_, _) => _vm.BackdropStyle = key;
            BackdropChips.Children.Add(chip);
        }
    }

    /// <summary>Подсветить выбранный рисунок и применить его к острову.</summary>
    private void ApplyBackdrop()
    {
        if (Backdrop is null) return;

        var s = _services.Settings.Current;
        Backdrop.Style = string.IsNullOrWhiteSpace(s.BackdropStyle) ? "none" : s.BackdropStyle;
        Backdrop.Strength = Math.Clamp(s.BackdropOpacity / 100.0, 0, 1);
        Backdrop.Corner = Math.Min(Pill.ActualHeight / 2, 23);

        if (BackdropChips is null) return;
        foreach (var child in BackdropChips.Children.OfType<Button>())
        {
            bool active = (string?)child.Tag == Backdrop.Style;
            child.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
            child.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
        }
    }

    // ================================================================
    //  Полноэкранный режим
    // ================================================================

    private void OnFullscreenChanged(bool fullscreen)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var hide = fullscreen && _vm.HideInFullscreen;
            _hiddenByFullscreen = hide;

            if (hide)
            {
                Log.Info("hidden: fullscreen app in foreground");
                _tickTimer.Stop();
                Units.SetPaused(true);
                MiniEq.IsRunning = false;
                ShowNoActivate(false);
            }
            else
            {
                ShowNoActivate(true);
                Units.SetPaused(false);
                _tickTimer.Start();
                ReassertTopmost();
                UpdateMiniEq();
                _vm.Tick();
            }
        });
    }

    /// <summary>
    /// Показать/спрятать окно НЕ активируя его. Обычный Show() украл бы фокус
    /// у игры или видео в момент выхода из фуллскрина — а это ровно то,
    /// за что пользователь возненавидит шелл.
    /// </summary>
    private void ShowNoActivate(bool show)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE |
            (show ? Win32.SWP_SHOWWINDOW : 0x0080 /* SWP_HIDEWINDOW */));
    }

    // ================================================================
    //  Быстрый чат с Qwen (вкладка QWEN)
    // ================================================================

    /// <summary>
    /// Подтянуть список моделей из Ollama (родной /api/tags, а не
    /// OpenAI-совместимый /v1/models: он отдаёт имена и размеры, а главное —
    /// не притворяется пустым массивом, когда моделей нет).
    ///
    /// Побочно решает давнюю боль: имя модели в настройках могло устареть
    /// (модель удалили), и человек видел «ошибку API» вместо объяснения.
    /// Теперь мёртвое имя само заменяется на существующее.
    /// </summary>
    private async Task RefreshModelsAsync(bool manual = false)
    {
        var models = await DotShell.Core.Ai.OllamaClient.ListAsync();
        var names = models.Select(m => m.Name).ToList();

        Dispatcher.BeginInvoke(() =>
        {
            AiModelBox.ItemsSource = names;

            var configured = _services.Settings.Current.AiModel;
            var configuredAlive = names.Any(n => string.Equals(n, configured, StringComparison.OrdinalIgnoreCase));

            if (names.Count > 0 && !configuredAlive)
            {
                var pick = names.FirstOrDefault(n => n.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                           ?? names[0];
                AiModelBox.Text = pick;
                _services.Settings.Update(st => st.AiModel = pick);
                _vm.StatusLine = $"модель чата: {pick}";
            }
            else if (string.IsNullOrWhiteSpace(AiModelBox.Text) && names.Count > 0)
            {
                AiModelBox.Text = names[0];
            }

            // Кнопка «скачать» нужна ровно тогда, когда скачивать нечего.
            AiPullBtn.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (names.Count > 0)
            {
                AiHint.Visibility = Visibility.Collapsed;
                if (manual) _vm.StatusLine = $"моделей найдено: {names.Count}";
                return;
            }

            ShowAiHint("В Ollama пока нет ни одной модели. Нажми СКАЧАТЬ — остров сам поставит " +
                       DotShell.Core.Ai.OllamaClient.Suggested +
                       " (1.4 ГБ, один раз), и чат заработает без интернета.");
        });
    }

    /// <summary>Скачать рекомендованную модель прямо из острова, с прогрессом.</summary>
    private async void OnPullModel(object sender, RoutedEventArgs e)
    {
        if (AiPullBtn is null) return;

        if (!await DotShell.Core.Ai.OllamaClient.IsRunningAsync())
        {
            ShowAiHint("Сервер Ollama не отвечает. Открой приложение Ollama (оно поднимает сервер " +
                       "на localhost:11434), подожди пару секунд и нажми СКАЧАТЬ снова.");
            return;
        }

        AiPullBtn.IsEnabled = false;
        AiPullBar.Visibility = Visibility.Visible;
        AiPullBar.Value = 0;
        ShowAiHint($"скачиваю {DotShell.Core.Ai.OllamaClient.Suggested} — это один раз, дальше всё офлайн");

        var model = DotShell.Core.Ai.OllamaClient.Suggested;
        var progress = new Progress<(string Status, double Fraction)>(p =>
        {
            if (p.Fraction >= 0) AiPullBar.Value = p.Fraction * 100;
            AiHint.Text = p.Fraction >= 0
                ? $"{p.Status} {p.Fraction * 100:F0}%"
                : p.Status;
        });

        var ok = await Task.Run(() => DotShell.Core.Ai.OllamaClient.PullAsync(
            model,
            (status, fraction) => ((IProgress<(string, double)>)progress).Report((status, fraction))));

        AiPullBtn.IsEnabled = true;
        AiPullBar.Visibility = Visibility.Collapsed;

        if (!ok)
        {
            ShowAiHint("Не получилось скачать модель. Проверь, запущена ли Ollama, и попробуй ещё раз.");
            return;
        }

        _services.Settings.Update(st =>
        {
            st.AiModel = model;
            st.AiProvider = "Ollama";
            st.AiBaseUrl = DotShell.Core.Ai.OllamaClient.Root + "/v1";
        });

        ShowAiHint($"готово: {model} на месте. Пиши вопрос в поле внизу и жми Enter.");
        await RefreshModelsAsync(manual: true);
        ChatInput.Focus();
    }

    private void ShowAiHint(string text)
    {
        AiHint.Text = text;
        AiHint.Visibility = Visibility.Visible;
    }

    private void OnModelDropDownOpened(object? sender, EventArgs e) => _ = RefreshModelsAsync();

    private void OnRefreshModels(object sender, RoutedEventArgs e) => _ = RefreshModelsAsync(manual: true);

    private void OnSaveAiConfig(object sender, RoutedEventArgs e)
    {
        var model = AiModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowAiHint("Выбери модель из списка (кнопка ⟳) или впиши имя вручную — его подскажет команда ollama list.");
            return;
        }

        _services.Settings.Update(st => st.AiModel = model);
        AiHint.Visibility = Visibility.Collapsed;
        _vm.StatusLine = $"модель чата: {model}";
        ChatInput.Focus();
    }

    private async void OnChatSend(object sender, RoutedEventArgs e) => await SendChat();

    private async void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // без этого Enter вставил бы перенос строки
        await SendChat();
    }

    private async Task SendChat()
    {
        var text = ChatInput.Text;
        if (string.IsNullOrWhiteSpace(text) || _vm.ChatBusy) return;
        ChatInput.Clear();
        await _vm.ChatSendAsync(text);
    }

    // ================================================================
    //  Полка файлов: приём дропа и drag-out
    // ================================================================

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Файлы, брошенные на остров в любом состоянии, уезжают на полку.</summary>
    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        _services.Shelf.Add(files);
        ExpandAndGoToTab("shelf");
        _vm.StatusLine = $"на полке: +{files.Length}";
        e.Handled = true;
    }

    private Point _shelfDragStart;
    private IslandViewModel.ShelfRow? _shelfDragRow;

    private void OnShelfDragCandidate(object sender, MouseButtonEventArgs e)
    {
        _shelfDragStart = e.GetPosition(null);
        _shelfDragRow = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as IslandViewModel.ShelfRow;
    }

    private void OnShelfDragMove(object sender, MouseEventArgs e)
    {
        if (_shelfDragRow is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _shelfDragStart.X) < 8 && Math.Abs(pos.Y - _shelfDragStart.Y) < 8) return;

        var row = _shelfDragRow;
        _shelfDragRow = null;

        // Настоящий OLE drag-and-drop: файл прилетает в целевое окно
        // как обычное перетаскивание из проводника.
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { row.Path });
        var effect = DragDrop.DoDragDrop(ShelfList, data, DragDropEffects.Copy);

        // Утащили успешно — файл сделал своё дело на полке и покидает её.
        if (effect != DragDropEffects.None) _vm.ShelfRemove(row);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private void OnShelfOpen(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is IslandViewModel.ShelfRow row)
        {
            try
            {
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
                _services.Shelf.Remove(row.File);
            }
            catch (Exception ex) { Log.Info($"shelf open failed: {ex.Message}"); }
        }
    }

    private void OnShelfRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is IslandViewModel.ShelfRow row) _vm.ShelfRemove(row);
    }

    private void OnShelfClear(object sender, RoutedEventArgs e) => _vm.ShelfClear();

    // ================================================================
    //  Таймер и напоминания
    // ================================================================

    // ================================================================
    //  Заметки: задачи и напоминания
    // ================================================================

    private void OnTaskInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTaskFromInput();
    }

    private void OnAddTask(object sender, RoutedEventArgs e) => AddTaskFromInput();

    private void AddTaskFromInput()
    {
        var text = TaskInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _vm.AddTask(text);
        TaskInput.Clear();
        TaskInput.Focus();
    }

    private void OnTaskToggle(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DotShell.Core.Tasks.TaskItem item) return;
        _vm.ToggleTask(item);
        e.Handled = true;
    }

    private void OnTaskRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DotShell.Core.Tasks.TaskItem item) return;
        _vm.RemoveTask(item);
    }

    private void OnReminderInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddReminder(sender, e);
    }
    private void OnTimerStart(object sender, RoutedEventArgs e)
    {
        var text = TimerInput.Text.Replace(',', '.');
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
            _vm.StartTimer(minutes);
        else
            _vm.StatusLine = "таймер: впиши минуты числом";
    }

    private void OnTimerStop(object sender, RoutedEventArgs e) => _vm.StopTimer();

    private void OnAddReminder(object sender, RoutedEventArgs e)
    {
        if (_vm.AddReminder(ReminderInput.Text)) ReminderInput.Clear();
    }

    private void OnRemoveReminder(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Reminder r) _vm.RemoveReminder(r);
    }

    // ================================================================
    //  Командная строка
    // ================================================================

    private async void OnCmdRun(object sender, RoutedEventArgs e) => await RunCommand();

    private async void OnCmdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunCommand();
    }

    private async Task RunCommand()
    {
        var cmd = CmdInput.Text;
        if (string.IsNullOrWhiteSpace(cmd)) return;
        CmdInput.Clear();
        await _vm.RunCmdAsync(cmd);
    }

    // ================================================================
    //  Главная: громкость
    // ================================================================

    private void SyncVolumeUI()
    {
        if (VolSlider is null) return;
        var vol = _services.Volume;
        if (!vol.Available)
        {
            VolSlider.IsEnabled = false;
            VolMute.IsEnabled = false;
            VolPercent.Text = "—";
            return;
        }

        _volSync = true;
        VolSlider.Value = Math.Round(vol.Level * 100);
        _volSync = false;
        VolPercent.Text = vol.Muted ? "ТИШИНА" : $"{vol.Level * 100:F0}%";
        VolMute.Content = vol.Muted ? "\uE74F" : "\uE767"; // Fluent: звук выкл / вкл
    }

    private void OnVolSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML выставляет Minimum/Maximum слайдера ещё до конца InitializeComponent,
        // и WPF честно дёргает ValueChanged прямо оттуда. В этот момент _services
        // ещё null (он приезжает из Program.cs уже после конструктора окна) —
        // раньше это давало NullReferenceException и окно не открывалось вовсе.
        if (_volSync || _services is null) return;
        _services.Volume.SetLevel((float)(VolSlider.Value / 100));
    }

    private void OnVolMute(object sender, RoutedEventArgs e) => _services.Volume.ToggleMute();

    /// <summary>Колесо мыши над свёрнутой пилюлей = громкость, как в плеерах.</summary>
    private void OnPillWheel(object sender, MouseWheelEventArgs e)
    {
        var vol = _services.Volume;
        if (!vol.Available) return;
        vol.SetLevel(vol.Level + (e.Delta > 0 ? 0.03f : -0.03f));
        VolChipText.Text = vol.Muted ? "ТИШИНА" : $"VOL {vol.Level * 100:F0}%";
        VolChip.Visibility = Visibility.Visible;
        _volChipTimer.Stop();
        _volChipTimer.Start();
        e.Handled = true;
    }

    // ================================================================
    //  Главная: инструменты
    // ================================================================

    private void RefreshToolLabels()
    {
        if (ToolTheme is null) return;
        ToolTheme.Content = DotShell.Core.Native.WindowsTheme.IsDarkApps() ? "СВЕТЛАЯ ТЕМА" : "ТЁМАЯ ТЕМА";
        ToolDesk.Content = _vm.HideDesktopIcons ? "ПОКАЗАТЬ СТОЛ" : "СПРЯТАТЬ СТОЛ";
        ToolFocus.Content = _vm.FocusMode ? "ФОКУС ВКЛ" : "ФОКУС";
    }

    private void RebuildSmartRow()
    {
        if (SmartRow is null) return;
        SmartRow.Children.Clear();
        if (_vm.SmartActions.Count == 0)
        {
            SmartRow.Visibility = Visibility.Collapsed;
            return;
        }

        SmartRow.Visibility = Visibility.Visible;
        var hint = new TextBlock
        {
            Text = "АЛХИМИЯ:",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        SmartRow.Children.Add(hint);

        foreach (var row in _vm.SmartActions)
        {
            var btn = new Button { Style = (Style)FindResource("DsButton"), Content = row.Label.ToUpperInvariant() };
            btn.Click += (_, _) => _vm.ExecuteSmartAction(row);
            SmartRow.Children.Add(btn);
        }
    }

    /// <summary>
    /// Перечитать сочетания закреплённых программ и зарегистрировать их заново.
    /// Отдельный менеджер: при любом изменении списка проще снести и создать заново,
    /// чем хранить соответствие «имя → id» и аккуратно снимать по одному.
    /// </summary>
    private List<string> RefreshPinHotkeys()
    {
        var busy = new List<string>();
        _pinHotkeys?.Dispose();
        _pinHotkeys = new HotkeyManager(_services.MessageWindow);

        foreach (var app in _services.Pinned.Items)
        {
            var parsed = KeyCombo.Parse(app.Hotkey);
            if (parsed is null) continue;

            var target = app;
            var ok = _pinHotkeys.Register($"pin.{target.Name}", parsed.Modifiers, parsed.VirtualKey, () =>
            {
                var row = _vm.PinnedItems.FirstOrDefault(r =>
                    string.Equals(r.Target, target.Target, StringComparison.OrdinalIgnoreCase));
                if (row is not null) _vm.LaunchPinned(row);
            });

            if (!ok)
            {
                Log.Info($"pin hotkey busy: {app.Hotkey} для «{app.Name}»");
                busy.Add($"{app.Hotkey} ({app.Name})");
            }
        }

        return busy;
    }

    /// <summary>Окно «Клавиши и сочетания»: справочник и живая проба.</summary>
    private void OpenKeysWindow()
    {
        if (_keysWindow is { IsVisible: true })
        {
            _keysWindow.Activate();
            return;
        }

        _keysWindow = new KeyProbeWindow();
        _keysWindow.Closed += (_, _) => _keysWindow = null;
        _keysWindow.Show();
        _keysWindow.Activate();
    }

    /// <summary>Диалог назначения клавиши на плитку.</summary>
    private void AssignHotkey(IslandViewModel.PinnedRow row)
    {
        var dialog = new HotkeyCaptureWindow(row.Name, ShellServices.Instance.Pinned
            .Items.FirstOrDefault(p => p.Target == row.Target)?.Hotkey, combo =>
        {
            if (combo is null) return;                       // отмена
            ShellServices.Instance.Pinned.SetHotkey(row.Target, combo);
            var busy = RefreshPinHotkeys();
            RebuildPinnedTiles();


            _vm.StatusLine = string.IsNullOrWhiteSpace(combo)
                ? $"«{row.Name}»: сочетание снято"
                : busy.Count > 0
                    ? $"{combo} занято другой программой — выбери другое"
                    : $"{combo} → {row.Name}";
        });

        dialog.ShowDialog();
    }
    /// <summary>Тумблер «цвет заголовка из обложки»: пишем настройку и обновляем плеер.</summary>
    private void OnCoverAccentToggled(object sender, RoutedEventArgs e)
    {
        bool on = CoverAccentCheck.IsChecked == true;
        _services.Settings.Update(s => s.PlayerAccentFromCover = on);

        // Перестроить плеер, чтобы он пересчитал цвет (обложка уже загружена).
        Player.RefreshThemeColors();

        _vm.StatusLine = on ? "цвет заголовка: из обложки трека" : "цвет заголовка: из обоев";
    }
    private void RefreshWidgetsLabel()
    {
        if (ToolWidgets is null) return;
        int n = WidgetsManager.Types.Count(WidgetsManager.Has);
        ToolWidgets.Content = n > 0 ? $"ВИДЖЕТЫ · {n}" : "ВИДЖЕТЫ";
    }

    /// <summary>Сохранить город погоды из настроек и перезагрузить виджеты.</summary>
    private void OnSaveWeatherCity(object sender, RoutedEventArgs e)
    {
        var city = (WeatherCityBox.Text ?? string.Empty).Trim();
        if (city.Length == 0) { _vm.StatusLine = "город не может быть пустым"; return; }

        _services.Settings.Update(s => s.WeatherCity = city);
        DotShell.Core.Weather.WeatherService.ClearCache();
        _vm.StatusLine = $"погода: {city}";

        // Скажем открытым виджетам перерисоваться, не дожидаясь таймера 15 минут.
        DotShell.Island.Wpf.Widgets.WidgetsManager.RefreshAll();
    }
    /// <summary>Тумблер «свой курсор».</summary>
    private void OnCustomCursorToggled(object sender, RoutedEventArgs e)
    {
        if (CursorCheck.IsChecked == true)
        {
            CursorStyler.Enable();
            _vm.StatusLine = CursorStyler.Enabled
                ? "курсор: точка (стрелка, ссылка, текст, прицел, ожидание)"
                : "курсор: не удалось применить";
        }
        else
        {
            CursorStyler.Disable();
            _vm.StatusLine = "курсор: системный";
        }

        CursorCheck.IsChecked = CursorStyler.Enabled;
    }
    /// <summary>Тумблер «акцент Windows = цвет обоев».</summary>
    private void OnWindowsAccentToggled(object sender, RoutedEventArgs e)
    {
        if (WinAccentCheck.IsChecked == true)
        {
            WindowsAccentSync.Enable();
            var windowsHex = DotShell.Core.Native.WindowsAccent.CurrentHex() ?? "?";
            _vm.StatusLine = $"акцент Windows: {windowsHex} (цвет обоев), часть элементов — после перезапуска проводника";
        }
        else
        {
            WindowsAccentSync.Disable();
            _vm.StatusLine = $"акцент Windows: вернул прежний ({DotShell.Core.Native.WindowsAccent.CurrentHex() ?? "?"})";
        }

        WinAccentCheck.IsChecked = WindowsAccentSync.Enabled;
    }
    /// <summary>Тумблер «оформление всей Windows»: тёмные заголовки всех окон.</summary>
    /// <summary>
    /// Пересчитать точки на плитках групп. Опрос, а не события: программы
    /// запускаются и закрываются в чужих процессах, событий оттуда не приходит.
    /// </summary>
    private void RefreshGroupDots()
    {
        foreach (var (dot, group) in _groupDots)
        {
            if (dot.Parent is null) continue;
            bool running = group.Items.Any(DotShell.Core.Launcher.ProcessProbe.IsRunning);
            var want = running ? Visibility.Visible : Visibility.Collapsed;
            if (dot.Visibility != want) dot.Visibility = want;
        }
    }
    private void OnFullStyleToggled(object sender, RoutedEventArgs e)
    {
        bool on = FullStyleCheck.IsChecked == true;
        _services.Settings.Update(s => s.SystemStyleFull = on);

        if (on)
        {
            DotShell.Core.Native.WindowsTheme.SetDark(true);
            DotShell.Core.Native.DarkTitleBars.Start();
            _vm.StatusLine = $"оформление Windows: тёмные заголовки ({DotShell.Core.Native.DarkTitleBars.LastApplied} окон)";
        }
        else
        {
            DotShell.Core.Native.DarkTitleBars.Stop();
            DotShell.Core.Native.DarkTitleBars.RestoreAll();
            _vm.StatusLine = "оформление Windows: выключено, заголовки вернулись как были";
        }

    }
    private void OnToolTheme(object sender, RoutedEventArgs e)
    {
        _vm.SystemDark = !_vm.SystemDark;
        RefreshToolLabels();
    }

    private void OnToolDesk(object sender, RoutedEventArgs e)
    {
        _vm.HideDesktopIcons = !_vm.HideDesktopIcons;
        RefreshToolLabels();
    }

    private void OnToolLock(object sender, RoutedEventArgs e) => Win32.LockWorkStation();

    private void OnToolSleep(object sender, RoutedEventArgs e) => Win32.SetSuspendState(false, true, false);

    private void OnToolFocus(object sender, RoutedEventArgs e)
    {
        _vm.FocusMode = !_vm.FocusMode;
        RefreshToolLabels();
    }

    private void OnToolWidgets(object sender, RoutedEventArgs e) => OpenWidgetGallery();

    /// <summary>Витрина виджетов: карточки, перетаскивание на стол, клик — вкл/выкл.</summary>
    private void OpenWidgetGallery()
    {
        if (_widgetGallery is { IsVisible: true })
        {
            _widgetGallery.Activate();
            return;
        }

        _widgetGallery = new WidgetGalleryWindow();
        _widgetGallery.Closed += (_, _) => _widgetGallery = null;
        _widgetGallery.ShowGallery();
    }

    // ================================================================
    //  Главная: мои программы (плитки) и выбор из индекса
    // ================================================================

private string? _openGroupId;

private const string PinnedDragFormat = "DotShell.PinnedTarget";

/// <summary>
/// Плитки «мои программы»: сначала полка групп, внутри группы — её программы.
///
/// Владелец просил не плоский список, а группировки, и не как в проводнике:
/// поэтому группа — это плитка с именем, счётчиком и превью иконок, а внутри
/// неё те же квадратные плитки программ. Ярлык можно притащить мышкой прямо
/// из проводника или с рабочего стола — группа подсветится и примет его.
/// </summary>
private void RebuildPinnedTiles()
{
    if (PinnedWrap is null) return;

    PinnedWrap.Children.Clear();

        // Без этой строки каждая перестройка плиток добавляла записи в _groupDots:
        // тик раз в секунду опрашивал процессы по удалённым группам, а старые
        // плитки не освобождались (эллипс держит родителя).
        _groupDots.Clear();
    var store = ShellServices.Instance.Pinned;

    // Группу могли удалить, пока она была открыта.
    if (_openGroupId is not null && store.FindGroup(_openGroupId) is null) _openGroupId = null;

    BuildGroupShelf(store);

    var hint = new TextBlock
    {
        Text = "перетащи ярлык с рабочего стола на группу — он останется в ней",
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 9,
        Width = 620,
        Margin = new Thickness(0, 2, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };
    hint.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    PinnedWrap.Children.Add(hint);
}

/// <summary>Полка групп: плитки-группы плюс плитка «новая группа».</summary>
private void BuildGroupShelf(PinnedStore store)
{
    foreach (var group in store.Groups)
    {
        var tile = GroupTile(group, store);
        PinnedWrap.Children.Add(tile);
    }

    PinnedWrap.Children.Add(NewGroupTile(store));
}
/// <summary>Плитка группы на полке.</summary>
private System.Windows.Controls.Border GroupTile(PinnedGroup group, PinnedStore store)
{
    // Плитка-«полка»: точечная подложка, крупный значок, имя типографикой и
    // счётчик акцентом. Раньше это была серая коробка с мелкими иконками —
    // аккуратно, но без характера, на что владелец и пожаловался.
    var tile = new System.Windows.Controls.Border
    {
        Width = 152,
        Height = 104,
        Margin = new Thickness(0, 0, 10, 10),
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        AllowDrop = true,
        ClipToBounds = true,
        ToolTip = $"{group.Name} · программ: {group.Items.Count}\nперетащи сюда ярлык · клик — открыть окном",
    };
    tile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DsSurfaceAlt");
    tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    var layer = new Grid();

    var backdrop = new DotShell.Island.Wpf.Controls.IslandBackdrop
    {
        Style = "dots",
        Strength = 0.65,
        Corner = 14,
        IsHitTestVisible = false,
    };
    layer.Children.Add(backdrop);

    var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    // Значок группы крупно — это её «лицо».
    var glyph = new TextBlock
    {
        Text = string.IsNullOrWhiteSpace(group.Glyph) ? "\uE8B7" : group.Glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 22,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    glyph.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
    grid.Children.Add(glyph);

    var count = new TextBlock
    {
        Text = group.Items.Count.ToString("00"),
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 26,
        FontWeight = FontWeights.Light,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, -4, 0, 0),
    };
    count.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
    grid.Children.Add(count);

    var name = new TextBlock
    {
        Text = group.Name,
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    name.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
    // Нижняя строка: имя и точка «в группе что-то запущено».
    var footer = new Grid();
    footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    name.VerticalAlignment = VerticalAlignment.Center;
    Grid.SetColumn(name, 0);
    footer.Children.Add(name);

    var runDot = new System.Windows.Shapes.Ellipse
    {
        Width = 5,
        Height = 5,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = group.Items.Any(DotShell.Core.Launcher.ProcessProbe.IsRunning)
            ? Visibility.Visible
            : Visibility.Collapsed,
    };
    runDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DsAccent");
    Grid.SetColumn(runDot, 1);
    footer.Children.Add(runDot);

    Grid.SetRow(footer, 1);
    grid.Children.Add(footer);
    _groupDots.Add((runDot, group));
    DotShell.Island.Wpf.Controls.Tap.Attach(tile);

    layer.Children.Add(grid);
    tile.Child = layer;

    tile.MouseEnter += (_, _) => tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsAccent");
    tile.MouseLeave += (_, _) => tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    tile.MouseLeftButtonUp += (_, e) =>
    {
        // Иначе клик долетит до таблетки, и она свернёт остров вместо открытия группы.
        e.Handled = true;
        new GroupWindow(group).Show();
    };

    var rename = new MenuItem { Header = "Переименовать группу…" };
    rename.Click += (_, _) => RenameGroupPrompt(store, group);
    var glyphPick = new MenuItem { Header = "Сменить значок" };
    glyphPick.Click += (_, _) => CycleGlyph(store, group);
    var remove = new MenuItem { Header = "Удалить группу" };
    remove.Click += (_, _) =>
    {
        store.RemoveGroup(group.Id);
        RebuildPinnedTiles();

    };
    tile.ContextMenu = new ContextMenu();
    tile.ContextMenu.Items.Add(rename);
    tile.ContextMenu.Items.Add(glyphPick);
    tile.ContextMenu.Items.Add(remove);

    WireDrop(tile, store, group.Id);
    return tile;
}

private System.Windows.Controls.Border NewGroupTile(PinnedStore store)
{
    var tile = new System.Windows.Controls.Border
    {
        Width = 92,
        Height = 92,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Создать группу",
    };
    tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    var plus = new TextBlock
    {
        Text = "+",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 20,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    plus.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    tile.Child = plus;

    tile.MouseLeftButtonUp += (_, _) =>
    {
        var group = store.AddGroup($"ГРУППА {store.Groups.Count + 1}");
        store.SetLook(group.Id, glyph: "\uE8B7");
        // группа создана — сразу спрашиваем имя
        RebuildPinnedTiles();

        RenameGroupPrompt(store, group);
    };

    return tile;
}

/// <summary>Внутри группы: плитки программ, «назад» и «добавить».</summary>
private void BuildGroupApps(PinnedStore store, PinnedGroup group)
{
    var back = new System.Windows.Controls.Border
    {
        Width = 88,
        Height = 64,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Все группы",
    };
    back.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");
    var backText = new TextBlock
    {
        Text = "← ГРУППЫ",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 9,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    backText.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    back.Child = backText;
    back.MouseLeftButtonUp += (_, _) =>
    {
        _openGroupId = null;
        RebuildPinnedTiles();

    };
    PinnedWrap.Children.Add(back);

    foreach (var app in group.Items.ToList())
    {
        var row = _vm.PinnedItems.FirstOrDefault(r =>
            string.Equals(r.Target, app.Target, StringComparison.OrdinalIgnoreCase));

        var tile = new System.Windows.Controls.Border
        {
            Width = 88,
            Height = 64,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = app.Target,
        };
        tile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DsSurfaceAlt");
        tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var icon = row?.Icon ?? LoadIconFile(app.IconPath);
        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 26, Height = 26 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            stack.Children.Add(image);
        }
        else
        {
            var letter = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            letter.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
            stack.Children.Add(letter);
        }

        var label = new TextBlock
        {
            Text = app.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            MaxWidth = 80,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        stack.Children.Add(label);
        tile.Child = stack;

        if (row is not null) tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; _vm.LaunchPinned(row); };

        // Перетаскивание плитки в другую группу.
        Point start = default;
        bool pressed = false;
        tile.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(this); pressed = true; };
        tile.PreviewMouseLeftButtonUp += (_, _) => pressed = false;
        tile.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;

            pressed = false;
            DragDrop.DoDragDrop(tile, new DataObject(PinnedDragFormat, app.Target), DragDropEffects.Move);
        };

        var assign = new MenuItem
        {
            Header = row is null || string.IsNullOrWhiteSpace(row.Hotkey)
                ? "Назначить клавишу…"
                : $"Клавиша: {row.Hotkey} — изменить",
        };
        if (row is not null) assign.Click += (_, _) => AssignHotkey(row);

        var unpin = new MenuItem { Header = "Убрать с острова" };
        unpin.Click += (_, _) =>
        {
            if (row is not null) _vm.Unpin(row);
            else { store.Remove(app); RebuildPinnedTiles(); }
        };

        var folder = new MenuItem { Header = "Папка программы" };
        folder.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{app.Target}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Info($"open folder failed: {ex.Message}"); }
        };

        tile.ContextMenu = new ContextMenu();
        tile.ContextMenu.Items.Add(assign);
        tile.ContextMenu.Items.Add(new Separator());
        tile.ContextMenu.Items.Add(unpin);
        tile.ContextMenu.Items.Add(folder);

        PinnedWrap.Children.Add(tile);
    }

    var add = new System.Windows.Controls.Border
    {
        Width = 88,
        Height = 64,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Добавить программу из меню «Пуск»",
    };
    add.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");
    var plus = new TextBlock
    {
        Text = "+",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 20,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    plus.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    add.Child = plus;
    add.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenPicker(); };
    PinnedWrap.Children.Add(add);
}

/// <summary>Приём файлов из проводника и плиток из других групп.</summary>
private void WireDrop(UIElement element, PinnedStore store, string groupId)
{
    element.AllowDrop = true;

    element.DragOver += (_, e) =>
    {
        bool can = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(PinnedDragFormat);
        e.Effects = can ? DragDropEffects.Copy : DragDropEffects.None;
        if (can) element.Opacity = 0.75;
        e.Handled = true;
    };

    element.DragLeave += (_, _) => element.Opacity = 1;

    element.Drop += (_, e) =>
    {
        element.Opacity = 1;
        e.Handled = true;

        if (e.Data.GetData(PinnedDragFormat) is string dragged)
        {
            var app = store.Items.FirstOrDefault(a =>
                string.Equals(a.Target, dragged, StringComparison.OrdinalIgnoreCase));
            if (app is not null) store.MoveToGroup(app, groupId);
            RebuildPinnedTiles();

            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        foreach (var path in files)
            if (TryAddFile(path, store, groupId)) added++;

        _vm.StatusLine = added > 0
            ? $"добавлено в группу: {added}"
            : "это не программа — нужен ярлык или .exe";
        RebuildPinnedTiles();

    };
}

/// <summary>Добавить файл из проводника: ярлык, exe, bat, url.</summary>
private bool TryAddFile(string path, PinnedStore store, string groupId)
{
    try
    {
        if (Directory.Exists(path)) return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".lnk" or ".exe" or ".bat" or ".cmd" or ".url" or ".msc")) return false;

        var name = Path.GetFileNameWithoutExtension(path);
        store.Add(name, path, ext == ".lnk" ? path : string.Empty, groupId);
        return true;
    }
    catch (Exception ex)
    {
        Log.Info($"drop add failed: {ex.Message}");
        return false;
    }
}

/// <summary>Перебор значков группы: у каждой полки должно быть своё «лицо».</summary>
private void CycleGlyph(PinnedStore store, PinnedGroup group)
{
    string[] glyphs =
    {
        "\uE8B7", "\uE7FC", "\uE8D6", "\uE7C4", "\uE9D9", "\uE713",
        "\uE721", "\uE787", "\uE8F1", "\uEC4F", "\uE706", "\uE81C",
    };

    int index = Array.IndexOf(glyphs, group.Glyph);
    var next = glyphs[(index + 1 + glyphs.Length) % glyphs.Length];
    store.SetLook(group.Id, glyph: next);
    RebuildPinnedTiles();
}

private void RenameGroupPrompt(PinnedStore store, PinnedGroup group)
{
    var dialog = new TextPromptWindow("ИМЯ ГРУППЫ", group.Name, name =>
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        store.RenameGroup(group.Id, name);
        RebuildPinnedTiles();

    });
    dialog.ShowDialog();
}

private static BitmapSource? LoadIconFile(string path)
{
    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
    try
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
    catch { return null; }
}


    /// <summary>Закрепить выбранную программу сразу в открытую группу.</summary>
    private void PinIntoOpenGroup(LauncherRow row)
    {
        _vm.PinFromPicker(row);
        if (_openGroupId is null) return;

        var store = ShellServices.Instance.Pinned;
        var app = store.Items.FirstOrDefault(a =>
            string.Equals(a.Target, row.Target, StringComparison.OrdinalIgnoreCase));
        if (app is not null) store.MoveToGroup(app, _openGroupId);

        RebuildPinnedTiles();

    }
    private void OpenPicker()
    {
        PickerPanel.Visibility = Visibility.Visible;
        _vm.PickerOpen();
        PickerSearch.Focus();
    }

    private void OnPickerSearch(object sender, TextChangedEventArgs e) => _vm.SearchPicker(PickerSearch.Text);

    private void OnPickerPin(object sender, MouseButtonEventArgs e)
    {
        if (PickerList.SelectedItem is LauncherRow row) PinIntoOpenGroup(row);
    }

    private void OnPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PickerList.SelectedItem is LauncherRow row)
        {
            _vm.PinFromPicker(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClosePicker();
            e.Handled = true;
        }
    }

    private void OnPickerDone(object sender, RoutedEventArgs e) => ClosePicker();

    private void ClosePicker()
    {
        PickerPanel.Visibility = Visibility.Collapsed;
        PickerSearch.Text = string.Empty;
    }

    // ================================================================
    //  Контекстное меню
    // ================================================================

    private void OnMenuToggle(object sender, RoutedEventArgs e) => ToggleExpanded();

    private void OnMenuClipboard(object sender, RoutedEventArgs e) => ExpandAndGoToTab("clipboard");

    private void OnMenuLauncher(object sender, RoutedEventArgs e) => LauncherHost.Toggle();

    private void OnMenuAutostart(object sender, RoutedEventArgs e) =>
        _vm.RunAtStartup = MenuAutostart.IsChecked == true;

    /// <summary>
    /// Перезапуск проводника: часть оформления Windows применяется только при
    /// старте explorer.exe. Панель задач исчезает на секунду-две — предупреждаем.
    /// </summary>
    private void OnMenuRestartExplorer(object sender, RoutedEventArgs e)
    {
        _tray?.Notify("Проводник", "Перезапускаю: панель задач исчезнет на пару секунд.");

        // Дадим уведомлению показаться, прежде чем закрывать оболочку.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            bool ok = DotShell.Core.Native.ExplorerRestart.Restart();
            Log.Info($"explorer restart: {ok}");
            _vm.StatusLine = ok ? "проводник перезапущен" : "не удалось перезапустить проводник";
        };
        timer.Start();
    }
    private void OnMenuSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnMenuOpenFolder(object sender, RoutedEventArgs e) => OpenSettingsFolder();

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.SettingsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info($"open folder failed: {ex.Message}"); }
    }

    // ================================================================
    //  Обновления с GitHub
    // ================================================================

    private DotShell.Core.Updates.UpdateInfo? _available;

    /// <summary>Проверить, не вышла ли новая версия. Тихо, если всё в порядке.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        var info = await DotShell.Core.Updates.UpdateChecker.CheckAsync();

        if (info is null)
        {
            Log.Info("update check: github не ответил");
            if (manual)
            {
                _tray?.Notify("Обновления", "Не удалось проверить: GitHub не ответил. Попробуй позже.");
                _vm.StatusLine = "обновления: не удалось проверить (GitHub не ответил)";
            }
            return;
        }

        var currentVersion = DotShell.Core.Updates.UpdateChecker.Current;
        if (info.Version <= currentVersion)
        {
            _available = null;
            Log.Info($"update check: свежая {DotShell.Core.Updates.UpdateChecker.ActualText}, на GitHub {info.Version.ToString(3)}");
            if (manual)
            {
                _tray?.Notify("Обновления", $"У тебя свежая версия {DotShell.Core.Updates.UpdateChecker.ActualText}.");
                _vm.StatusLine = $"обновления: свежая версия {DotShell.Core.Updates.UpdateChecker.ActualText}";
            }
            return;
        }

        _available = info;
        Log.Info($"update check: доступна {info.Version.ToString(3)} (у нас {DotShell.Core.Updates.UpdateChecker.CurrentText})");


        // Окно вместо шарика: обновление — редкое событие, двухсекундное сообщение
        // трея слишком легко пропустить.
        var skipped = _services.Settings.Current.SkippedUpdateVersion;
        bool muted = string.Equals(skipped, info.Version.ToString(3), StringComparison.OrdinalIgnoreCase);

        if (!muted && !_updateShown)
        {
            _updateShown = true;
            var toShow = info;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var window = new UpdateWindow(toShow, skipThis =>
                    {
                        if (!skipThis) return;
                        _services.Settings.Update(s => s.SkippedUpdateVersion = toShow.Version.ToString(3));
                        _vm.StatusLine = $"обновление {toShow.Version.ToString(3)} больше не напоминаю";
                    });
                    window.ShowDialog();
                }
                catch (Exception ex) { Log.Info($"update window failed: {ex.Message}"); }
            });
        }
        // Результат — не только в трее: иначе проверить обновление,
        // не заглядывая в меню трея, было невозможно.
        _vm.StatusLine = $"обновления: есть {info.Version.ToString(3)} — «Скачать обновление» в трее";
        _tray?.Notify(
            $"Есть версия {info.Version.ToString(3)}",
            "Остров — в трее, пункт «Скачать обновление»: откроется страница релиза.");
    }

    /// <summary>Открыть страницу загрузки последнего релиза (или сразу файл).</summary>
    private void OpenUpdatePage()
    {
        var url = _available?.PageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info($"open update page failed: {ex.Message}"); }
    }

    /// <summary>Первая проверка — не в момент старта, а когда остров уже обжился.</summary>
    private async Task DelayedUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            await CheckUpdatesAsync(manual: false);
        }
        catch (Exception ex) { Log.Info($"update check failed: {ex.Message}"); }
    }

    private void OnMenuExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}