namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Runtime.InteropServices;
using System.Collections.Specialized;
using System.Threading;
using System.Threading.Tasks;
using DotShell.Core;
using DotShell.Core.Native;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Виджет рабочего стола: маленькое прозрачное окно-«островок» в стиле шелла.
///
/// Принципы, выстраданные островом:
///   • AllowsTransparency — клики по прозрачным пикселям проходят сквозь окно;
///   • ShowActivated=false — виджет не крадёт фокус у работы;
///   • НЕ topmost — виджет живёт НА рабочем столе, под окнами, а не поверх игры;
///   • WS_EX_TOOLWINDOW — не мелькает в Alt+Tab;
///   • таймеры останавливаются на Closed: шелл висит 24/7, утечки запрещены.
///
/// Перетаскивание — честный DragMove за подложку; положение запоминает
/// WidgetsManager в widgets.json.
/// </summary>
public sealed partial class WidgetWindow : Window
{
    private readonly string _type;
    private readonly string _path;
    private readonly Border _root;
    private readonly List<DispatcherTimer> _timers = new();
    private readonly Action<IReadOnlyList<string>>? _savePinned;
    private readonly IReadOnlyList<string> _initialPinned;
    private readonly Action<DotShell.Core.Widgets.FolderCollectionSpec?>? _saveCollection;
    private readonly DotShell.Core.Widgets.FolderCollectionSpec? _initialCollection;

    public string WidgetType => _type;
    public string WidgetPath => _path;

    public WidgetWindow(string type, int x, int y, int width = 0, int height = 0, string path = "",
        IEnumerable<string>? initialPinned = null, Action<IReadOnlyList<string>>? savePinned = null,
        DotShell.Core.Widgets.FolderCollectionSpec? initialCollection = null,
        Action<DotShell.Core.Widgets.FolderCollectionSpec?>? saveCollection = null)
    {
        _type = type;
        _path = path ?? string.Empty;
        _savePinned = savePinned;
        _initialPinned = initialPinned?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ?? (IReadOnlyList<string>)Array.Empty<string>();
        _initialCollection = initialCollection;
        _saveCollection = saveCollection;
        Title = "DotShell widget";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        SizeToContent = SizeToContent.WidthAndHeight;

        Resources.MergedDictionaries.Add((ResourceDictionary)
            Application.LoadComponent(new Uri("/Widgets/WidgetStyles.xaml", UriKind.Relative)));

        var outer = new Grid { Margin = new Thickness(10) };
        _root = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12),
            Cursor = Cursors.SizeAll,
        };
        _root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        _root.SetResourceReference(Border.BorderBrushProperty, "DsAccentSoft");
        SetContent(type);
        outer.Children.Add(_root);

        Loaded += (_, _) =>
        {
            KeepOnDesktopLayer();

            // Появление виджета: раньше он «возникал» мгновенно.
            Motion.FadeIn(this, 0);
        };

        // Ручка размера: висит поверх содержимого в правом нижнем углу.
        var grip = new Thumb { Style = (Style)FindResource("WResizeGrip") };
        grip.DragStarted += (_, _) =>
        {
            // С этого момента размер ведём руками, а не «по содержимому».
            SizeToContent = SizeToContent.Manual;
            Width = ActualWidth;
            Height = ActualHeight;
        };
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Max(190, Width + e.HorizontalChange);
            Height = Math.Max(84, Height + e.VerticalChange);
        };
        grip.DragCompleted += (_, _) => WidgetsManager.OnResized(this);
        outer.Children.Add(grip);

        Content = outer;

        // Размер, который человек уже подобрал: не заставляем подбирать заново.
        if (width > 0 && height > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Width = width;
            Height = height;
        }

        // Тянем виджет только за пустое место подложки. Иначе клик по строке
        // приложения или по кнопке начинал DragMove и таскал всё окно —
        // виджет «уезжал» вместо того, чтобы сделать то, о чём его просят.
        _root.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, _root)) DragMove();
        };
        _root.MouseLeftButtonUp += (_, _) => WidgetsManager.OnMoved(this);

        var remove = new MenuItem { Header = "Убрать виджет" };
        remove.Click += (_, _) => WidgetsManager.CloseWindow(this);
        _root.ContextMenu = new ContextMenu();
        _root.ContextMenu.Items.Add(remove);

        // У виджетов со своим меню (трей, курсы, погода, папка) меню окна убираем:
        // иначе правый клик открывал только «Убрать виджет», а настройки
        // виджета были недоступны. Пункт «Убрать» добавляется внутрь своего меню.
        if (type is "tray" or "currency" or "weather" or "folder") _root.ContextMenu = null;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Убираем из Alt+Tab: виджет — не «окно программы», а часть стола.
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_TOOLWINDOW;
            ex &= ~Win32.WS_EX_APPWINDOW;

            // Режим «на обоях»/desktop host: окно не активируется и уходит под остальные окна.
            var settings = ShellServices.Instance.Settings.Current;
            bool desktopMode = settings.DesktopWidgets || settings.DesktopHostEnabled;
            if (desktopMode)
            {
                ex |= Win32.WS_EX_NOACTIVATE;
            }

            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

            if (desktopMode)
            {
                SendToDesktopLayer();
            }

            // Авто-цвет не должен брать пробы с самих виджетов (иначе палитра
            // закольцуется сама на себя).
            ShellServices.Instance.OwnWindows.Add(hwnd);
        };

        SizeChanged += (_, _) => _relayout?.Invoke(ActualWidth - 24, ActualHeight - 24);



        Left = x;
        

        Top = y;

        Closed += (_, _) =>
        {
            foreach (var t in _timers) t.Stop();
            _timers.Clear();
            _mediaUnsubscribe?.Invoke();
            _paletteUnsubscribe?.Invoke();
        };
    }

    /// <summary>Не выпускаем виджет за пределы экрана после перетаскивания.</summary>
    /// <summary>Не отпускает виджет за пределы экрана после перетаскивания.</summary>
    public void ClampToScreen()
    {
        // Виртуальный экран: виджет может жить на другом дисплее.
        var vs = MonitorHelper.GetVirtualScreenDip();

        double width = ActualWidth > 0 ? ActualWidth : Width;
        double height = ActualHeight > 0 ? ActualHeight : Height;
        if (double.IsNaN(width) || width <= 0) width = 160;
        if (double.IsNaN(height) || height <= 0) height = 90;

        Left = Math.Clamp(Left, vs.Left - width + 80, vs.Right - 80);
        Top = Math.Clamp(Top, vs.Top, vs.Bottom - 40);

        // Прилипание к сетке: 0 — выкл, 8 или 16 px.
        int grid = Math.Clamp(ShellServices.Instance.Settings.Current.WidgetGrid, 0, 16);
        if (grid > 0)
        {
            Left = Math.Round(Left / grid) * grid;
            Top = Math.Round(Top / grid) * grid;
        }
    }


    private DispatcherTimer Timer(int ms, Action tick)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        t.Tick += (_, _) => tick();
        _timers.Add(t);
        t.Start();
        return t;
    }

    // ------------------------------------------------------------------
    //  Содержимое по типам
    // ------------------------------------------------------------------

    private Action<double, double>? _relayout;


    private Action? _mediaUnsubscribe;
    private Action? _paletteUnsubscribe;

    /// <summary>
    /// Пересобрать содержимое виджета (например, после смены города погоды).
    /// Таймеры старого тела останавливаются при его удалении, поэтому утечки нет.
    /// </summary>
    /// <summary>
    /// Содержимое виджета поверх рисунка острова: сетка, волны, топо — то же, что
    /// в капсуле. Рисунок лежит над фоном и под содержимым, поэтому текст остаётся
    /// читаемым при любой плотности.
    /// </summary>
    private void SetContent(string type)
    {
        _relayout = null;   // новому составу — своё правило композиции

        var settings = ShellServices.Instance?.Settings.Current;

        var backdrop = new DotShell.Island.Wpf.Controls.IslandBackdrop
        {
            Style = string.IsNullOrWhiteSpace(settings?.BackdropStyle) ? "none" : settings!.BackdropStyle,
            Strength = Math.Clamp((settings?.BackdropOpacity ?? 60) / 100.0, 0, 1),
            Corner = 15,
        };

        var layer = new Grid();
        layer.Children.Add(backdrop);
        layer.Children.Add(BuildContent(type));

        _root.Child = layer;
    }
    public void Reload()
    {
        try
        {
            // Перед пересборкой обязательно снимаем всё, что держало старый состав:
            // таймеры продолжали тикать по мёртвым элементам, а media.PropertyChanged
            // получал обработчик поверх старого — нагрузка росла с каждой сменой темы.
            foreach (var t in _timers) t.Stop();
            _timers.Clear();

            _mediaUnsubscribe?.Invoke();
            _mediaUnsubscribe = null;

            _paletteUnsubscribe?.Invoke();
            _paletteUnsubscribe = null;

            _relayout = null;

            // Через SetContent, а не _root.Child: только он собирает подложку острова,
            // которую Reload раньше терял.
            SetContent(_type);
        }
        catch { }
    }
    /// <summary>Пункт «Убрать виджет» для собственных меню виджетов.</summary>
    private MenuItem RemoveWidgetItem()
    {
        var item = new MenuItem { Header = "Убрать виджет" };
        item.Click += (_, _) => WidgetsManager.CloseWindow(this);
        return item;
    }
    private UIElement BuildContent(string type)
    {
        _relayout = null;

        var content = BuildBody(type);

        // Списки (настройки, дубликаты, карта окон) раньше были зажаты фиксированной
        // высотой: увеличиваешь виджет — растёт чёрное поле, а список стоит на месте.
        // Теперь единственный список тянется вместе с виджетом.
        if (_relayout is null && content is DependencyObject root)
        {
            var lists = Descendants<ScrollViewer>(root).ToList();
            if (lists.Count == 1)
            {
                var list = lists[0];
                _relayout = (_, h) => list.MaxHeight = Math.Max(120, h - 100);
            }
        }

        return content;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private UIElement BuildBody(string type) => type switch
    {
        "clock" => BuildClock(),
        "eq" => BuildEq(),
        "media" => BuildMedia(),
        "system" => BuildSystem(),
        "units" => BuildUnits(),
        "settings" => BuildSettings(),
        "clean" => BuildClean(),
        "dupes" => BuildDupes(),
        "update" => BuildUpdate(),
        "windows" => BuildWindows(),
        "audio" => BuildAudioRouter(),
        "currency" => BuildCurrency(),
        "tray" => BuildTray(),
        "weather" => BuildWeather(),
        "calendar" => BuildCalendar(),
        "folder" => BuildFolder(),
        _ => new TextBlock { Text = "?" },
    };

    /// <summary>
    /// Часы: семисегментные цифры из точек.
    ///
    /// Обычная точечная матрица на маленьком размере не читалась — владелец
    /// справедливо попросил «как на старых электронных». Семь сегментов дают
    /// узнаваемую форму цифры даже по три точки на сегмент, и это всё ещё точки.
    /// </summary>
    private UIElement BuildClock()
    {
        // Три композиции вместо одной: в полоске нужны только цифры, в квадрате —
        // ещё дата, в широком прямоугольнике дата уезжает вправо и добавляется день
        // недели. Раньше часы просто растягивались, и на широком виджете половина
        // места пустовала.
        var clockStrip = new SegmentClock { Margin = new Thickness(0) };
        var clockSmall = new SegmentClock { Margin = new Thickness(0) };
        var clockWide = new SegmentClock { Margin = new Thickness(0) };

        var dateSmall = WidgetAdaptive.Detail(11);
        var dateWide = WidgetAdaptive.Caption(string.Empty);
        var weekday = WidgetAdaptive.Detail(12);

        var stripPanel = new Grid();
        stripPanel.Children.Add(clockStrip);

        var smallPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        smallPanel.Children.Add(clockSmall);
        dateSmall.Margin = new Thickness(0, 4, 0, 0);
        smallPanel.Children.Add(dateSmall);

        var wideRight = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        wideRight.Children.Add(dateWide);
        wideRight.Children.Add(weekday);

        var widePanel = new Grid();
        widePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        widePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        widePanel.Children.Add(clockWide);
        Grid.SetColumn(wideRight, 1);
        widePanel.Children.Add(wideRight);

        var host = new Grid();
        host.Children.Add(stripPanel);
        host.Children.Add(smallPanel);
        host.Children.Add(widePanel);

        object? current = null;

        void Refresh()
        {
            var now = DateTime.Now;
            dateSmall.Text = now.ToString("dddd, d MMMM");
            dateWide.Text = now.ToString("MMMM").ToUpperInvariant();
            weekday.Text = now.ToString("dddd, d");
        }

        Refresh();
        Timer(20_000, Refresh);

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);
            double timeHeight = Math.Max(24, Math.Min(h, 120));

            clockStrip.Height = timeHeight;
            clockSmall.Height = Math.Max(24, Math.Min(h * 0.62, 96));
            clockWide.Height = Math.Max(24, Math.Min(h * 0.85, 120));
            clockStrip.Width = clockSmall.Width = clockWide.Width = Math.Max(80, w);

            var next = size == WidgetSizeClass.Strip
                ? (UIElement)stripPanel
                : w > h * 1.55
                    ? widePanel
                    : smallPanel;

            WidgetAdaptive.Show(host, next, ref current);
        };

        return host;
    }

    /// <summary>
    /// Плеер, который умеет быть разного размера.
    ///
    /// Маленький — это пульт: название, исполнитель, три кнопки.
    /// Средний — добавляется полоса прогресса и время.
    /// Большой — обложка во всю высоту и крупный трек.
    /// Раньше виджет был 244 пикселя навсегда: половина места пустовала,
    /// а нужного (времени, обложки) не было вовсе.
    /// </summary>
    /// <summary>
    /// Плеер-виджет в трёх раскладках, как у Dotify:
    ///
    ///   полоса   — широкий и низкий: обложка слева, текст, кнопки справа;
    ///   карточка — обложка сверху во всю ширину, под ней текст и кнопки;
    ///   обложка  — высокий и близкий к квадрату: картинка во весь виджет,
    ///              текст поверх неё на градиенте.
    ///
    /// Почему так, а не «спрятать лишнее»: размер виджета определяет не количество
    /// деталей, а композицию. Один каркас на все размеры всегда даёт либо пустое
    /// место, либо кашу из мелких элементов.
    /// </summary>
    private UIElement BuildMedia()
    {
        var media = ShellServices.Instance.Media;

        var host = new Grid();

        // ---------- общие помощники ----------

        static Image Art() => new() { Stretch = Stretch.UniformToFill };

        var ink = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        var muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));

        // Цвета задаём явно. Через стиль WTitle они приходили чёрными: стиль
        // лежит в ресурсах окна, а DynamicResource DsText там не разрешался.
        static TextBlock Line(double size, Brush brush, Thickness margin, bool bold = true) => new()
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = margin,
        };

        static ProgressBar Bar(double height) => new()
        {
            Height = height,
            Minimum = 0,
            Maximum = 100,
            BorderThickness = new Thickness(0),
        };

        Border ArtBox(Image image, double radius)
        {
            var box = new Border
            {
                CornerRadius = new CornerRadius(radius),
                ClipToBounds = true,
                Background = Brushes.Transparent,
            };
            box.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
            box.Child = image;
            return box;
        }

        Button Icon(string glyph, string styleKey, bool accent = false)
        {
            var button = new Button
            {
                Content = glyph,
                Style = (Style)FindResource(styleKey),
                Padding = new Thickness(0),
            };
            return button;
        }

        // ---------- 1. полоса: обложка слева, текст, кнопки справа ----------

        var pillArt = Art();
        var pillArtBox = ArtBox(pillArt, 12);
        pillArtBox.Width = pillArtBox.Height = 56;
        pillArtBox.VerticalAlignment = VerticalAlignment.Center;

        var pillTitle = Line(15, ink, new Thickness(0));
        var pillArtist = Line(11, muted, new Thickness(0, 1, 0, 0), bold: false);

        var pillText = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 10, 0) };
        pillText.Children.Add(pillTitle);
        pillText.Children.Add(pillArtist);

        var pillPrev = Icon("⏮", "WRound");
        var pillPlay = Icon("▶", "WRoundAccent");
        var pillNext = Icon("⏭", "WRound");
        pillPrev.Click += (_, _) => _ = media.PreviousAsync();
        pillPlay.Click += (_, _) => _ = media.TogglePlayAsync();
        pillNext.Click += (_, _) => _ = media.NextAsync();

        var pillButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        pillButtons.Children.Add(pillPrev);
        pillButtons.Children.Add(pillPlay);
        pillButtons.Children.Add(pillNext);

        var pillBar = Bar(2);
        pillBar.SetResourceReference(ProgressBar.ForegroundProperty, "DsAccent");
        pillBar.SetResourceReference(ProgressBar.BackgroundProperty, "DsBorder");
        pillBar.VerticalAlignment = VerticalAlignment.Bottom;
        // Полоса прогресса идёт через все колонки: раньше она жила в нулевой,
        // и её отступы раздували колонку с обложкой — текст уезжал от картинки.
        pillBar.Margin = new Thickness(70, 0, 16, 0);

        var pill = new Grid();
        pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pillArtBox, 0);
        Grid.SetColumn(pillText, 1);
        Grid.SetColumn(pillButtons, 2);
        Grid.SetColumnSpan(pillBar, 3);
        pill.Children.Add(pillBar);
        pill.Children.Add(pillArtBox);
        pill.Children.Add(pillText);
        pill.Children.Add(pillButtons);

        // ---------- 2. карточка: обложка сверху, текст снизу ----------

        var cardArt = Art();
        var cardArtBox = ArtBox(cardArt, 14);
        cardArtBox.VerticalAlignment = VerticalAlignment.Stretch;

        var cardTitle = Line(20, ink, new Thickness(0, 12, 0, 0));
        var cardArtist = Line(12, muted, new Thickness(0, 2, 0, 0), bold: false);

        var cardBar = Bar(3);
        cardBar.SetResourceReference(ProgressBar.ForegroundProperty, "DsAccent");
        cardBar.SetResourceReference(ProgressBar.BackgroundProperty, "DsBorder");
        cardBar.Margin = new Thickness(0, 12, 0, 0);

        var cardTimes = Line(10, muted, new Thickness(0, 4, 0, 0), bold: false);

        var cardPrev = Icon("⏮", "WRound");
        var cardPlay = Icon("▶", "WRoundAccent");
        var cardNext = Icon("⏭", "WRound");
        cardPrev.Click += (_, _) => _ = media.PreviousAsync();
        cardPlay.Click += (_, _) => _ = media.TogglePlayAsync();
        cardNext.Click += (_, _) => _ = media.NextAsync();

        var cardButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 10, 0, 0),
        };
        cardButtons.Children.Add(cardPrev);
        cardButtons.Children.Add(cardPlay);
        cardButtons.Children.Add(cardNext);

        var cardInfo = new StackPanel();
        cardInfo.Children.Add(cardTitle);
        cardInfo.Children.Add(cardArtist);
        cardInfo.Children.Add(cardBar);
        cardInfo.Children.Add(cardTimes);
        cardInfo.Children.Add(cardButtons);

        var card = new Grid();
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(cardArtBox, 0);
        Grid.SetRow(cardInfo, 1);
        card.Children.Add(cardArtBox);
        card.Children.Add(cardInfo);

        // ---------- 3. обложка: картинка фоном, текст на градиенте ----------

        var coverArt = Art();
        var coverArtBox = ArtBox(coverArt, 14);

        var shade = new Border
        {
            CornerRadius = new CornerRadius(14),
            IsHitTestVisible = false,
        };
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.32),
            EndPoint = new Point(0, 1),
        };
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0x00, 0x00, 0x00), 0));
        gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xD0, 0x05, 0x05, 0x07), 1));
        shade.Background = gradient;

        var coverTitle = Line(22, Brushes.White, new Thickness(0));
        coverTitle.Foreground = Brushes.White;
        coverTitle.TextWrapping = TextWrapping.NoWrap;

        var coverArtist = new TextBlock
        {
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 3, 0, 0),
        };

        var coverBar = Bar(2);
        coverBar.Foreground = Brushes.White;
        coverBar.Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        coverBar.Margin = new Thickness(0, 12, 0, 0);

        var coverTimes = new TextBlock
        {
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 4, 0, 0),
        };

        var coverPrev = Icon("⏮", "WRound");
        var coverPlay = Icon("▶", "WRoundAccent");
        var coverNext = Icon("⏭", "WRound");
        coverPrev.Click += (_, _) => _ = media.PreviousAsync();
        coverPlay.Click += (_, _) => _ = media.TogglePlayAsync();
        coverNext.Click += (_, _) => _ = media.NextAsync();

        var coverButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 2),
        };
        coverButtons.Children.Add(coverPrev);
        coverButtons.Children.Add(coverPlay);
        coverButtons.Children.Add(coverNext);

        var coverInfo = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(16, 0, 16, 12),
        };
        coverInfo.Children.Add(coverTitle);
        coverInfo.Children.Add(coverArtist);
        coverInfo.Children.Add(coverBar);
        coverInfo.Children.Add(coverTimes);
        coverInfo.Children.Add(coverButtons);

        var cover = new Grid();
        cover.Children.Add(coverArtBox);
        cover.Children.Add(shade);
        cover.Children.Add(coverInfo);

        host.Children.Add(pill);
        host.Children.Add(card);
        host.Children.Add(cover);

        // ---------- данные ----------

        void ApplyText(string title, string artist)
        {
            foreach (var block in new[] { pillTitle, cardTitle, coverTitle })
                block.Text = title;

            foreach (var block in new[] { pillArtist, cardArtist, coverArtist })
                block.Text = artist;
        }

        Image[] arts = { pillArt, cardArt, coverArt };

        void ApplyArtwork()
        {
            var bytes = media.ArtworkBytes;
            BitmapImage? bitmap = null;

            if (bytes is { Length: > 0 })
            {
                try
                {
                    bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = new MemoryStream(bytes);
                    bitmap.EndInit();
                    bitmap.Freeze();
                }
                catch { bitmap = null; }
            }

            foreach (var art in arts) art.Source = bitmap;
        }

        void ApplyPlaying()
        {
            string glyph = media.IsPlaying ? "❚❚" : "▶";
            pillPlay.Content = glyph;
            cardPlay.Content = glyph;
            coverPlay.Content = glyph;
        }

        // ---------- выбор раскладки по размеру ----------

        string current = "";

        void Relayout()
        {
            double w = Math.Max(120, (ActualWidth > 0 ? ActualWidth : 320) - 24);
            double h = Math.Max(60, (ActualHeight > 0 ? ActualHeight : 132) - 24);
            double ratio = h / w;

            string mode = h < 170
                ? "pill"
                : ratio > 0.85 && h > 240
                    ? "cover"
                    : "card";

            if (mode == current)
            {
                // Размер мог измениться внутри того же режима — подгоняем габариты.
                if (mode == "pill")
                {
                    pillArtBox.Width = pillArtBox.Height = Math.Clamp(h * 0.42, 40, 72);
                    pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 16, 0);
                }
                return;
            }

            current = mode;

            Dispatcher.BeginInvoke(() => Log.Info(
                $"media: mode={mode} window={ActualWidth:0}x{ActualHeight:0} " +
                $"pill={pill.ActualWidth:0}x{pill.ActualHeight:0} " +
                $"art={pillArtBox.ActualWidth:0} text={pillText.ActualWidth:0} " +
                $"title='{pillTitle.Text}' fg={(pillTitle.Foreground is null ? "null" : pillTitle.Foreground.ToString())} " +
                $"but={pillButtons.ActualWidth:0}"));

            pill.Visibility = mode == "pill" ? Visibility.Visible : Visibility.Collapsed;
            card.Visibility = mode == "card" ? Visibility.Visible : Visibility.Collapsed;
            cover.Visibility = mode == "cover" ? Visibility.Visible : Visibility.Collapsed;

            if (mode == "pill")
            {
                pillArtBox.Width = pillArtBox.Height = Math.Clamp(h * 0.42, 40, 72);
                pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 16, 0);
            }

            if (mode == "card")
            {
                // Кнопки среднего размера: на карточке они не должны занимать
                // пол-экрана, но и в 30 px промахиваться нельзя.
                double size = Math.Clamp(w / 12, 30, 40);
                cardPrev.Width = cardNext.Width = size;
                cardPrev.Height = cardNext.Height = size;
                cardPlay.Width = cardPlay.Height = size + 6;

                cardTitle.FontSize = Math.Clamp(w / 18, 16, 24);
                cardArtist.FontSize = Math.Clamp(w / 28, 10, 14);
            }

            if (mode == "cover")
            {
                coverTitle.FontSize = Math.Clamp(w / 14, 18, 28);
                coverArtist.FontSize = Math.Clamp(w / 26, 11, 15);
            }

            // Входящая раскладка появляется плавно: переключение по ресайзу не
            // должно выглядеть как рывок.
            var incoming = mode == "pill" ? (UIElement)pill : mode == "card" ? card : cover;
            incoming.Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
            incoming.BeginAnimation(OpacityProperty, fade);
        }

        // ---------- подписка на данные ----------

        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
            Dispatcher.BeginInvoke(() =>
            {
                ApplyText(
                    string.IsNullOrWhiteSpace(media.Title) ? "ничего не играет" : media.Title,
                    string.IsNullOrWhiteSpace(media.Artist) ? media.SourceApp : media.Artist);

                ApplyPlaying();
                if (e.PropertyName is nameof(media.ArtworkBytes) or null) ApplyArtwork();
            });

        media.PropertyChanged += handler;
        _mediaUnsubscribe = () => media.PropertyChanged -= handler;
        handler(this, new System.ComponentModel.PropertyChangedEventArgs(null));
        ApplyArtwork();

        Timer(500, () =>
        {
            double total = media.Duration.TotalSeconds;
            double now = media.LivePosition.TotalSeconds;
            double percent = total > 0 ? Math.Clamp(now / total * 100, 0, 100) : 0;

            pillBar.Value = percent;
            cardBar.Value = percent;
            coverBar.Value = percent;

            static string Fmt(TimeSpan t) => t.TotalHours >= 1
                ? t.ToString(@"h\:mm\:ss")
                : t.ToString(@"m\:ss");

            string times = total > 0 ? $"{Fmt(media.LivePosition)} / {Fmt(media.Duration)}" : string.Empty;
            cardTimes.Text = times;
            coverTimes.Text = times;
        });

        // Размер меняется ручкой — раскладка должна пересчитываться вместе с ним.
        _relayout = (_, _) => Relayout();

        Relayout();
        Dispatcher.BeginInvoke(Relayout);
        return host;
    }

    /// <summary>
    /// Система: точечные дорожки вместо «цифр точками».
    ///
    /// Маленький виджет — две полоски, по ним мгновенно видно «сколько занято».
    /// Большой — те же полоски плюс проценты, гигабайты и батарея: размер
    /// добавляет информации, а не пустого места.
    /// </summary>
    /// <summary>
    /// Эквалайзер: тянется по ширине, но на полоске ему нужен свой режим —
    /// иначе «сталактиты» в 12 px превращаются в мусор из точек.
    /// </summary>
    private UIElement BuildEq()
    {
        var eq = new MiniEq
        {
            MinWidth = 120,
            MinHeight = 36,
            IsRunning = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);

            if (size == WidgetSizeClass.Strip)
            {
                eq.MinHeight = 10;
                eq.Margin = new Thickness(0, 2, 0, 2);
            }
            else
            {
                eq.MinHeight = 36;
                eq.Margin = new Thickness(6);
            }
        };

        return eq;
    }

    /// <summary>
    /// Юниты: маленький виджет — одно существо, обычный — два, большой — два и
    /// подпись, чтобы место не пустовало. Тот же принцип, что у плеера: размер
    /// меняет композицию.
    /// </summary>
    private UIElement BuildUnits()
    {
        var one = new Controls.Units { Margin = new Thickness(4), Interactive = true, TwoUnits = false };
        var two = new Controls.Units { Margin = new Thickness(6), Interactive = true };
        var note = WidgetAdaptive.Detail(10);

        var single = new Grid();
        single.Children.Add(one);

        var pair = new Grid();
        pair.Children.Add(two);

        // Крупный виджет — портрет: в 399×311 помещается 104×102 точек, это уже
        // не «два человечка», а тот же юнит в подробном разрешении.
        var portrait = new Controls.UnitPortrait
        {
            Margin = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var largePanel = new Grid();
        largePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        largePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        largePanel.Children.Add(portrait);
        note.Text = "портрет в точках · клик — искры";
        note.Margin = new Thickness(0, 6, 0, 0);
        Grid.SetRow(note, 1);
        largePanel.Children.Add(note);

        var host = new Grid();
        host.Children.Add(single);
        host.Children.Add(pair);
        host.Children.Add(largePanel);

        object? current = null;

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);

            var next = size is WidgetSizeClass.Strip or WidgetSizeClass.Small
                ? (UIElement)single
                : size == WidgetSizeClass.Large
                    ? largePanel
                    : pair;

            WidgetAdaptive.Show(host, next, ref current);
        };

        return host;
    }

    private UIElement BuildSystem()
    {
        var stats = ShellServices.Instance.Stats;

        TextBlock Label(string text) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = ResBrush("DsTextMuted"),
            Margin = new Thickness(0, 0, 0, 3),
        };

        // Полоска: две дорожки без подписей — на высоте в один палец подписи не
        // помещаются, а сами дорожки читаются.
        var stripCpu = new DotBar { Height = 8 };
        var stripRam = new DotBar { Height = 8, Margin = new Thickness(0, 6, 0, 0) };
        var stripPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stripPanel.Children.Add(stripCpu);
        stripPanel.Children.Add(stripRam);

        // Обычный: подписи с процентами и дорожки.
        var cpuLabel = Label("CPU");
        var ramLabel = Label("RAM");
        var cpuBar = new DotBar { Height = 12 };
        var ramBar = new DotBar { Height = 12 };

        var normalPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        normalPanel.Children.Add(cpuLabel);
        normalPanel.Children.Add(cpuBar);
        ramLabel.Margin = new Thickness(0, 10, 0, 3);
        normalPanel.Children.Add(ramLabel);
        normalPanel.Children.Add(ramBar);

        // Большой: свои дорожки (элемент не может жить в двух панелях сразу —
        // иначе «уже логический дочерний для другого элемента»).
        var cpuBarLarge = new DotBar { Height = 16 };
        var ramBarLarge = new DotBar { Height = 16, Margin = new Thickness(0, 0, 0, 0) };
        var details = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = ResBrush("DsTextMuted"),
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        var largePanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        largePanel.Children.Add(Label("CPU"));
        largePanel.Children.Add(cpuBarLarge);
        largePanel.Children.Add(new TextBlock
        {
            Text = "ПАМЯТЬ",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = ResBrush("DsTextMuted"),
            Margin = new Thickness(0, 12, 0, 3),
        });
        largePanel.Children.Add(ramBarLarge);
        largePanel.Children.Add(details);

        var host = new Grid();
        host.Children.Add(stripPanel);
        host.Children.Add(normalPanel);
        host.Children.Add(largePanel);

        object? current = null;

        void Refresh()
        {
            int ramPct = stats.TotalMemoryMb > 0
                ? (int)Math.Round((double)stats.UsedMemoryMb / stats.TotalMemoryMb * 100)
                : 0;

            double cpu = Math.Clamp(stats.Cpu / 100.0, 0, 1);
            double ram = ramPct / 100.0;

            stripCpu.Value = cpu;
            stripRam.Value = ram;
            cpuBar.Value = cpu;
            ramBar.Value = ram;
            cpuBarLarge.Value = cpu;
            ramBarLarge.Value = ram;

            cpuLabel.Text = $"CPU · {stats.Cpu:F0}%";
            ramLabel.Text = $"RAM · {ramPct}%";

            string battery = stats.HasBattery
                ? $"\nбатарея {stats.Battery}%{(stats.Charging ? " · заряжается" : string.Empty)}"
                : string.Empty;

            details.Text = $"{stats.UsedMemoryMb} из {stats.TotalMemoryMb} МБ занято{battery}";
        }

        Refresh();
        Timer(2000, Refresh);

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);

            var next = size == WidgetSizeClass.Strip
                ? (UIElement)stripPanel
                : size == WidgetSizeClass.Large
                    ? largePanel
                    : normalPanel;

            // Дорожки на большом виджете шире: иначе одинокая полоска в 12 px
            // выглядит как случайная черта.
            cpuBar.Height = ramBar.Height = 12;

            WidgetAdaptive.Show(host, next, ref current);
        };

        return host;
    }

    /// <summary>
    /// Курс доллара и юаня к рублю (ЦБ РФ).
    ///
    /// Маленький виджет — две цены со стрелками. Большой — у КАЖДОЙ валюты свой
    /// график: доллар ~80 ₽ и юань ~12 ₽, на общей оси юань превратился бы в
    /// плоскую линию у нуля, поэтому графиков два, а не один.
    ///
    /// Данные — официальный XML ЦБ, без ключей; кэш на 10 минут.
    /// </summary>
    /// <summary>
    /// Вторая валюта из настроек. Доллар всегда первый (это опорный курс),
    /// вторую человек выбирает сам через меню виджета.
    /// </summary>
    private static string SecondCurrency()
    {
        var code = ShellServices.Instance.Settings.Current.CurrencySecond;
        return string.IsNullOrWhiteSpace(code)
            ? DotShell.Core.Currency.CurrencyRates.CnyCode
            : code;
    }

    /// <summary>Меню выбора второй валюты (правый клик по виджету курса).</summary>
    private ContextMenu CurrencyMenu(Action refresh)
    {
        var settings = ShellServices.Instance.Settings;
        var menu = new ContextMenu();

        foreach (var (code, title) in DotShell.Core.Currency.CurrencyRates.Options)
        {
            var item = new MenuItem
            {
                Header = title,
                IsCheckable = true,
                IsChecked = string.Equals(settings.Current.CurrencySecond, code, StringComparison.OrdinalIgnoreCase),
            };

            var captured = code;
            item.Click += (_, _) =>
            {
                settings.Update(s => s.CurrencySecond = captured);
                refresh();
            };

            menu.Items.Add(item);
        }

        return menu;
    }
    private UIElement BuildCurrency()
    {
        var blocks = new Dictionary<string, (TextBlock Value, TextBlock Arrow, Sparkline Chart)>();
        var caption = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = ResBrush("DsTextMuted"),
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        var period = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 2) };
        var hint = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Foreground = ResBrush("DsTextMuted"),
            Text = "официальный курс ЦБ · обновляется раз в день",
            Margin = new Thickness(0, 4, 0, 0),
        };

        var root = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        int days = 7;

        UIElement Block(string code)
        {
            var header = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = DotShell.Core.Currency.CurrencyRates.Title(code),
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 10,
                Foreground = ResBrush("DsTextMuted"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            header.Children.Add(name);

            var value = new TextBlock
            {
                Text = "—",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Foreground = ResBrush("DsText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(value, 1);
            header.Children.Add(value);

            var arrow = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = ResBrush("DsAccent"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(arrow, 2);
            header.Children.Add(arrow);

            var chart = new Sparkline { Height = 54, Margin = new Thickness(0, 2, 0, 6) };

            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(chart);

            blocks[code] = (value, arrow, chart);
            return stack;
        }

        root.Children.Add(Block(DotShell.Core.Currency.CurrencyRates.UsdCode));
        root.Children.Add(Block(SecondCurrency()));
        root.Children.Add(period);
        root.Children.Add(caption);
        root.Children.Add(hint);

        void BuildPeriod()
        {
            period.Children.Clear();
            foreach (var (label, value) in new[] { ("7 ДНЕЙ", 7), ("МЕСЯЦ", 30), ("КВАРТАЛ", 90) })
            {
                var chip = new Button
                {
                    Style = (Style)FindResource("WButton"),
                    Content = label,
                    Margin = new Thickness(0, 0, 6, 0),
                    FontSize = 9,
                };
                bool active = days == value;
                chip.SetResourceReference(Button.BorderBrushProperty, active ? "DsAccent" : "DsBorder");
                chip.SetResourceReference(Button.ForegroundProperty, active ? "DsText" : "DsTextMuted");
                var captured = value;
                chip.Click += (_, _) =>
                {
                    days = captured;
        BuildPeriod();
                    _ = LoadAsync();
                };
                period.Children.Add(chip);
            }
        }

        async Task LoadAsync()
        {
            var parts = new List<string>();

        foreach (var code in new[] { DotShell.Core.Currency.CurrencyRates.UsdCode, SecondCurrency() })
            {
                var series = await DotShell.Core.Currency.CurrencyRates.GetAsync(code, days);
                if (series is null) continue;

                parts.Add($"{DotShell.Core.Currency.CurrencyRates.Title(code).ToLowerInvariant()} {series.DeltaPercent:+0.00;-0.00}%");

                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!blocks.TryGetValue(code, out var block)) return;

                    block.Value.Text = series.Last.ToString("F2");

                    // Дельта меньше половины копейки — это не рост и не падение:
                    // стрелка вверх при «+0,00» выглядела как вранье.
                    var flat = Math.Abs(series.Delta) < 0.005;
                    block.Arrow.Text = flat
                        ? "= 0,00"
                        : series.Delta > 0
                            ? $"▲ {series.Delta:0.00}"
                            : $"▼ {Math.Abs(series.Delta):0.00}";
                    block.Arrow.SetResourceReference(TextBlock.ForegroundProperty,
                        flat ? "DsTextMuted" : series.Delta > 0 ? "DsAccent" : "DsTextMuted");

                    block.Chart.Values = series.History.Select(p => p.Value).ToList();
                });
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                caption.Text = parts.Count > 0
                    ? $"ЦБ РФ · за {(days == 7 ? "неделю" : days == 30 ? "месяц" : "квартал")}: " + string.Join(" · ", parts)
                    : "курс недоступен — нет связи с ЦБ";
            });
        }

        BuildPeriod();

        // Правый клик — выбор второй валюты (доллар остаётся первым).
        // Пустой колбэк означал, что выбор второй валюты ничего не меняет до
        // пересоздания виджета. Reload пересобирает блоки под новый код.
        var currencyMenu = CurrencyMenu(Reload);
        root.MouseRightButtonUp += (_, e) =>
        {
            currencyMenu.PlacementTarget = root;
            currencyMenu.IsOpen = true;
            e.Handled = true;
        };
        _ = LoadAsync();
        Timer(900, () => _ = LoadAsync());

        _relayout = (_, h) =>
        {
            bool big = h >= 150;
            period.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
            caption.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
            hint.Visibility = big ? Visibility.Visible : Visibility.Collapsed;

            // Заголовки, чипы, подпись и подсказка занимают ~178 px; всё остальное
            // делим между двумя графиками, иначе контент выше окна и обрезается.
            double chartHeight = Math.Clamp((h - 178) / 2, 24, 120);
            foreach (var block in blocks.Values)
            {
                block.Chart.Visibility = big ? Visibility.Visible : Visibility.Collapsed;
                block.Chart.Height = chartHeight;
            }
        };

        return root;
    }
    /// <summary>Кисть из ресурсов окна (для контролов, созданных кодом).</summary>
    /// <summary>
    /// «Умная папка» на рабочем столе: живой вид содержимого папки.
    /// Удобства: фильтр, сортировка, навигация внутрь подпапок, контекстные действия,
    /// drag&drop (в т.ч. между дисками — копирование), автообновление.
    /// </summary>
    private UIElement BuildFolder()
    {
        string rootPath = _path;
        string currentPath = rootPath;
        bool missing = string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath);
        string filter = string.Empty;
        int sortMode = 0; // 0 имя, 1 дата, 2 размер, 3 тип
        int groupMode = 0; // 0 нет, 1 тип, 2 дата
        var collapsedGroups = new HashSet<string>(StringComparer.Ordinal);
        var pinned = new HashSet<string>(_initialPinned, StringComparer.OrdinalIgnoreCase);
        void SavePins() => _savePinned?.Invoke(pinned.ToList());
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rowByPath = new Dictionary<string, Border>(StringComparer.OrdinalIgnoreCase);
        var visiblePaths = new List<string>();
        string? lastClickedPath = null;
        var cutPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool cutPending = false;
        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dotByPath = new Dictionary<string, UIElement>(StringComparer.OrdinalIgnoreCase);
        bool marqueeActive = false;
        bool marqueePressed = false;
        Point marqueeStart = new();
        HashSet<string> marqueeSnapshot = new(StringComparer.OrdinalIgnoreCase);
        ModifierKeys marqueeMods = ModifierKeys.None;
        DispatcherTimer? marqueeAutoScroll = null;

        var collection = _initialCollection is null ? null : new DotShell.Core.Widgets.FolderCollectionSpec
        {
            Enabled = _initialCollection.Enabled,
            Preset = _initialCollection.Preset ?? string.Empty,
            Scope = _initialCollection.Scope ?? string.Empty,
            Recursive = _initialCollection.Recursive,
            Kinds = _initialCollection.Kinds?.ToList() ?? new List<string>(),
            NameContains = _initialCollection.NameContains ?? string.Empty,
            WithinDays = _initialCollection.WithinDays,
            MinSizeMb = _initialCollection.MinSizeMb,
            Limit = _initialCollection.Limit > 0 ? _initialCollection.Limit : 50,
        };
        void SaveCollection() => _saveCollection?.Invoke(collection);
        bool CollectionActive() => collection is { Enabled: true };

        bool fileOpRunning = false;



        FileSystemWatcher? collectionWatcher = null;
        DispatcherTimer? collectionDebounce = null;
        string? collectionWatcherPath = null;
        bool collectionWatcherRecursive = false;

        void DisposeCollectionWatcher()
        {
            try { collectionWatcher?.Dispose(); } catch { }
            collectionWatcher = null;
            collectionWatcherPath = null;
            collectionWatcherRecursive = false;
        }

        void EnsureCollectionDebounce()
        {
            if (collectionDebounce is not null) return;
            collectionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            collectionDebounce.Tick += (_, _) => { collectionDebounce.Stop(); if (CollectionActive() && !fileOpRunning) Rebuild(); };
            _timers.Add(collectionDebounce);
        }

        void TriggerCollectionRefresh()
        {
            if (fileOpRunning) return;
            if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(TriggerCollectionRefresh); return; }
            EnsureCollectionDebounce();
            collectionDebounce!.Stop();
            collectionDebounce.Start();
        }

        void UpdateFolderWatcher()
        {
            string scope;
            bool rec;
            if (CollectionActive() && collection is not null)
            {
                scope = !string.IsNullOrWhiteSpace(collection.Scope) ? collection.Scope : rootPath;
                if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope)) scope = rootPath;
                rec = collection.Recursive;
            }
            else
            {
                scope = currentPath;
                rec = false;
            }

            if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope))
            {
                DisposeCollectionWatcher();
                return;
            }

            if (collectionWatcher is not null && string.Equals(collectionWatcherPath, scope, StringComparison.OrdinalIgnoreCase) && collectionWatcherRecursive == rec)
                return;

            DisposeCollectionWatcher();
            try
            {
                var w = new FileSystemWatcher(scope)
                {
                    IncludeSubdirectories = rec,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                FileSystemEventHandler h = (_, _) => TriggerCollectionRefresh();
                RenamedEventHandler rh = (_, _) => TriggerCollectionRefresh();
                w.Created += h; w.Changed += h; w.Deleted += h; w.Renamed += rh;
                collectionWatcher = w;
                collectionWatcherPath = scope;
                collectionWatcherRecursive = rec;
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: watcher failed {scope} - {ex.Message}");
                DisposeCollectionWatcher();
            }
        }

        Closed += (_, _) => { try { collectionDebounce?.Stop(); } catch { } DisposeCollectionWatcher(); };
        var prevSig = new Dictionary<string, (long size, long ticks)>(StringComparer.OrdinalIgnoreCase);
        string snapshotPath = string.Empty;
        bool haveSnapshot = false;

        var title = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = ResBrush("DsText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var subtitle = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Foreground = ResBrush("DsTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
            ToolTip = rootPath,
        };

        var list = new StackPanel { Background = Brushes.Transparent };
        var marqueeRect = new System.Windows.Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromArgb(0xA0, 0xFF, 0xFF, 0xFF)),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        var marqueeCanvas = new Canvas { IsHitTestVisible = false };
        marqueeCanvas.Children.Add(marqueeRect);
        var listHost = new Grid { Background = Brushes.Transparent };
        listHost.Children.Add(list);
        listHost.Children.Add(marqueeCanvas);
        var scroll = new ScrollViewer
        {
            Content = listHost,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 6, 0, 0),
            Focusable = true,
            Background = Brushes.Transparent,
        };

        // Панель быстрого предпросмотра (Quick Look-идея, адаптированная под компактный виджет).
        var previewImage = new Image { Stretch = Stretch.Uniform, MaxHeight = 120, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
        var previewName = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ResBrush("DsText"), TextTrimming = TextTrimming.CharacterEllipsis };
        var previewMeta = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 9, Foreground = ResBrush("DsTextMuted"), TextWrapping = TextWrapping.Wrap };
        var previewText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 9, Foreground = ResBrush("DsTextMuted"), TextWrapping = TextWrapping.Wrap, MaxHeight = 70, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = Visibility.Collapsed };
        var previewPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        previewPanel.Children.Add(previewImage);
        previewPanel.Children.Add(previewName);
        previewPanel.Children.Add(previewMeta);
        previewPanel.Children.Add(previewText);

        var undoStack = new Stack<(string message, List<(string kind, string from, string to, bool isDir)> actions)>();
        var undoText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Foreground = ResBrush("DsTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var undoBtn = WBtn("Отменить", "Отменить последнее действие (Ctrl+Z)");
        undoBtn.Padding = new Thickness(6, 2, 6, 2);
        undoBtn.Margin = new Thickness(8, 0, 0, 0);
        var undoBar = new DockPanel { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        DockPanel.SetDock(undoBtn, Dock.Right);
        undoBar.Children.Add(undoBtn);
        undoBar.Children.Add(undoText);

        CancellationTokenSource? fileOpCts = null;

        var undoTtlTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        undoTtlTimer.Tick += (_, _) =>
        {
            undoTtlTimer.Stop();
            if (!fileOpRunning && undoStack.Count > 0)
            {
                // TTL: скрываем панель, но оставляем возможность отмены по Ctrl+Z (стек сохраняется).
                undoBar.Visibility = Visibility.Collapsed;
                scroll.MaxHeight = Math.Max(80, ActualHeight - 110
                    - (previewPanel.Visibility == Visibility.Visible ? 90 : 0));
            }
        };
        _timers.Add(undoTtlTimer);

        void BumpUndoTtl(bool showUndo)
        {
            undoTtlTimer.Stop();
            if (showUndo && !fileOpRunning && undoStack.Count > 0) undoTtlTimer.Start();
        }

        void SetFileOpUi(bool running, string? text = null)
        {
            fileOpRunning = running;
            bool showUndo = !running && undoStack.Count > 0;
            undoBar.Visibility = (running || undoStack.Count > 0) ? Visibility.Visible : Visibility.Collapsed;
            if (running)
            {
                undoTtlTimer.Stop();
                undoBtn.Content = "Отмена";
                undoBtn.ToolTip = "Отменить текущую операцию";
                undoBtn.IsEnabled = true;
                undoText.Text = text ?? "Выполняется…";
            }
            else
            {
                undoBtn.Content = "Отменить";
                undoBtn.ToolTip = "Отменить последнее действие (Ctrl+Z)";
                if (undoStack.Count == 0)
                {
                    undoTtlTimer.Stop();
                    undoBar.Visibility = Visibility.Collapsed;
                    undoText.Text = string.Empty;
                    undoBtn.IsEnabled = false;
                }
                else
                {
                    undoBtn.IsEnabled = true;
                    if (text is not null) undoText.Text = text;
                    else if (string.IsNullOrWhiteSpace(undoText.Text)) undoText.Text = $"Можно отменить: {undoStack.Count}";
                }
            }
            BumpUndoTtl(showUndo);
            scroll.MaxHeight = Math.Max(80, ActualHeight - 110
                - (previewPanel.Visibility == Visibility.Visible ? 90 : 0)
                - (undoBar.Visibility == Visibility.Visible ? 28 : 0));
        }

        void PushUndoBatch(IEnumerable<(string kind, string from, string to, bool isDir)> actions, string message)
        {
            if (fileOpRunning) return;
            var list = actions?.ToList() ?? new List<(string kind, string from, string to, bool isDir)>();
            if (list.Count == 0) return;
            undoStack.Push((message, list));
            SetFileOpUi(false, message);
        }

        void PushUndo(string kind, string from, string to, bool isDir, string message)
            => PushUndoBatch(new[] { (kind, from, to, isDir) }, message);
        Button?[] btnRefs = new Button?[2];
        (int restored, int notFound, int ambiguous) TryRestoreFromRecycleBin(IReadOnlyCollection<string> originalPaths)
        {
            var unique = (originalPaths ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => System.IO.Path.GetFullPath(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unique.Count == 0) return (0, 0, 0);
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                if (t is null) return (0, unique.Count, 0);
                dynamic? shell = Activator.CreateInstance(t);
                if (shell is null) return (0, unique.Count, 0);
                dynamic? bin = shell.NameSpace(10);
                if (bin is null) return (0, unique.Count, 0);

                var wanted = new HashSet<string>(unique, StringComparer.OrdinalIgnoreCase);
                var candidates = new Dictionary<string, List<dynamic>>(StringComparer.OrdinalIgnoreCase);

                var items = bin.Items();
                int count = 0;
                try { count = (int)items.Count; } catch { count = 0; }

                for (int idx = 0; idx < count; idx++)
                {
                    dynamic? item = null;
                    try { item = items.Item(idx); } catch { item = null; }
                    if (item is null) continue;

                    string itemName = string.Empty;
                    try { itemName = (string)(item.Name ?? string.Empty); } catch { }

                    string origLoc = string.Empty;
                    try { origLoc = (string)(bin.GetDetailsOf(item, 1) ?? string.Empty); } catch { }

                    string full = string.Empty;
                    if (!string.IsNullOrWhiteSpace(origLoc) && !string.IsNullOrWhiteSpace(itemName))
                    {
                        try { full = System.IO.Path.GetFullPath(System.IO.Path.Combine(origLoc, itemName)); } catch { full = string.Empty; }
                    }

                    if (!string.IsNullOrWhiteSpace(full) && wanted.Contains(full))
                    {
                        if (!candidates.TryGetValue(full, out var list)) { list = new List<dynamic>(); candidates[full] = list; }
                        list.Add(item);
                        continue;
                    }

                    var nameMatches = wanted.Where(p => string.Equals(System.IO.Path.GetFileName(p), itemName, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (nameMatches.Count == 1)
                    {
                        var match = nameMatches[0];
                        if (!candidates.TryGetValue(match, out var list)) { list = new List<dynamic>(); candidates[match] = list; }
                        list.Add(item);
                    }
                }

                int restored = 0, notFound = 0, ambiguous = 0;
                foreach (var path in unique)
                {
                    if (!candidates.TryGetValue(path, out var list) || list.Count == 0)
                    {
                        notFound++;
                        continue;
                    }
                    if (list.Count > 1)
                    {
                        ambiguous++;
                        continue;
                    }

                    dynamic item = list[0];
                    bool done = false;
                    try
                    {
                        dynamic verbs = item.Verbs();
                        int vcount = (int)verbs.Count;
                        for (int v = 0; v < vcount && !done; v++)
                        {
                            dynamic verb = verbs.Item(v);
                            string vname = string.Empty;
                            try { vname = ((string)(verb.Name ?? string.Empty)).Replace("&", string.Empty).Trim(); } catch { }
                            if (string.IsNullOrWhiteSpace(vname)) continue;
                            if (vname.IndexOf("Восстанов", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                vname.IndexOf("Restore", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                vname.IndexOf("Undelete", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                try { verb.DoIt(); done = true; } catch { done = false; }
                            }
                        }
                    }
                    catch { done = false; }

                    if (done) restored++; else notFound++;
                }

                return (restored, notFound, ambiguous);
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: recycle restore failed — {ex.Message}");
                return (0, unique.Count, 0);
            }
        }
        bool TryUndo()
        {
            if (fileOpRunning)
            {
                try { fileOpCts?.Cancel(); } catch { }
                return true;
            }
            while (undoStack.Count > 0)
            {
                var batch = undoStack.Pop();
                try
                {
                    if (batch.actions.Count == 1)
                    {
                        var a = batch.actions[0];
                        if (a.kind == "move" || a.kind == "rename")
                        {
                            if (string.Equals(System.IO.Path.GetFullPath(a.from), System.IO.Path.GetFullPath(a.to), StringComparison.OrdinalIgnoreCase))
                                continue;
                            if ((a.isDir && !Directory.Exists(a.to)) || (!a.isDir && !File.Exists(a.to)))
                                continue;
                            if (Directory.Exists(a.from) || File.Exists(a.from))
                                continue;
                            string? parent = System.IO.Path.GetDirectoryName(a.from);
                            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                            if (a.isDir) Directory.Move(a.to, a.from); else File.Move(a.to, a.from);
                            SetFileOpUi(false, "Действие отменено");
                            Rebuild();
                            return true;
                        }
                        if (a.kind == "mkdir")
                        {
                            if (Directory.Exists(a.from) && !Directory.EnumerateFileSystemEntries(a.from).Any())
                            {
                                Directory.Delete(a.from);
                                SetFileOpUi(false, "Папка удалена (отмена)");
                                Rebuild();
                                return true;
                            }
                        }
                        if (a.kind == "recycle")
                        {
                            var r = TryRestoreFromRecycleBin(new[] { a.from });
                            string msg = r.restored > 0
                                ? (r.ambiguous > 0 || r.notFound > 0 ? $"Восстановлено: {r.restored} (не найдено/неоднозначно: {r.notFound + r.ambiguous})" : "Восстановлено из корзины")
                                : (r.ambiguous > 0 ? "Не удалось: неоднозначно в корзине" : "Не удалось восстановить (не найдено в корзине)");
                            SetFileOpUi(false, msg);
                            Rebuild();
                            return r.restored > 0;
                        }
                    }
                    else
                    {
                        // Пакет: отменяем операции в обратном порядке.
                        bool allOk = true;
                        int undone = 0;
                        int total = batch.actions.Count;

                        // Сначала восстанавливаем удаления батчем (лучше идентификация в корзине).
                        var recyclePaths = batch.actions.Where(a => a.kind == "recycle").Select(a => a.from).ToList();
                        if (recyclePaths.Count > 0)
                        {
                            var rr = TryRestoreFromRecycleBin(recyclePaths);
                            undone += rr.restored;
                            if (rr.notFound + rr.ambiguous > 0) allOk = false;
                        }

                        for (int i = batch.actions.Count - 1; i >= 0; i--)
                        {
                            var a = batch.actions[i];
                            try
                            {
                                if (a.kind == "move" || a.kind == "rename")
                                {
                                    if ((a.isDir && !Directory.Exists(a.to)) || (!a.isDir && !File.Exists(a.to))) { allOk = false; continue; }
                                    if (Directory.Exists(a.from) || File.Exists(a.from)) { allOk = false; continue; }
                                    string? parent = System.IO.Path.GetDirectoryName(a.from);
                                    if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
                                    if (a.isDir) Directory.Move(a.to, a.from); else File.Move(a.to, a.from);
                                    undone++;
                                }
                                else if (a.kind == "mkdir")
                                {
                                    if (Directory.Exists(a.from) && !Directory.EnumerateFileSystemEntries(a.from).Any())
                                    {
                                        Directory.Delete(a.from);
                                        undone++;
                                    }
                                    else allOk = false;
                                }
                                // recycle обработан батчем выше
                            }
                            catch { allOk = false; }
                        }
                        if (undone > 0)
                        {
                            SetFileOpUi(false, allOk ? $"Отменено: {undone}/{total}" : $"Отменено частично: {undone}/{total}");
                            Rebuild();
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info($"folder widget: undo failed — {ex.Message}");
                    SetFileOpUi(false, "Не удалось отменить");
                    return false;
                }
            }
            SetFileOpUi(false);
            return false;
        }
        undoBtn.Click += (_, _) => TryUndo();

        void ClearPreview()
        {
            previewPanel.Visibility = Visibility.Collapsed;
            previewImage.Source = null;
            previewImage.Visibility = Visibility.Collapsed;
            previewText.Visibility = Visibility.Collapsed;
            previewName.Text = string.Empty;
            previewMeta.Text = string.Empty;
            scroll.MaxHeight = Math.Max(80, ActualHeight - 140 - (undoBar.Visibility == Visibility.Visible ? 28 : 0));
        }

        string FmtSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} Б";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} КБ";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):0.#} МБ";
            return $"{bytes / (1024.0 * 1024 * 1024):0.##} ГБ";
        }

        void ShowPreview(string path, bool isDir)
        {
            try
            {
                lastClickedPath = path;
                previewPanel.Visibility = Visibility.Visible;
                scroll.MaxHeight = Math.Max(80, ActualHeight - 230 - (undoBar.Visibility == Visibility.Visible ? 28 : 0));
                previewImage.Source = null;
                previewImage.Visibility = Visibility.Collapsed;
                previewText.Text = string.Empty;
                previewText.Visibility = Visibility.Collapsed;

                previewName.Text = System.IO.Path.GetFileName(path);
                if (isDir)
                {
                    var di = new DirectoryInfo(path);
                    int subDirs = 0, subFiles = 0;
                    try { subDirs = di.EnumerateDirectories().Take(500).Count(); subFiles = di.EnumerateFiles().Take(1000).Count(); } catch { }
                    previewMeta.Text = $"Папка · {subDirs} папок, {subFiles} файлов · изменено {di.LastWriteTime:g}";
                    return;
                }

                var fi = new FileInfo(path);
                previewMeta.Text = $"{fi.Extension.ToLowerInvariant()} · {FmtSize(fi.Exists ? fi.Length : 0)} · изменено {fi.LastWriteTime:g}";

                string ext = fi.Extension.ToLowerInvariant();
                if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp")
                {
                    try
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(path);
                        bmp.DecodePixelWidth = 320;
                        bmp.EndInit();
                        bmp.Freeze();
                        previewImage.Source = bmp;
                        previewImage.Visibility = Visibility.Visible;
                    }
                    catch { }
                }
                else if (ext is ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".ini" or ".cfg" or ".cs" or ".js" or ".ts" or ".py" or ".ps1" or ".yaml" or ".yml")
                {
                    try
                    {
                        if (fi.Length <= 256 * 1024)
                        {
                            using var sr = new StreamReader(path);
                            char[] buf = new char[600];
                            int n = sr.Read(buf, 0, buf.Length);
                            if (n > 0)
                            {
                                previewText.Text = new string(buf, 0, n).Replace("\r", "");
                                previewText.Visibility = Visibility.Visible;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: preview failed {path} — {ex.Message}");
                previewPanel.Visibility = Visibility.Collapsed;
            }
        }

        bool AtRoot() => string.Equals(System.IO.Path.GetFullPath(currentPath).TrimEnd('\\', '/'),
            System.IO.Path.GetFullPath(rootPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        string SortLabel() => sortMode switch { 1 => "дата", 2 => "размер", 3 => "тип", _ => "имя" };
        string GroupLabel() => groupMode switch { 1 => "тип", 2 => "дата", _ => "нет" };



        void RefreshHeader(Button?[] refs, int shown = -1, int total = -1)
        {
            if (missing || !Directory.Exists(currentPath))
            {
                missing = true;
                title.Text = "ПАПКА НЕ НАЙДЕНА";
                subtitle.Text = currentPath;
                if (refs[0] is not null) refs[0]!.IsEnabled = false;
                return;
            }

            string name = System.IO.Path.GetFileName(currentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = currentPath;
            title.Text = name.ToUpperInvariant();

            string where = AtRoot() ? rootPath : currentPath;
            string modeInfo = groupMode == 0 ? SortLabel() : $"{SortLabel()} · группы: {GroupLabel()}";
            subtitle.Text = (shown >= 0 && total >= 0 && shown != total)
                ? $"{shown} из {total} · {modeInfo} · {where}"
                : shown >= 0 ? $"{shown} объект(ов) · {modeInfo} · {where}" : where;
            subtitle.ToolTip = where;

            if (refs[0] is not null) refs[0]!.IsEnabled = !AtRoot();
            if (refs[1] is not null) refs[1]!.Content = "⇅";
        }

        ImageSource? IconForPath(string path, bool isDir)
        {
            try
            {
                if (isDir)
                {
                    try
                    {
                        using var dirIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                        if (dirIcon is not null)
                        {
                            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                dirIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                    }
                    catch { }

                    try
                    {
                        using var sys = System.Drawing.Icon.ExtractAssociatedIcon(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                        if (sys is not null)
                        {
                            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                                sys.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            src.Freeze();
                            return src;
                        }
                    }
                    catch { }
                    return null;
                }

                string png = DotShell.Core.Launcher.IconCache.Extract(string.Empty, path);
                if (!string.IsNullOrWhiteSpace(png) && File.Exists(png))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(png);
                    image.DecodePixelWidth = 32;
                    image.EndInit();
                    image.Freeze();
                    return image;
                }

                using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
                if (ico is null) return null;
                var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            catch { return null; }
        }

        void CopyToClipboardText(string text)
        {
            try { Clipboard.SetDataObject(text, true); }
            catch (Exception ex) { Log.Info($"folder widget: clipboard failed — {ex.Message}"); }
        }


        void NavigateTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            // Не выходим выше корневой папки виджета.
            var rootFull = System.IO.Path.GetFullPath(rootPath).TrimEnd('\\', '/');
            var full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
            currentPath = full;
            // При смене папки сбрасываем снимок, чтобы не помечать всё как «изменилось».
            snapshotPath = string.Empty;
            haveSnapshot = false;
            changedPaths.Clear();
            collapsedGroups.Clear();
            Rebuild();
            UpdateFolderWatcher();
        }

        UIElement Row(string path, bool isDir, string? extraTip = null)
        {
            var icon = new Image
            {
                Width = 18,
                Height = 18,
                Margin = new Thickness(0, 0, 8, 0),
                Source = IconForPath(path, isDir),
                Opacity = 0.95,
            };

            var name = new TextBlock
            {
                Text = System.IO.Path.GetFileName(path),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                Foreground = ResBrush("DsText"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = ResBrush("DsAccent"),
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = changedPaths.Contains(path) ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = "Новое или изменённое",
            };

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(icon, Dock.Left);
            DockPanel.SetDock(dot, Dock.Right);
            row.Children.Add(icon);
            row.Children.Add(dot);
            row.Children.Add(name);

            var border = new Border
            {
                Child = row,
                Padding = new Thickness(6, 4, 6, 4),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrWhiteSpace(extraTip) ? path : (path + "\n" + extraTip),
                Tag = path,
            };

            visiblePaths.Add(path);
            rowByPath[path] = border;
            dotByPath[path] = dot;
            ApplyRowVisual(border, path);

            border.MouseEnter += (_, _) =>
            {
                if (!selected.Contains(path)) border.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            };
            border.MouseLeave += (_, _) =>
            {
                if (!selected.Contains(path)) border.Background = Brushes.Transparent;
            };
            border.MouseLeftButtonUp += (_, e) =>
            {
                bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
                bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

                // Один клик — выбор/предпросмотр; двойной — открыть/перейти (без модификаторов).
                if (e.ClickCount >= 2 && !ctrl && !shift)
                {
                    if (isDir) NavigateTo(path);
                    else
                    {
                        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                        catch (Exception ex) { Log.Info($"folder widget: open failed {path} — {ex.Message}"); }
                    }
                    e.Handled = true;
                    return;
                }

                if (shift && lastClickedPath is not null)
                {
                    int a = visiblePaths.IndexOf(lastClickedPath);
                    int b = visiblePaths.IndexOf(path);
                    if (a >= 0 && b >= 0)
                    {
                        int from = Math.Min(a, b);
                        int to = Math.Max(a, b);
                        if (!ctrl) selected.Clear();
                        for (int i = from; i <= to; i++) selected.Add(visiblePaths[i]);
                    }
                    else
                    {
                        if (!ctrl) selected.Clear();
                        selected.Add(path);
                    }
                }
                else if (ctrl)
                {
                    if (!selected.Add(path)) selected.Remove(path);
                }
                else
                {
                    selected.Clear();
                    selected.Add(path);
                }

                MarkSeen(path);
                lastClickedPath = path;
                RefreshSelectionVisuals();
                ShowPreview(path, isDir);
                scroll.Focus();
                e.Handled = true;
            };

            List<string> TargetPaths() => selected.Contains(path) && selected.Count > 1 ? selected.ToList() : new List<string> { path };

            var menu = new ContextMenu();
            var open = new MenuItem { Header = isDir ? "Открыть внутри виджета" : "Открыть" };
            open.Click += (_, _) =>
            {
                if (isDir) NavigateTo(path);
                else
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Info($"folder widget: open failed {path} — {ex.Message}"); }
                }
            };
            menu.Items.Add(open);

            if (isDir)
            {
                var openExt = new MenuItem { Header = "Открыть в проводнике" };
                openExt.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Info($"folder widget: open dir failed {path} — {ex.Message}"); }
                };
                menu.Items.Add(openExt);
            }

            var reveal = new MenuItem { Header = "Показать в проводнике" };
            reveal.Click += (_, _) =>
            {
                try
                {
                    if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
                    else System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(System.IO.Path.GetDirectoryName(path) ?? currentPath) { UseShellExecute = true });
                }
                catch (Exception ex) { Log.Info($"folder widget: reveal failed {path} — {ex.Message}"); }
            };
            menu.Items.Add(reveal);

            var copy = new MenuItem { Header = "Копировать путь" };
            copy.Click += (_, _) => CopyToClipboardText(string.Join(Environment.NewLine, TargetPaths()));
            menu.Items.Add(copy);

            var copyFile = new MenuItem { Header = selected.Count > 1 && selected.Contains(path) ? "Копировать файлы" : "Копировать файл" };
            copyFile.Click += (_, _) =>
            {
                try
                {
                    var targets = TargetPaths();
                    var files = new StringCollection();
                    foreach (var p in targets) files.Add(p);
                    Clipboard.SetFileDropList(files);
                    cutPaths.Clear();
                    cutPending = false;
                    RefreshSelectionVisuals();
                }
                catch (Exception ex) { Log.Info($"folder widget: copy file failed — {ex.Message}"); }
            };
            menu.Items.Add(copyFile);

            var cutFile = new MenuItem { Header = selected.Count > 1 && selected.Contains(path) ? "Вырезать файлы" : "Вырезать" };
            cutFile.Click += (_, _) =>
            {
                try
                {
                    var targets = TargetPaths();
                    var files = new StringCollection();
                    cutPaths.Clear();
                    foreach (var p in targets)
                    {
                        files.Add(p);
                        cutPaths.Add(p);
                    }
                    Clipboard.SetFileDropList(files);
                    cutPending = true;
                    RefreshSelectionVisuals();
                }
                catch (Exception ex) { Log.Info($"folder widget: cut failed — {ex.Message}"); }
            };
            menu.Items.Add(cutFile);

            menu.Items.Add(new Separator());

            var rename = new MenuItem { Header = "Переименовать", IsEnabled = !(selected.Count > 1 && selected.Contains(path)) };
            rename.Click += (_, _) =>
            {
                string oldName = System.IO.Path.GetFileName(path);
                var input = PromptText("Переименовать", $"Новое имя для «{oldName}»:", oldName);
                if (string.IsNullOrWhiteSpace(input)) return;
                input = input.Trim();
                if (string.Equals(input, oldName, StringComparison.Ordinal)) return;
                try
                {
                    string dest = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? currentPath, input);
                    bool isDirItem = Directory.Exists(path);
                    if (isDirItem) Directory.Move(path, dest);
                    else if (File.Exists(path)) File.Move(path, dest);
                    else return;
                    PushUndo("rename", path, dest, isDirItem, "Переименовано — можно отменить");
                }
                catch (Exception ex) { Log.Info($"folder widget: rename failed {path} — {ex.Message}"); }
                finally { Rebuild(); }
            };
            menu.Items.Add(rename);

            var delete = new MenuItem { Header = selected.Count > 1 && selected.Contains(path) ? "Удалить выбранные в корзину" : "Удалить в корзину" };
            delete.Click += (_, _) =>
            {
                try
                {
                    bool pinsChanged = false;
                    var recycled = new List<string>();
                    foreach (var p in TargetPaths().ToList())
                    {
                        if (RecyclePath(p))
                        {
                            recycled.Add(p);
                            if (pinned.Remove(p)) pinsChanged = true;
                        }
                    }
                    if (pinsChanged) SavePins();
                    if (recycled.Count > 0)
                        PushUndoBatch(recycled.Select(p => ("recycle", p, string.Empty, Directory.Exists(p) || p.EndsWith("\\"))),
                            recycled.Count == 1 ? "Удалено в корзину — можно отменить" : $"Удалено в корзину ({recycled.Count}) — можно отменить");
                }
                catch (Exception ex) { Log.Info($"folder widget: delete failed — {ex.Message}"); }
                finally { Rebuild(); }
            };
            menu.Items.Add(delete);

            var pin = new MenuItem { Header = pinned.Contains(path) ? "Открепить" : "Закрепить" };
            pin.Click += (_, _) =>
            {
                if (!pinned.Add(path)) pinned.Remove(path);
                SavePins();
                Rebuild();
            };
            menu.Items.Add(pin);

            menu.Items.Add(new Separator());
            menu.Items.Add(RemoveWidgetItem());
            border.ContextMenu = menu;

            return border;
        }

        void ApplyRowVisual(Border b, string path)
        {
            bool isSelected = selected.Contains(path);
            bool isCut = cutPaths.Contains(path);
            b.Opacity = isCut ? 0.5 : 1.0;
            b.BorderBrush = isSelected ? ResBrush("DsAccent") : Brushes.Transparent;
            b.BorderThickness = isSelected ? new Thickness(1) : new Thickness(0);
            b.Background = isSelected ? new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)) : Brushes.Transparent;
        }

        void RefreshSelectionVisuals()
        {
            foreach (var kv in rowByPath)
                ApplyRowVisual(kv.Value, kv.Key);
        }

        void Rebuild()
        {
            try
            {
                rowByPath.Clear();
                dotByPath.Clear();
                visiblePaths.Clear();
                list.Children.Clear();
                if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
                {
                    // Если текущая подпапка исчезла — пробуем вернуться к корню.
                    if (!string.Equals(currentPath, rootPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(rootPath))
                    {
                        currentPath = rootPath;
                        UpdateFolderWatcher();
                    }
                    else
                    {
                        missing = true;
                        RefreshHeader(btnRefs);
                        list.Children.Add(new TextBlock
                        {
                            Text = "Папка недоступна. Можно убрать виджет или открыть корень заново.",
                            FontFamily = new FontFamily("Segoe UI"),
                            FontSize = 11,
                            Foreground = ResBrush("DsTextMuted"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(4, 8, 4, 4),
                        });
                        return;
                    }
                }

                missing = false;

                if (CollectionActive() && collection is not null)
                {
                    string scope = !string.IsNullOrWhiteSpace(collection.Scope) ? collection.Scope : rootPath;
                    if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope)) scope = rootPath;
                    if (!Directory.Exists(scope))
                    {
                        list.Children.Add(new TextBlock { Text = "Источник подборки недоступен.", FontFamily = new FontFamily("Segoe UI"), FontSize = 11, Foreground = ResBrush("DsTextMuted"), Margin = new Thickness(4, 8, 4, 4) });
                        subtitle.Text = "Подборка: источник недоступен";
                        return;
                    }

                    bool KindMatch(string ext)
                    {
                        if (collection.Kinds is null || collection.Kinds.Count == 0) return true;
                        ext = (ext ?? string.Empty).ToLowerInvariant();
                        foreach (var k in collection.Kinds)
                        {
                            var kk = (k ?? string.Empty).Trim().ToLowerInvariant();
                            if (string.IsNullOrEmpty(kk)) continue;
                            if (kk.StartsWith(".")) { if (ext == kk) return true; continue; }
                            bool isImage = ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".svg" or ".ico" or ".heic";
                            bool isDoc = ext is ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf" or ".csv";
                            bool isPdf = ext == ".pdf";
                            bool isArchive = ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz";
                            bool isVideo = ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm";
                            bool isAudio = ext is ".mp3" or ".wav" or ".flac" or ".m4a" or ".ogg" or ".aac";
                            if (kk == "image" && isImage) return true;
                            if (kk == "doc" && isDoc) return true;
                            if (kk == "pdf" && isPdf) return true;
                            if (kk == "archive" && isArchive) return true;
                            if (kk == "video" && isVideo) return true;
                            if (kk == "audio" && isAudio) return true;
                        }
                        return false;
                    }

                    var matched = new List<FileInfo>();
                    try
                    {
                        var opt = new EnumerationOptions { RecurseSubdirectories = collection.Recursive, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                        foreach (var fpath in Directory.EnumerateFiles(scope, "*", opt))
                        {
                            FileInfo fi;
                            try { fi = new FileInfo(fpath); } catch { continue; }
                            if (!string.IsNullOrWhiteSpace(collection.NameContains) && fi.Name.IndexOf(collection.NameContains, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                            if (!KindMatch(fi.Extension)) continue;
                            if (collection.WithinDays > 0 && fi.LastWriteTime < DateTime.Now.AddDays(-collection.WithinDays)) continue;
                            if (collection.MinSizeMb > 0 && fi.Length < (long)collection.MinSizeMb * 1024L * 1024L) continue;
                            matched.Add(fi);
                            if (matched.Count > 2000) break; // safety cap
                        }
                    }
                    catch (Exception ex) { Log.Info($"folder widget: collection enum failed — {ex.Message}"); }

                    matched = matched.OrderByDescending(f => f.LastWriteTimeUtc).Take(Math.Max(1, collection.Limit)).ToList();
                    visiblePaths.Clear();
                    selected.RemoveWhere(p => !File.Exists(p));

                    string chips = collection.Preset;
                    var parts = new List<string>();
                    if (!string.IsNullOrWhiteSpace(collection.Preset)) parts.Add(collection.Preset);
                    if (collection.Recursive) parts.Add("с подпапками");
                    if (collection.WithinDays > 0) parts.Add($"{collection.WithinDays} дн.");
                    if (collection.Kinds.Count > 0) parts.Add(string.Join(",", collection.Kinds.Take(3)));
                    if (collection.MinSizeMb > 0) parts.Add($">{collection.MinSizeMb} МБ");
                    if (!string.IsNullOrWhiteSpace(collection.NameContains)) parts.Add($"«{collection.NameContains}»");
                    chips = parts.Count > 0 ? string.Join(" · ", parts) : "Подборка";
                    title.Text = "ПОДБОРКА";
                    subtitle.Text = $"{chips} — {matched.Count} объект(ов) · {scope}";
                    subtitle.ToolTip = scope;

                    foreach (var fi in matched)
                    {
                        var reasons = new List<string>();
                        reasons.Add($"Изменён: {fi.LastWriteTime:g}");
                        reasons.Add($"Размер: {fi.Length / 1024.0 / 1024.0:0.#} МБ");
                        if (!string.IsNullOrWhiteSpace(collection.NameContains) && fi.Name.IndexOf(collection.NameContains, StringComparison.CurrentCultureIgnoreCase) >= 0)
                            reasons.Add($"Имя содержит: «{collection.NameContains}»");
                        if (collection.WithinDays > 0) reasons.Add($"За {collection.WithinDays} дн.");
                        if (collection.MinSizeMb > 0) reasons.Add($">{collection.MinSizeMb} МБ");
                        if (collection.Kinds.Count > 0) reasons.Add($"Тип: {string.Join(",", collection.Kinds)}");
                        list.Children.Add(Row(fi.FullName, false, "Совпало: " + string.Join(" · ", reasons)));
                    }

                    if (matched.Count == 0)
                    {
                        list.Children.Add(new TextBlock
                        {
                            Text = "Ничего не найдено по текущим правилам. Попробуйте ослабить фильтры или выбрать другой пресет.",
                            FontFamily = new FontFamily("Segoe UI"),
                            FontSize = 11,
                            Foreground = ResBrush("DsTextMuted"),
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(4, 8, 4, 4),
                        });
                    }
                    RefreshSelectionVisuals();
                    return;
                }

                var dirInfos = new DirectoryInfo(currentPath).EnumerateDirectories().ToList();
                var fileInfos = new DirectoryInfo(currentPath).EnumerateFiles().ToList();

                // Индикатор «новое/изменилось»: сравниваем снимок содержимого текущей папки с предыдущим (в сессии).
                var currentSig = new Dictionary<string, (long size, long ticks)>(StringComparer.OrdinalIgnoreCase);
                foreach (var d in dirInfos)
                    currentSig[d.FullName] = (-1, d.LastWriteTimeUtc.Ticks);
                foreach (var f in fileInfos)
                    currentSig[f.FullName] = (f.Length, f.LastWriteTimeUtc.Ticks);

                bool sameFolder = string.Equals(snapshotPath, currentPath, StringComparison.OrdinalIgnoreCase);
                if (sameFolder && haveSnapshot)
                {
                    foreach (var kv in currentSig)
                    {
                        if (!prevSig.TryGetValue(kv.Key, out var old) || old != kv.Value)
                            changedPaths.Add(kv.Key);
                    }
                    // Удалённые больше не помечаем.
                    changedPaths.RemoveWhere(p => !currentSig.ContainsKey(p));
                }
                else
                {
                    // Смена папки/первый снимок — не помечаем всё как изменённое.
                    changedPaths.Clear();
                }
                prevSig = currentSig;
                snapshotPath = currentPath;
                haveSnapshot = true;

                bool Match(string name) => string.IsNullOrWhiteSpace(filter) || name.Contains(filter, StringComparison.CurrentCultureIgnoreCase);

                IEnumerable<DirectoryInfo> SortDirs(IEnumerable<DirectoryInfo> src) => sortMode switch
                {
                    1 => src.OrderByDescending(d => d.LastWriteTimeUtc).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                    2 => src.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                    3 => src.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                    _ => src.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase),
                };

                IEnumerable<FileInfo> SortFiles(IEnumerable<FileInfo> src) => sortMode switch
                {
                    1 => src.OrderByDescending(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
                    2 => src.OrderByDescending(f => f.Length).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
                    3 => src.OrderBy(f => f.Extension, StringComparer.CurrentCultureIgnoreCase).ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
                    _ => src.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase),
                };

                var dirsShown = SortDirs(dirInfos.Where(d => Match(d.Name))).Take(160).ToList();
                var filesShown = SortFiles(fileInfos.Where(f => Match(f.Name))).Take(240).ToList();
                int total = dirInfos.Count + fileInfos.Count;
                int shown = dirsShown.Count + filesShown.Count;

                // Чистим только реально удалённые закрепления (не скрытые навигацией/фильтром).
                int pinnedBefore = pinned.Count;
                pinned.RemoveWhere(p => !File.Exists(p) && !Directory.Exists(p));
                if (pinned.Count != pinnedBefore) SavePins();

                var pinnedDirs = dirsShown.Where(d => pinned.Contains(d.FullName)).ToList();
                var pinnedFiles = filesShown.Where(f => pinned.Contains(f.FullName)).ToList();
                var restDirs = dirsShown.Where(d => !pinned.Contains(d.FullName)).ToList();
                var restFiles = filesShown.Where(f => !pinned.Contains(f.FullName)).ToList();

                RefreshHeader(btnRefs, shown, total);

                string TypeGroup(FileInfo f)
                {
                    string ext = f.Extension.ToLowerInvariant();
                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".svg" or ".ico") return "Изображения";
                    if (ext is ".mp4" or ".mkv" or ".avi" or ".mov" or ".mp3" or ".wav" or ".flac" or ".m4a") return "Медиа";
                    if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".md" or ".rtf") return "Документы";
                    if (ext is ".zip" or ".rar" or ".7z" or ".tar" or ".gz") return "Архивы";
                    return "Прочее";
                }

                string DateGroup(DateTime dtLocal)
                {
                    var today = DateTime.Today;
                    if (dtLocal.Date == today) return "Сегодня";
                    if (dtLocal.Date == today.AddDays(-1)) return "Вчера";
                    if (dtLocal.Date >= today.AddDays(-7)) return "За неделю";
                    return "Ранее";
                }

                UIElement GroupHeader(string text, int count, string key)
                {
                    bool collapsed = collapsedGroups.Contains(key);
                    var tb = new TextBlock
                    {
                        Text = $"{(collapsed ? "▸" : "▾")} {text} · {count}",
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = ResBrush("DsTextMuted"),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    var border = new Border
                    {
                        Child = tb,
                        Padding = new Thickness(4, 6, 4, 2),
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        ToolTip = collapsed ? "Развернуть" : "Свернуть",
                    };
                    border.MouseLeftButtonUp += (_, e) =>
                    {
                        if (!collapsedGroups.Add(key)) collapsedGroups.Remove(key);
                        Rebuild();
                        e.Handled = true;
                    };
                    return border;
                }

                void RenderGroup(string text, string key, IEnumerable<string> paths, bool isDir)
                {
                    var listPaths = paths.ToList();
                    list.Children.Add(GroupHeader(text, listPaths.Count, key));
                    if (collapsedGroups.Contains(key)) return;
                    foreach (var p in listPaths) list.Children.Add(Row(p, isDir));
                }

                bool hasPinned = pinnedDirs.Count + pinnedFiles.Count > 0;
                if (hasPinned)
                {
                    string pinKey = "p:Закреплённые";
                    list.Children.Add(GroupHeader("★ Закреплённые", pinnedDirs.Count + pinnedFiles.Count, pinKey));
                    if (!collapsedGroups.Contains(pinKey))
                    {
                        foreach (var d in pinnedDirs) list.Children.Add(Row(d.FullName, true));
                        foreach (var f in pinnedFiles) list.Children.Add(Row(f.FullName, false));
                    }
                }

                if (groupMode == 1)
                {
                    if (restDirs.Count > 0)
                        RenderGroup("Папки", "1:Папки", restDirs.Select(d => d.FullName), true);
                    foreach (var g in restFiles.GroupBy(TypeGroup).OrderBy(g => g.Key, StringComparer.CurrentCulture))
                    {
                        var items = g.ToList();
                        RenderGroup(g.Key, $"1:{g.Key}", items.Select(f => f.FullName), false);
                    }
                }
                else if (groupMode == 2)
                {
                    if (restDirs.Count > 0)
                        RenderGroup("Папки", "2:Папки", restDirs.Select(d => d.FullName), true);
                    var dateGroups = restFiles
                        .GroupBy(f => DateGroup(f.LastWriteTime))
                        .OrderBy(g => g.Key switch { "Сегодня" => 0, "Вчера" => 1, "За неделю" => 2, _ => 3 });
                    foreach (var g in dateGroups)
                    {
                        var items = g.ToList();
                        RenderGroup(g.Key, $"2:{g.Key}", items.Select(f => f.FullName), false);
                    }
                }
                else
                {
                    foreach (var d in restDirs) list.Children.Add(Row(d.FullName, true));
                    foreach (var f in restFiles) list.Children.Add(Row(f.FullName, false));
                }

                // Чистим исчезнувшие из выделения и предпросмотр.
                selected.RemoveWhere(p => !Directory.Exists(p) && !File.Exists(p));
                RefreshSelectionVisuals();
                if (!string.IsNullOrWhiteSpace(lastClickedPath) &&
                    !Directory.Exists(lastClickedPath) && !File.Exists(lastClickedPath))
                {
                    lastClickedPath = null;
                    ClearPreview();
                }

                if (shown == 0)
                {
                    list.Children.Add(new TextBlock
                    {
                        Text = total == 0
                            ? "Папка пуста. Перетащите сюда файлы из проводника."
                            : "Ничего не найдено по фильтру.",
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        Foreground = ResBrush("DsTextMuted"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(4, 8, 4, 4),
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: rebuild failed — {ex.Message}");
                RefreshHeader(btnRefs);
                list.Children.Add(new TextBlock
                {
                    Text = "Не удалось прочитать папку.",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Foreground = ResBrush("DsTextMuted"),
                    Margin = new Thickness(4, 8, 4, 4),
                });
            }
        }

        RefreshHeader(btnRefs);
        UpdateFolderWatcher();
        Rebuild();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titles = new StackPanel();
        titles.Children.Add(title);
        titles.Children.Add(subtitle);
        header.Children.Add(titles);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var upBtn = WBtn("↑", "На уровень вверх (в пределах папки виджета)");
        upBtn.Padding = new Thickness(6, 2, 6, 2);
        upBtn.Margin = new Thickness(0, 0, 0, 0);
        upBtn.IsEnabled = false;
        upBtn.Click += (_, _) =>
        {
            if (AtRoot()) return;
            var parent = Directory.GetParent(currentPath);
            if (parent is not null) NavigateTo(parent.FullName);
        };
        btnRefs[0] = upBtn;

        var openBtn = WBtn("↗", "Открыть текущую папку в проводнике");
        openBtn.Padding = new Thickness(6, 2, 6, 2);
        openBtn.Margin = new Thickness(4, 0, 0, 0);
        openBtn.Click += (_, _) =>
        {
            try { if (Directory.Exists(currentPath)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(currentPath) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Info($"folder widget: open root failed — {ex.Message}"); }
        };

        var sortBtn = WBtn("⇅", "Сортировка: имя → дата → размер → тип");
        sortBtn.Padding = new Thickness(6, 2, 6, 2);
        sortBtn.Margin = new Thickness(4, 0, 0, 0);
        sortBtn.Click += (_, _) => { sortMode = (sortMode + 1) % 4; Rebuild(); };
        btnRefs[1] = sortBtn;

        var groupBtn = WBtn("≡", "Группировка: нет → тип → дата");
        groupBtn.Padding = new Thickness(6, 2, 6, 2);
        groupBtn.Margin = new Thickness(4, 0, 0, 0);
        groupBtn.Click += (_, _) => { groupMode = (groupMode + 1) % 3; collapsedGroups.Clear(); Rebuild(); };


        var collBtn = WBtn("⚡", "Живые подборки / правила / порталы");
        collBtn.Padding = new Thickness(6, 2, 6, 2);
        collBtn.Margin = new Thickness(4, 0, 0, 0);
        collBtn.Click += (_, _) =>
        {
            collection ??= new DotShell.Core.Widgets.FolderCollectionSpec();
            var cm = new ContextMenu();
            void SaveAndRefresh() { SaveCollection(); UpdateFolderWatcher(); Rebuild(); }

            MenuItem MI(string header, Action action)
            {
                var m = new MenuItem { Header = header };
                m.Click += (_, _) => { action(); SaveAndRefresh(); };
                return m;
            }

            void ApplyPreset(string name, Action<DotShell.Core.Widgets.FolderCollectionSpec> cfg)
            {
                collection ??= new DotShell.Core.Widgets.FolderCollectionSpec();
                collection.Enabled = true;
                collection.Preset = name;
                collection.Scope = string.IsNullOrWhiteSpace(collection.Scope) ? rootPath : collection.Scope;
                collection.Kinds.Clear();
                collection.NameContains = string.Empty;
                collection.WithinDays = 0;
                collection.MinSizeMb = 0;
                collection.Limit = 50;
                cfg(collection);
            }

            void EditRulesDialog()
            {
                collection ??= new DotShell.Core.Widgets.FolderCollectionSpec();
                var c = collection.Clone();

                var dialog = new Window
                {
                    Title = "Правила живой подборки",
                    Width = 520,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow,
                    Background = ResBrush("DsBg"),
                };

                var root = new StackPanel { Margin = new Thickness(14) };
                TextBlock L(string t) => new() { Text = t, Foreground = ResBrush("DsTextMuted"), FontFamily = new FontFamily("Segoe UI"), Margin = new Thickness(0, 8, 0, 3) };
                TextBox TB(string v) => new() { Text = v ?? string.Empty, FontFamily = new FontFamily("Segoe UI") };

                root.Children.Add(L("Название / пресет"));
                var nameBox = TB(c.Preset);
                root.Children.Add(nameBox);

                root.Children.Add(L("Область (папка, пусто = текущая папка виджета)"));
                var scopeBox = TB(string.IsNullOrWhiteSpace(c.Scope) ? rootPath : c.Scope);
                root.Children.Add(scopeBox);

                var recBox = new CheckBox { Content = "Искать во вложенных папках", IsChecked = c.Recursive, Margin = new Thickness(0, 8, 0, 0), Foreground = ResBrush("DsText") };
                root.Children.Add(recBox);

                root.Children.Add(L("Типы (можно выбрать несколько)"));
                var kindsPanel = new WrapPanel();
                (string key, string label)[] kindDefs = { ("image","Изображения"), ("doc","Документы"), ("pdf","PDF"), ("archive","Архивы"), ("video","Видео"), ("audio","Аудио") };
                var kindChecks = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
                var knownKinds = new HashSet<string>(kindDefs.Select(k => k.key), StringComparer.OrdinalIgnoreCase);
                foreach (var kd in kindDefs)
                {
                    var cb = new CheckBox
                    {
                        Content = kd.label,
                        Margin = new Thickness(0, 0, 12, 4),
                        Foreground = ResBrush("DsText"),
                        IsChecked = c.Kinds.Any(k => string.Equals(k, kd.key, StringComparison.OrdinalIgnoreCase)),
                    };
                    kindChecks[kd.key] = cb;
                    kindsPanel.Children.Add(cb);
                }
                root.Children.Add(kindsPanel);

                var customKinds = string.Join(", ", (c.Kinds ?? new List<string>()).Where(k => !knownKinds.Contains(k)));
                root.Children.Add(L("Доп. типы/расширения (через запятую, например: .psd, .fig)"));
                var customKindsBox = TB(customKinds);
                root.Children.Add(customKindsBox);

                root.Children.Add(L("Имя содержит"));
                var nameContainsBox = TB(c.NameContains);
                root.Children.Add(nameContainsBox);

                var grid = new Grid { Margin = new Thickness(0, 8, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var daysBox = TB(c.WithinDays > 0 ? c.WithinDays.ToString() : "");
                var sizeBox = TB(c.MinSizeMb > 0 ? c.MinSizeMb.ToString() : "");
                var limitBox = TB(c.Limit > 0 ? c.Limit.ToString() : "50");

                var daysPanel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
                daysPanel.Children.Add(L("За N дней (0/пусто = все)"));
                daysPanel.Children.Add(daysBox);
                Grid.SetColumn(daysPanel, 0); grid.Children.Add(daysPanel);

                var sizePanel = new StackPanel { Margin = new Thickness(6, 0, 6, 0) };
                sizePanel.Children.Add(L("Мин. размер, МБ"));
                sizePanel.Children.Add(sizeBox);
                Grid.SetColumn(sizePanel, 1); grid.Children.Add(sizePanel);

                var limitPanel = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
                limitPanel.Children.Add(L("Лимит результатов"));
                limitPanel.Children.Add(limitBox);
                Grid.SetColumn(limitPanel, 2); grid.Children.Add(limitPanel);
                root.Children.Add(grid);

                var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
                var ok = new Button { Content = "Сохранить", MinWidth = 110, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
                var cancel = new Button { Content = "Отмена", MinWidth = 100, IsCancel = true };
                ok.Click += (_, _) =>
                {
                    int ParseInt(string s, int def) => int.TryParse((s ?? "").Trim(), out var v) ? v : def;
                    c.Enabled = true;
                    c.Preset = (nameBox.Text ?? string.Empty).Trim();
                    c.Scope = (scopeBox.Text ?? string.Empty).Trim();
                    c.Recursive = recBox.IsChecked == true;
                    c.Kinds = kindChecks.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                    foreach (var part in (customKindsBox.Text ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var k = part.Trim();
                        if (k.Length == 0) continue;
                        if (!k.StartsWith(".") && !knownKinds.Contains(k.ToLowerInvariant())) k = "." + k.ToLowerInvariant();
                        if (!c.Kinds.Contains(k, StringComparer.OrdinalIgnoreCase)) c.Kinds.Add(k);
                    }
                    c.NameContains = (nameContainsBox.Text ?? string.Empty).Trim();
                    c.WithinDays = Math.Max(0, ParseInt(daysBox.Text, 0));
                    c.MinSizeMb = Math.Max(0, ParseInt(sizeBox.Text, 0));
                    c.Limit = Math.Clamp(ParseInt(limitBox.Text, 50), 1, 2000);
                    collection = c;
                    dialog.DialogResult = true;
                };
                cancel.Click += (_, _) => dialog.DialogResult = false;
                buttons.Children.Add(ok);
                buttons.Children.Add(cancel);
                root.Children.Add(buttons);

                dialog.Content = root;
                try { nameBox.Focus(); nameBox.SelectAll(); } catch { }
                if (dialog.ShowDialog() == true)
                    SaveAndRefresh();
            }

            cm.Items.Add(MI(collection.Enabled ? "✓ Выключить подборку" : "Включить подборку", () => { collection.Enabled = !collection.Enabled; }));
            cm.Items.Add(new Separator());
            cm.Items.Add(MI("Недавние (7 дней)", () => ApplyPreset("Недавние (7 дней)", c => { c.WithinDays = 7; c.Recursive = true; c.Limit = 60; })));
            cm.Items.Add(MI("Изображения (30 дней)", () => ApplyPreset("Изображения (30 дней)", c => { c.WithinDays = 30; c.Recursive = true; c.Kinds.Add("image"); c.Limit = 80; })));
            cm.Items.Add(MI("Документы (30 дней)", () => ApplyPreset("Документы (30 дней)", c => { c.WithinDays = 30; c.Recursive = true; c.Kinds.AddRange(new[]{"doc","pdf"}); c.Limit = 80; })));
            cm.Items.Add(MI("PDF (30 дней)", () => ApplyPreset("PDF (30 дней)", c => { c.WithinDays = 30; c.Recursive = true; c.Kinds.Add("pdf"); c.Limit = 80; })));
            cm.Items.Add(MI("Крупные (>100 МБ)", () => ApplyPreset("Крупные (>100 МБ)", c => { c.MinSizeMb = 100; c.Recursive = true; c.Limit = 60; })));
            cm.Items.Add(MI("Архивы (30 дней)", () => ApplyPreset("Архивы (30 дней)", c => { c.WithinDays = 30; c.Recursive = true; c.Kinds.Add("archive"); c.Limit = 80; })));
            cm.Items.Add(new Separator());

            var editMi = new MenuItem { Header = "Настроить правила…" };
            editMi.Click += (_, _) => EditRulesDialog();
            cm.Items.Add(editMi);

            var savePortalMi = new MenuItem { Header = "Сохранить как портал…" };
            savePortalMi.Click += (_, _) =>
            {
                var name = PromptText("Сохранить портал", "Имя портала/набора:", string.IsNullOrWhiteSpace(collection?.Preset) ? "Мой портал" : collection!.Preset);
                if (string.IsNullOrWhiteSpace(name)) return;
                var portals = DotShell.Core.Widgets.FolderPortalStore.Load();
                var spec = (collection ?? new DotShell.Core.Widgets.FolderCollectionSpec()).Clone();
                spec.Enabled = true;
                if (string.IsNullOrWhiteSpace(spec.Preset)) spec.Preset = name.Trim();
                var existing = portals.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (existing is not null) existing.Spec = spec; else portals.Add(new DotShell.Core.Widgets.FolderPortal { Name = name.Trim(), Spec = spec });
                DotShell.Core.Widgets.FolderPortalStore.Save(portals.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList());
                collection = spec.Clone();
                SaveAndRefresh();
            };
            cm.Items.Add(savePortalMi);

            var portalsRoot = new MenuItem { Header = "Порталы" };
            var savedPortals = DotShell.Core.Widgets.FolderPortalStore.Load();
            if (savedPortals.Count == 0)
            {
                portalsRoot.Items.Add(new MenuItem { Header = "(нет сохранённых)", IsEnabled = false });
            }
            else
            {
                foreach (var portal in savedPortals.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList())
                {
                    var pitem = new MenuItem { Header = portal.Name };
                    var pcopy = portal;
                    pitem.Click += (_, _) =>
                    {
                        collection = (pcopy.Spec ?? new DotShell.Core.Widgets.FolderCollectionSpec()).Clone();
                        collection.Enabled = true;
                        if (string.IsNullOrWhiteSpace(collection.Preset)) collection.Preset = pcopy.Name;
                        SaveAndRefresh();
                    };
                    portalsRoot.Items.Add(pitem);
                }
                portalsRoot.Items.Add(new Separator());
                var delPortal = new MenuItem { Header = "Удалить портал…" };
                delPortal.Click += (_, _) =>
                {
                    var names = string.Join(", ", DotShell.Core.Widgets.FolderPortalStore.Load().Select(p => p.Name));
                    var name = PromptText("Удалить портал", $"Имя портала для удаления:\n{names}", "");
                    if (string.IsNullOrWhiteSpace(name)) return;
                    var list = DotShell.Core.Widgets.FolderPortalStore.Load();
                    int removed = list.RemoveAll(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (removed > 0) DotShell.Core.Widgets.FolderPortalStore.Save(list);
                };
                portalsRoot.Items.Add(delPortal);
            }
            cm.Items.Add(portalsRoot);

            cm.Items.Add(new Separator());
            var rec = new MenuItem { Header = "Искать во вложенных", IsCheckable = true, IsChecked = collection.Recursive };
            rec.Click += (_, _) => { collection.Recursive = !collection.Recursive; SaveAndRefresh(); };
            cm.Items.Add(rec);
            cm.Items.Add(MI("Сбросить (обычная папка)", () => { collection.Enabled = false; collection.Preset = string.Empty; collection.Kinds.Clear(); collection.NameContains = string.Empty; collection.WithinDays = 0; collection.MinSizeMb = 0; collection.Scope = string.Empty; collection.Limit = 50; }));
            cm.PlacementTarget = collBtn;
            cm.IsOpen = true;
        };

        var newBtn = WBtn("+", "Создать папку");
        newBtn.Padding = new Thickness(6, 2, 6, 2);
        newBtn.Margin = new Thickness(4, 0, 0, 0);
        newBtn.Click += (_, _) =>
        {
            if (missing || !Directory.Exists(currentPath)) return;
            var input = PromptText("Новая папка", "Имя папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(input)) return;
            try
            {
                string created = System.IO.Path.Combine(currentPath, input.Trim());
                Directory.CreateDirectory(created);
                PushUndo("mkdir", created, "", true, "Папка создана — можно отменить");
            }
            catch (Exception ex) { Log.Info($"folder widget: create folder failed — {ex.Message}"); }
            finally { Rebuild(); }
        };

        var refreshBtn = WBtn("⟳", "Обновить");
        refreshBtn.Padding = new Thickness(6, 2, 6, 2);
        refreshBtn.Margin = new Thickness(4, 0, 0, 0);
        refreshBtn.Click += (_, _) => Rebuild();
        actions.Children.Add(upBtn);
        actions.Children.Add(openBtn);
        actions.Children.Add(sortBtn);
        actions.Children.Add(groupBtn);
        actions.Children.Add(collBtn);
        actions.Children.Add(newBtn);
        actions.Children.Add(refreshBtn);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);

        var filterBox = WTextBox("Фильтр по имени…");
        filterBox.Margin = new Thickness(0, 6, 0, 0);
        filterBox.TextChanged += (_, _) => { filter = filterBox.Text?.Trim() ?? string.Empty; Rebuild(); };

        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(filterBox);

        var container = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(previewPanel, Dock.Bottom);
        DockPanel.SetDock(undoBar, Dock.Bottom);
        container.Children.Add(top);
        container.Children.Add(previewPanel);
        container.Children.Add(undoBar);
        container.Children.Add(scroll);

        string UniqueDest(string dir, string name)
        {
            string dest = System.IO.Path.Combine(dir, name);
            if (!File.Exists(dest) && !Directory.Exists(dest)) return dest;
            string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = System.IO.Path.GetExtension(name);
            for (int i = 2; i < 10000; i++)
            {
                string candidate = System.IO.Path.Combine(dir, $"{baseName} ({i}){ext}");
                if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
            }
            return System.IO.Path.Combine(dir, $"{baseName} ({Guid.NewGuid():N}){ext}");
        }

        // 0=replace,1=skip,2=keep-both
        int? conflictAll = null;
        int AskConflict(string name)
        {
            if (conflictAll.HasValue) return conflictAll.Value;
            if (!Dispatcher.CheckAccess())
                return Dispatcher.Invoke(() => AskConflict(name));
            try
            {
                var dialog = new Window
                {
                    Title = "Конфликт имён",
                    Width = 460,
                    SizeToContent = SizeToContent.Height,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = this,
                    ResizeMode = ResizeMode.NoResize,
                    WindowStyle = WindowStyle.ToolWindow,
                    Background = ResBrush("DsBg"),
                };
                var root = new StackPanel { Margin = new Thickness(14) };
                root.Children.Add(new TextBlock
                {
                    Text = $"«{name}» уже существует в папке назначения.",
                    Foreground = ResBrush("DsText"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10),
                });
                var applyAll = new CheckBox { Content = "Применить ко всем оставшимся", Margin = new Thickness(0, 0, 0, 10), Foreground = ResBrush("DsTextMuted") };
                root.Children.Add(applyAll);
                var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
                int choice = 1;
                Button Mk(string text, int value, bool def = false)
                {
                    var b = new Button { Content = text, MinWidth = 110, Margin = new Thickness(6, 0, 0, 0), IsDefault = def };
                    b.Click += (_, _) => { choice = value; if (applyAll.IsChecked == true) conflictAll = value; dialog.DialogResult = true; };
                    return b;
                }
                buttons.Children.Add(Mk("Заменить", 0));
                buttons.Children.Add(Mk("Пропустить", 1, true));
                buttons.Children.Add(Mk("Сохранить оба", 2));
                root.Children.Add(buttons);
                dialog.Content = root;
                dialog.ShowDialog();
                return choice;
            }
            catch { return 1; }
        }

        void ReportProgress(string text)
        {
            if (Dispatcher.CheckAccess()) SetFileOpUi(true, text);
            else Dispatcher.BeginInvoke(() => SetFileOpUi(true, text));
        }

        void CopySelectionToClipboard(bool cut)
        {
            if (selected.Count == 0) return;
            try
            {
                var files = new StringCollection();
                cutPaths.Clear();
                foreach (var p in selected)
                {
                    files.Add(p);
                    if (cut) cutPaths.Add(p);
                }
                Clipboard.SetFileDropList(files);
                cutPending = cut;
                RefreshSelectionVisuals();
            }
            catch (Exception ex) { Log.Info($"folder widget: {(cut ? "cut" : "copy")} failed — {ex.Message}"); }
        }

        async void PasteIntoCurrent()
        {
            if (fileOpRunning) return;
            if (missing || !Directory.Exists(currentPath)) return;
            if (!Clipboard.ContainsFileDropList()) return;

            var files = Clipboard.GetFileDropList();
            var fileList = new List<string>();
            foreach (var p in files)
            {
                if (!string.IsNullOrWhiteSpace(p)) fileList.Add(p);
            }
            if (fileList.Count == 0) return;

            conflictAll = null;
            bool doMove = cutPending && fileList.Count > 0;
            if (doMove)
            {
                foreach (var p in fileList)
                {
                    if (!cutPaths.Contains(p)) { doMove = false; break; }
                }
            }

            fileOpCts?.Dispose();
            fileOpCts = new CancellationTokenSource();
            var ct = fileOpCts.Token;
            var movedPairs = new List<(string src, string dst, bool isDir)>();
            int errors = 0;
            bool cancelled = false;
            string opName = doMove ? "Перемещение" : "Копирование";

            long doneBytes = 0;

            long totalBytes = 0;



            SetFileOpUi(true, opName);

            try
            {
                await Task.Run(() =>
                {
                    totalBytes = doMove ? 0 : ComputeTotalBytes(fileList);
                    var startedUtc = DateTime.UtcNow;


                    for (int i = 0; i < fileList.Count; i++)
                    {
                        if (ct.IsCancellationRequested) { cancelled = true; break; }
                        string p = fileList[i];
                        string label = System.IO.Path.GetFileName(p);
                        ReportProgress(FormatProgressText(opName, i + 1, fileList.Count, label, System.Threading.Interlocked.Read(ref doneBytes), totalBytes, startedUtc));

                        long lastReportTicks = 0;
                        void AddBytes(long b)
                        {
                            long nowDone = System.Threading.Interlocked.Add(ref doneBytes, b);
                            long now = DateTime.UtcNow.Ticks;
                            long prev = System.Threading.Interlocked.Read(ref lastReportTicks);
                            if (now - prev >= TimeSpan.TicksPerMillisecond * 400 && System.Threading.Interlocked.CompareExchange(ref lastReportTicks, now, prev) == prev)
                            {
                                ReportProgress(FormatProgressText(opName, i + 1, fileList.Count, label, nowDone, totalBytes, startedUtc));
                            }
                        }

                        try
                        {
                            if (!File.Exists(p) && !Directory.Exists(p)) continue;
                            bool isDirSrc = Directory.Exists(p);
                            string name = System.IO.Path.GetFileName(p);
                            string dest = System.IO.Path.Combine(currentPath, name);

                            if (string.Equals(System.IO.Path.GetFullPath(p), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                                continue;

                            bool exists = isDirSrc ? Directory.Exists(dest) : File.Exists(dest);
                            if (exists || (isDirSrc ? File.Exists(dest) : Directory.Exists(dest)))
                            {
                                int action = AskConflict(name);
                                if (action == 1) continue;
                                if (action == 2) dest = UniqueDest(currentPath, name);
                                if (action == 0)
                                {
                                    try
                                    {
                                        if (Directory.Exists(dest)) Directory.Delete(dest, true);
                                        else if (File.Exists(dest)) File.Delete(dest);
                                    }
                                    catch (Exception ex) { Log.Info($"folder widget: replace delete failed {dest} — {ex.Message}"); errors++; continue; }
                                }
                            }

                            if (ct.IsCancellationRequested) { cancelled = true; break; }

                            if (doMove)
                            {
                                bool movedOk = false;
                                try
                                {
                                    if (isDirSrc) Directory.Move(p, dest);
                                    else File.Move(p, dest);
                                    movedOk = true;
                                }
                                catch (IOException) { }
                                catch (Exception ex)
                                {
                                    Log.Info($"folder widget: move paste failed {p} — {ex.Message}");
                                    errors++;
                                    continue;
                                }

                                if (!movedOk)
                                {
                                    try
                                    {
                                        if (isDirSrc) { CopyDirectoryCancellable(p, dest, ct, AddBytes); if (!ct.IsCancellationRequested) Directory.Delete(p, true); }
                                        else { CopyFileCancellable(p, dest, ct, AddBytes); if (!ct.IsCancellationRequested) File.Delete(p); }
                                        if (ct.IsCancellationRequested) { cancelled = true; break; }
                                        movedOk = true;
                                    }
                                    catch (OperationCanceledException) { cancelled = true; break; }
                                    catch (Exception ex) { Log.Info($"folder widget: move paste fallback failed {p} — {ex.Message}"); errors++; continue; }
                                }

                                if (movedOk) movedPairs.Add((p, dest, isDirSrc));
                            }
                            else
                            {
                                try
                                {
                                    if (isDirSrc) CopyDirectoryCancellable(p, dest, ct, AddBytes);
                                    else CopyFileCancellable(p, dest, ct, AddBytes);
                                }
                                catch (OperationCanceledException) { cancelled = true; break; }
                                catch (Exception ex) { Log.Info($"folder widget: paste failed {p} — {ex.Message}"); errors++; }
                            }
                        }
                        catch (OperationCanceledException) { cancelled = true; break; }
                        catch (Exception ex) { Log.Info($"folder widget: paste failed {p} — {ex.Message}"); errors++; }
                    }
                }, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: paste failed — {ex.Message}");
                errors++;
            }
            finally
            {
                if (doMove && movedPairs.Count > 0)
                {
                    foreach (var m in movedPairs) cutPaths.Remove(m.src);
                    cutPending = cutPaths.Count > 0;
                    try { if (!cancelled) Clipboard.Clear(); } catch { }
                    PushUndoBatch(movedPairs.Select(m => ("move", m.src, m.dst, m.isDir)),
                        $"Перемещено — можно отменить ({movedPairs.Count})");
                }

                fileOpRunning = false;
                string status = cancelled ? "Операция отменена" : (errors > 0 ? $"Готово (ошибок: {errors})" : "Готово");
                if (doMove && movedPairs.Count > 0 && !cancelled && errors == 0)
                    status = $"Перемещено — можно отменить ({movedPairs.Count})";
                else if (!cancelled && errors == 0 && !doMove)
                    status = "Скопировано";

                SetFileOpUi(false, status);
                if (undoStack.Count == 0)
                {
                    undoBar.Visibility = Visibility.Visible;
                    undoBtn.IsEnabled = false;
                    undoBtn.Content = "Отменить";
                    undoText.Text = status;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += (_, _) => { t.Stop(); if (!fileOpRunning && undoStack.Count == 0) { undoBar.Visibility = Visibility.Collapsed; } };
                    t.Start();
                }
                Rebuild();
            }
        }


        var menu = new ContextMenu();
        var openRoot = new MenuItem { Header = "Открыть текущую папку" };
        openRoot.Click += (_, _) =>
        {
            try { if (Directory.Exists(currentPath)) System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(currentPath) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Info($"folder widget: open root failed — {ex.Message}"); }
        };
        menu.Items.Add(openRoot);
        var toRoot = new MenuItem { Header = "К корню виджета" };
        toRoot.Click += (_, _) => NavigateTo(rootPath);
        menu.Items.Add(toRoot);
        var copyPath = new MenuItem { Header = "Копировать путь папки" };
        copyPath.Click += (_, _) => CopyToClipboardText(currentPath);
        menu.Items.Add(copyPath);

        menu.Items.Add(new Separator());
        var newFolder = new MenuItem { Header = "Создать папку" };
        newFolder.Click += (_, _) =>
        {
            if (missing || !Directory.Exists(currentPath)) return;
            var input = PromptText("Новая папка", "Имя папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(input)) return;
            try
            {
                string created = System.IO.Path.Combine(currentPath, input.Trim());
                Directory.CreateDirectory(created);
                PushUndo("mkdir", created, "", true, "Папка создана — можно отменить");
            }
            catch (Exception ex) { Log.Info($"folder widget: create folder failed — {ex.Message}"); }
            finally { Rebuild(); }
        };
        menu.Items.Add(newFolder);

        var paste = new MenuItem { Header = "Вставить" };
        paste.Click += (_, _) => PasteIntoCurrent();
        menu.Items.Add(paste);

        var selectAllItem = new MenuItem { Header = "Выделить всё" };
        selectAllItem.Click += (_, _) =>
        {
            selected.Clear();
            foreach (var p in visiblePaths) selected.Add(p);
            if (visiblePaths.Count > 0)
            {
                lastClickedPath = visiblePaths[^1];
                ShowPreview(lastClickedPath, Directory.Exists(lastClickedPath));
            }
            RefreshSelectionVisuals();
        };
        menu.Items.Add(selectAllItem);

        menu.Items.Add(new Separator());
        var refreshItem = new MenuItem { Header = "Обновить" };
        refreshItem.Click += (_, _) => Rebuild();
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(RemoveWidgetItem());
        container.ContextMenu = menu;
        _root.ContextMenu = null;

        container.Focusable = true;
        void MarkSeen(string path)
        {
            if (changedPaths.Remove(path) && dotByPath.TryGetValue(path, out var dot))
                dot.Visibility = Visibility.Collapsed;
        }

        void SelectSingle(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            MarkSeen(path);
            selected.Clear();
            selected.Add(path);
            lastClickedPath = path;
            RefreshSelectionVisuals();
            ShowPreview(path, Directory.Exists(path));
            if (rowByPath.TryGetValue(path, out var b)) b.BringIntoView();
        }

        void HandleKeys(object? _, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            if (filterBox.IsKeyboardFocusWithin) return;

            if (ctrl && e.Key == Key.A)
            {
                selected.Clear();
                foreach (var p in visiblePaths) selected.Add(p);
                if (visiblePaths.Count > 0)
                {
                    lastClickedPath = visiblePaths[^1];
                    ShowPreview(lastClickedPath, Directory.Exists(lastClickedPath));
                }
                RefreshSelectionVisuals();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.C)
            {
                CopySelectionToClipboard(cut: false);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.X)
            {
                CopySelectionToClipboard(cut: true);
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.V)
            {
                PasteIntoCurrent();
                e.Handled = true;
            }
            else if (ctrl && e.Key == Key.Z)
            {
                TryUndo();
                e.Handled = true;
            }
            else if ((e.Key == Key.Down || e.Key == Key.Up) && visiblePaths.Count > 0)
            {
                int idx = lastClickedPath is not null ? visiblePaths.IndexOf(lastClickedPath) : -1;
                if (idx < 0) idx = selected.Count > 0 ? visiblePaths.FindIndex(p => selected.Contains(p)) : -1;
                if (idx < 0) idx = e.Key == Key.Down ? -1 : visiblePaths.Count;
                int next = e.Key == Key.Down ? Math.Min(idx + 1, visiblePaths.Count - 1) : Math.Max(idx - 1, 0);
                if (shift && lastClickedPath is not null)
                {
                    // Простое расширение выделения стрелками от якоря не реализуем полноценно — ведём как одиночный выбор.
                }
                SelectSingle(visiblePaths[next]);
                e.Handled = true;
            }
            else if ((e.Key == Key.Enter || e.Key == Key.Right) && !string.IsNullOrWhiteSpace(lastClickedPath))
            {
                string p = lastClickedPath;
                if (Directory.Exists(p)) NavigateTo(p);
                else if (File.Exists(p))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(p) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Info($"folder widget: open failed {p} — {ex.Message}"); }
                }
                e.Handled = true;
            }
            else if ((e.Key == Key.Left || e.Key == Key.Back) && !string.Equals(currentPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                string? parent = System.IO.Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrWhiteSpace(parent)) NavigateTo(parent);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                selected.Clear();
                RefreshSelectionVisuals();
                ClearPreview();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && selected.Count > 0)
            {
                var recycled = new List<string>();
                bool pinsChanged = false;
                foreach (var p in selected.ToList())
                {
                    if (RecyclePath(p))
                    {
                        recycled.Add(p);
                        if (pinned.Remove(p)) pinsChanged = true;
                    }
                }
                if (pinsChanged) SavePins();
                if (recycled.Count > 0)
                    PushUndoBatch(recycled.Select(p => ("recycle", p, string.Empty, false)),
                        recycled.Count == 1 ? "Удалено в корзину — можно отменить" : $"Удалено в корзину ({recycled.Count}) — можно отменить");
                Rebuild();
                e.Handled = true;
            }
        }
        container.PreviewKeyDown += HandleKeys;
        scroll.PreviewKeyDown += HandleKeys;

        AllowDrop = true;
        container.AllowDrop = true;
        scroll.AllowDrop = true;
        list.AllowDrop = true;

        void OnDragOver(object? s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !missing && !fileOpRunning ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        async void OnDrop(object? s, DragEventArgs e)
        {
            e.Handled = true;
            if (fileOpRunning || missing || !Directory.Exists(currentPath)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

            fileOpCts?.Dispose();

            fileOpCts = new CancellationTokenSource();

            var ct = fileOpCts.Token;

            int errors = 0;

            bool cancelled = false;

            long doneBytes = 0;

            long totalBytes = 0;

            string opName = "Перетаскивание";



            SetFileOpUi(true, "Перетаскивание");

            try

            {

                await Task.Run(() =>

                {

                    totalBytes = ComputeTotalBytes(paths);
                    var startedUtc = DateTime.UtcNow;


                    for (int i = 0; i < paths.Length; i++)

                    {

                        if (ct.IsCancellationRequested) { cancelled = true; break; }

                        string sourcePath = paths[i];

                        if (string.IsNullOrWhiteSpace(sourcePath)) continue;

                        string label = System.IO.Path.GetFileName(sourcePath);

                        ReportProgress(FormatProgressText(opName, i + 1, paths.Length, label, System.Threading.Interlocked.Read(ref doneBytes), totalBytes, startedUtc));

                        long lastReportTicks = 0;
                        void AddBytes(long b)
                        {
                            long nowDone = System.Threading.Interlocked.Add(ref doneBytes, b);
                            long now = DateTime.UtcNow.Ticks;
                            long prev = System.Threading.Interlocked.Read(ref lastReportTicks);
                            if (now - prev >= TimeSpan.TicksPerMillisecond * 400 && System.Threading.Interlocked.CompareExchange(ref lastReportTicks, now, prev) == prev)
                            {
                                ReportProgress(FormatProgressText(opName, i + 1, paths.Length, label, nowDone, totalBytes, startedUtc));
                            }
                        }

                        try
                        {
                            string name = System.IO.Path.GetFileName(sourcePath);
                            string dest = System.IO.Path.Combine(currentPath, name);
                            if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (Directory.Exists(dest) || File.Exists(dest))
                            {
                                string baseName = System.IO.Path.GetFileNameWithoutExtension(name);
                                string ext = System.IO.Path.GetExtension(name);
                                for (int k = 2; k < 10000; k++)
                                {
                                    string candidate = System.IO.Path.Combine(currentPath, $"{baseName} ({k}){ext}");
                                    if (!File.Exists(candidate) && !Directory.Exists(candidate)) { dest = candidate; break; }
                                }
                            }

                            bool moved = false;
                            try
                            {
                                if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, dest);
                                else if (File.Exists(sourcePath)) File.Move(sourcePath, dest);
                                else continue;
                                moved = true;
                            }
                            catch (IOException) { }
                            catch (Exception ex)
                            {
                                Log.Info($"folder widget: drop move failed {sourcePath} — {ex.Message}");
                                errors++;
                                continue;
                            }

                            if (!moved)
                            {
                                try
                                {
                                    if (Directory.Exists(sourcePath)) CopyDirectoryCancellable(sourcePath, dest, ct, AddBytes);
                                    else if (File.Exists(sourcePath)) CopyFileCancellable(sourcePath, dest, ct, AddBytes);
                                }
                                catch (OperationCanceledException) { cancelled = true; break; }
                                catch (Exception ex) { Log.Info($"folder widget: drop copy failed {sourcePath} — {ex.Message}"); errors++; }
                            }
                        }
                        catch (OperationCanceledException) { cancelled = true; break; }
                        catch (Exception ex) { Log.Info($"folder widget: drop failed {sourcePath} — {ex.Message}"); errors++; }
                    }
                }, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: drop failed — {ex.Message}");
                errors++;
            }
            finally
            {
                fileOpRunning = false;
                string status = cancelled ? "Перенос отменён" : (errors > 0 ? $"Готово (ошибок: {errors})" : "Перенос завершён");
                SetFileOpUi(false, status);
                if (undoStack.Count == 0)
                {
                    undoBar.Visibility = Visibility.Visible;
                    undoBtn.IsEnabled = false;
                    undoBtn.Content = "Отменить";
                    undoText.Text = status;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                    t.Tick += (_, _) => { t.Stop(); if (!fileOpRunning && undoStack.Count == 0) { undoBar.Visibility = Visibility.Collapsed; } };
                    t.Start();
                }
                Rebuild();
            }
        }

        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;

        bool IsOnRow(DependencyObject? src)
        {
            while (src is not null)
            {
                if (ReferenceEquals(src, scroll) || ReferenceEquals(src, listHost)) break;
                if (src is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrEmpty(tag) && rowByPath.ContainsKey(tag))
                    return true;
                src = VisualTreeHelper.GetParent(src);
            }
            return false;
        }

        Rect MarqueeBounds(Point a, Point b)
        {
            double x = Math.Min(a.X, b.X);
            double y = Math.Min(a.Y, b.Y);
            return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        }

        void ApplyMarqueeSelection(Rect rect)
        {
            var hit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in rowByPath)
            {
                try
                {
                    if (!kv.Value.IsVisible) continue;
                    var p = kv.Value.TranslatePoint(new Point(0, 0), list);
                    var r = new Rect(p, kv.Value.RenderSize);
                    if (r.IntersectsWith(rect)) hit.Add(kv.Key);
                }
                catch { }
            }

            if (marqueeMods.HasFlag(ModifierKeys.Control))
            {
                selected.Clear();
                foreach (var p in marqueeSnapshot) selected.Add(p);
                foreach (var p in hit)
                {
                    if (!selected.Remove(p)) selected.Add(p);
                }
            }
            else if (marqueeMods.HasFlag(ModifierKeys.Shift))
            {
                // Расширение диапазона от якоря до области резинки (по видимому порядку).
                selected.Clear();
                foreach (var p in marqueeSnapshot) selected.Add(p);
                if (hit.Count > 0)
                {
                    int anchor = !string.IsNullOrWhiteSpace(lastClickedPath) ? visiblePaths.IndexOf(lastClickedPath) : -1;
                    if (anchor < 0 && marqueeSnapshot.Count > 0)
                    {
                        foreach (var s in marqueeSnapshot) { int si = visiblePaths.IndexOf(s); if (si >= 0) { anchor = si; break; } }
                    }
                    if (anchor >= 0)
                    {
                        int lo = anchor, hi = anchor;
                        foreach (var p in hit)
                        {
                            int idx = visiblePaths.IndexOf(p);
                            if (idx < 0) continue;
                            if (idx < lo) lo = idx;
                            if (idx > hi) hi = idx;
                        }
                        for (int i = lo; i <= hi && i < visiblePaths.Count; i++) selected.Add(visiblePaths[i]);
                    }
                    else
                    {
                        foreach (var p in hit) selected.Add(p);
                    }
                }
            }
            else
            {
                selected.Clear();
                foreach (var p in hit) selected.Add(p);
            }
            RefreshSelectionVisuals();
        }

        void EndMarquee(bool cancel)
        {
            if (marqueeAutoScroll is not null) { marqueeAutoScroll.Stop(); marqueeAutoScroll = null; }
            if (!marqueePressed && !marqueeActive) return;
            marqueePressed = false;
            if (marqueeActive)
            {
                marqueeActive = false;
                marqueeRect.Visibility = Visibility.Collapsed;
                if (cancel)
                {
                    selected.Clear();
                    foreach (var p in marqueeSnapshot) selected.Add(p);
                    RefreshSelectionVisuals();
                }
            }
            try { scroll.ReleaseMouseCapture(); } catch { }
        }

        scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (missing || fileOpRunning) return;
            if (IsOnRow(e.OriginalSource as DependencyObject)) return;
            marqueePressed = true;
            marqueeActive = false;
            marqueeMods = Keyboard.Modifiers;
            marqueeSnapshot = new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase);
            marqueeStart = e.GetPosition(list);
            scroll.Focus();
            scroll.CaptureMouse();
        };

        scroll.PreviewMouseMove += (_, e) =>
        {
            if (!marqueePressed || e.LeftButton != MouseButtonState.Pressed) return;
            Point cur = e.GetPosition(list);
            Vector d = cur - marqueeStart;
            if (!marqueeActive)
            {
                if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;
                marqueeActive = true;
                marqueeRect.Visibility = Visibility.Visible;
            }

            Point inScroll = e.GetPosition(scroll);
            double edge = 24;
            double dy = 0;
            if (inScroll.Y < edge) dy = -Math.Max(2, (edge - inScroll.Y) * 0.4);
            else if (inScroll.Y > scroll.ViewportHeight - edge) dy = Math.Max(2, (inScroll.Y - (scroll.ViewportHeight - edge)) * 0.4);
            if (Math.Abs(dy) > 0.1)
            {
                scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + dy)));
                cur = e.GetPosition(list);
            }

            var rect = MarqueeBounds(marqueeStart, cur);
            Canvas.SetLeft(marqueeRect, rect.X);
            Canvas.SetTop(marqueeRect, rect.Y);
            marqueeRect.Width = rect.Width;
            marqueeRect.Height = rect.Height;
            ApplyMarqueeSelection(rect);
            e.Handled = true;
        };

        void MarqueeAutoTick(object? s2, EventArgs e2)
        {
            if (!marqueeActive || Mouse.LeftButton != MouseButtonState.Pressed) { EndMarquee(false); return; }
            try
            {
                Point ms = Mouse.GetPosition(scroll);
                double edge2 = 24; double dy2 = 0;
                if (ms.Y < edge2) dy2 = -Math.Max(2, (edge2 - ms.Y) * 0.4);
                else if (ms.Y > scroll.ViewportHeight - edge2) dy2 = Math.Max(2, (ms.Y - (scroll.ViewportHeight - edge2)) * 0.4);
                if (Math.Abs(dy2) > 0.1)
                {
                    scroll.ScrollToVerticalOffset(Math.Max(0, Math.Min(scroll.ScrollableHeight, scroll.VerticalOffset + dy2)));
                    Point cur2 = Mouse.GetPosition(list);
                    var r2 = MarqueeBounds(marqueeStart, cur2);
                    Canvas.SetLeft(marqueeRect, r2.X);
                    Canvas.SetTop(marqueeRect, r2.Y);
                    marqueeRect.Width = r2.Width;
                    marqueeRect.Height = r2.Height;
                    ApplyMarqueeSelection(r2);
                }
            }
            catch { }
        }
        marqueeAutoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        marqueeAutoScroll.Tick += MarqueeAutoTick;

        scroll.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!marqueePressed) return;
            bool wasActive = marqueeActive;
            EndMarquee(false);
            if (wasActive)
            {
                e.Handled = true;
            }
            else
            {
                if (!marqueeMods.HasFlag(ModifierKeys.Control) && !marqueeMods.HasFlag(ModifierKeys.Shift))
                {
                    selected.Clear();
                    RefreshSelectionVisuals();
                    ClearPreview();
                }
            }
        };

        scroll.LostMouseCapture += (_, _) => EndMarquee(true);

        Timer(5000, Rebuild);

        _relayout = (_, h) => scroll.MaxHeight = Math.Max(80, h - 110
            - (previewPanel.Visibility == Visibility.Visible ? 90 : 0)
            - (undoBar.Visibility == Visibility.Visible ? 28 : 0));
        return container;
    }

    private static void CopyFileCancellable(string source, string dest, CancellationToken ct, Action<long>? addBytes = null)
    {
        ct.ThrowIfCancellationRequested();
        const int bufSize = 1024 * 128;
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufSize, FileOptions.SequentialScan);
        using var output = new FileStream(dest, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufSize, FileOptions.SequentialScan);
        var buffer = new byte[bufSize];
        int read;
        long since = 0;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            since += read;
            if (since >= 1024 * 512)
            {
                addBytes?.Invoke(since);
                since = 0;
            }
        }
        if (since > 0) addBytes?.Invoke(since);
    }

    private static void CopyDirectoryCancellable(string sourceDir, string destDir, CancellationToken ct, Action<long>? addBytes = null)
    {
        ct.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyFileCancellable(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), ct, addBytes);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectoryCancellable(dir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir)), ct, addBytes);
        }
    }

    private static long ComputeTotalBytes(IEnumerable<string> paths)
    {
        long total = 0;
        foreach (var p in paths)
        {
            try
            {
                if (File.Exists(p)) { total += new FileInfo(p).Length; continue; }
                if (Directory.Exists(p))
                {
                    foreach (var f in Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                    {
                        try { total += new FileInfo(f).Length; } catch { }
                    }
                }
            }
            catch { }
        }
        return total;
    }

    private static string FormatProgressText(string op, int i, int n, string label, long doneBytes, long totalBytes, DateTime? startedUtc = null)
    {
        string baseText = $"{op} {i}/{n}: {label}";
        if (totalBytes <= 0) return baseText;
        double doneMb = doneBytes / 1024.0 / 1024.0;
        double totalMb = totalBytes / 1024.0 / 1024.0;
        string eta = string.Empty;
        if (startedUtc is { } s && doneBytes > 0 && doneBytes < totalBytes)
        {
            double elapsed = (DateTime.UtcNow - s).TotalSeconds;
            if (elapsed >= 1.0)
            {
                double rate = doneBytes / elapsed;
                if (rate > 0)
                {
                    double rem = (totalBytes - doneBytes) / rate;
                    if (rem < 1) eta = ", <1с";
                    else if (rem < 90) eta = $", ~{Math.Ceiling(rem):0}с";
                    else if (rem < 3600) eta = $", ~{Math.Ceiling(rem / 60):0}мин";
                    else eta = $", ~{rem / 3600:0.#}ч";
                }
            }
        }
        return $"{baseText} · {doneMb:0.#}/{totalMb:0.#} МБ{eta}";
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, System.IO.Path.Combine(destDir, System.IO.Path.GetFileName(dir)));
    }

    private Brush ResBrush(string key) => TryFindResource(key) as Brush ?? Brushes.White;

    /// <summary>
    /// Опустить окно на самый низ z-порядка: виджет остаётся на обоях, а любые
    /// обычные окна его перекрывают.
    ///
    /// Почему не родитель WorkerW (трюк живых обоев): он ломает ввод и перетаскивание,
    /// а нам нужны живые виджеты. HWND_BOTTOM даёт тот же визуальный результат и
    /// переживает перезапуск проводника.
    /// </summary>
    private void SendToDesktopLayer()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // D1.2: согласованный порядок — host ниже, виджет поверх host (но под обычными окнами).
            if (ShellServices.Instance.Settings.Current.DesktopHostEnabled)
            {
                try { DesktopHostWindow.ReassertBottom(); } catch { }
            }

            Win32.SetWindowPos(hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>Применить desktop-layer режим (DesktopWidgets/DesktopHostEnabled) к уже открытому окну.</summary>
    public void ApplyDesktopMode()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var settings = ShellServices.Instance.Settings.Current;
            bool desktopMode = settings.DesktopWidgets || settings.DesktopHostEnabled;
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();

            if (desktopMode) ex |= Win32.WS_EX_NOACTIVATE;
            else ex &= ~Win32.WS_EX_NOACTIVATE;

            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

            if (desktopMode) SendToDesktopLayer();
        }
        catch { }
    }

    /// <summary>Периодически возвращаем окно на нижний слой (desktop widgets/host режим).</summary>
    private void KeepOnDesktopLayer()
    {
        var s = ShellServices.Instance.Settings.Current;
        if (!s.DesktopWidgets && !s.DesktopHostEnabled) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            var cur = ShellServices.Instance.Settings.Current;
            if (cur.DesktopWidgets || cur.DesktopHostEnabled)
                SendToDesktopLayer();
        };
        _timers.Add(timer);
        timer.Start();
    }

    // --- Утилиты для умной папки: безопасное удаление, переименование, создание ---

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private static bool RecyclePath(string path)
    {
        try
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = 3, // FO_DELETE
                pFrom = path + "\0\0",
                fFlags = 0x0040 /*FOF_ALLOWUNDO*/ | 0x0010 /*FOF_NOCONFIRMATION*/ | 0x0400 /*FOF_NOERRORUI*/ | 0x0004 /*FOF_SILENT*/,
            };
            return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
        }
        catch (Exception ex)
        {
            Log.Info($"folder widget: recycle failed {path} — {ex.Message}");
            return false;
        }
    }

    private string? PromptText(string title, string label, string initial = "")
    {
        try
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                ResizeMode = ResizeMode.NoResize,
                WindowStyle = WindowStyle.ToolWindow,
                Background = ResBrush("DsBg"),
            };

            var root = new StackPanel { Margin = new Thickness(14) };
            root.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = ResBrush("DsText"),
                FontFamily = new FontFamily("Segoe UI"),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            });
            var box = new TextBox { Text = initial ?? string.Empty, FontFamily = new FontFamily("Segoe UI") };
            root.Children.Add(box);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new Button { Content = "OK", MinWidth = 84, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "Отмена", MinWidth = 84, IsCancel = true };
            string? result = null;
            ok.Click += (_, _) => { result = box.Text; dialog.DialogResult = true; };
            cancel.Click += (_, _) => { dialog.DialogResult = false; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);
            dialog.Content = root;
            dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            return dialog.ShowDialog() == true ? result : null;
        }
        catch (Exception ex)
        {
            Log.Info($"folder widget: prompt failed — {ex.Message}");
            return null;
        }
    }
}

