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

    public string WidgetType => _type;
    public string WidgetPath => _path;

    public WidgetWindow(string type, int x, int y, int width = 0, int height = 0, string path = "")
    {
        _type = type;
        _path = path ?? string.Empty;
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

            // Режим «на обоях»: окно не активируется и уходит под остальные окна.
            if (ShellServices.Instance.Settings.Current.DesktopWidgets)
            {
                ex |= Win32.WS_EX_NOACTIVATE;
            }

            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

            if (ShellServices.Instance.Settings.Current.DesktopWidgets)
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
    public void ClampToScreen()
    {
        // Виртуальный экран, а не основной монитор: иначе виджет, унесённый на второй
        // монитор, прилипал обратно к основному после отпускания мыши.
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;

        Left = Math.Clamp(Left, vLeft - ActualWidth + 80, vLeft + SystemParameters.VirtualScreenWidth - 80);
        Top = Math.Clamp(Top, vTop, vTop + SystemParameters.VirtualScreenHeight - 40);

        // Прилипание к сетке: 0 — выкл, 8 или 16 px. Виджеты выстраиваются в ряд
        // без ручной подгонки по пикселям.
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

        var list = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 6, 0, 0),
        };

        bool AtRoot() => string.Equals(System.IO.Path.GetFullPath(currentPath).TrimEnd('\\', '/'),
            System.IO.Path.GetFullPath(rootPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        string SortLabel() => sortMode switch { 1 => "дата", 2 => "размер", 3 => "тип", _ => "имя" };

        Button? upBtnRef = null;
        Button? sortBtnRef = null;

        void RefreshHeader(int shown = -1, int total = -1)
        {
            if (missing || !Directory.Exists(currentPath))
            {
                missing = true;
                title.Text = "ПАПКА НЕ НАЙДЕНА";
                subtitle.Text = currentPath;
                if (upBtnRef is not null) upBtnRef.IsEnabled = false;
                return;
            }

            string name = System.IO.Path.GetFileName(currentPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(name)) name = currentPath;
            title.Text = name.ToUpperInvariant();

            string where = AtRoot() ? rootPath : currentPath;
            subtitle.Text = (shown >= 0 && total >= 0 && shown != total)
                ? $"{shown} из {total} · {SortLabel()} · {where}"
                : shown >= 0 ? $"{shown} объект(ов) · {SortLabel()} · {where}" : where;
            subtitle.ToolTip = where;

            if (upBtnRef is not null) upBtnRef.IsEnabled = !AtRoot();
            if (sortBtnRef is not null) sortBtnRef.Content = "⇅";
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

        void MoveOrCopyInto(string sourcePath, string targetDir)
        {
            string dest = System.IO.Path.Combine(targetDir, System.IO.Path.GetFileName(sourcePath));
            if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                if (Directory.Exists(sourcePath)) Directory.Move(sourcePath, dest);
                else if (File.Exists(sourcePath)) File.Move(sourcePath, dest);
                return;
            }
            catch (IOException)
            {
                // Между дисками Move не работает — пробуем копирование.
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: move failed {sourcePath} — {ex.Message}");
                throw;
            }

            try
            {
                if (Directory.Exists(sourcePath)) CopyDirectory(sourcePath, dest);
                else if (File.Exists(sourcePath)) File.Copy(sourcePath, dest, overwrite: false);
            }
            catch (Exception ex)
            {
                Log.Info($"folder widget: copy fallback failed {sourcePath} — {ex.Message}");
            }
        }

        void NavigateTo(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            // Не выходим выше корневой папки виджета.
            var rootFull = System.IO.Path.GetFullPath(rootPath).TrimEnd('\\', '/');
            var full = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return;
            currentPath = full;
            Rebuild();
        }

        UIElement Row(string path, bool isDir)
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

            var row = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(name);

            var border = new Border
            {
                Child = row,
                Padding = new Thickness(6, 4, 6, 4),
                CornerRadius = new CornerRadius(6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = path,
                Tag = path,
            };

            border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
            border.MouseLeftButtonUp += (_, e) =>
            {
                if (isDir)
                {
                    // Внутри виджета папки удобнее открывать навигацией; во внешний проводник — ПКМ.
                    NavigateTo(path);
                }
                else
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Exception ex) { Log.Info($"folder widget: open failed {path} — {ex.Message}"); }
                }
                e.Handled = true;
            };

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
            copy.Click += (_, _) => CopyToClipboardText(path);
            menu.Items.Add(copy);

            menu.Items.Add(new Separator());

            var rename = new MenuItem { Header = "Переименовать" };
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
                    if (Directory.Exists(path)) Directory.Move(path, dest);
                    else if (File.Exists(path)) File.Move(path, dest);
                }
                catch (Exception ex) { Log.Info($"folder widget: rename failed {path} — {ex.Message}"); }
                finally { Rebuild(); }
            };
            menu.Items.Add(rename);

            var delete = new MenuItem { Header = "Удалить в корзину" };
            delete.Click += (_, _) =>
            {
                try { RecyclePath(path); }
                catch (Exception ex) { Log.Info($"folder widget: delete failed {path} — {ex.Message}"); }
                finally { Rebuild(); }
            };
            menu.Items.Add(delete);

            menu.Items.Add(new Separator());
            menu.Items.Add(RemoveWidgetItem());
            border.ContextMenu = menu;

            return border;
        }

        void Rebuild()
        {
            try
            {
                list.Children.Clear();
                if (string.IsNullOrWhiteSpace(currentPath) || !Directory.Exists(currentPath))
                {
                    // Если текущая подпапка исчезла — пробуем вернуться к корню.
                    if (!string.Equals(currentPath, rootPath, StringComparison.OrdinalIgnoreCase) && Directory.Exists(rootPath))
                    {
                        currentPath = rootPath;
                    }
                    else
                    {
                        missing = true;
                        RefreshHeader();
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
                var dirInfos = new DirectoryInfo(currentPath).EnumerateDirectories().ToList();
                var fileInfos = new DirectoryInfo(currentPath).EnumerateFiles().ToList();

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

                var dirs = SortDirs(dirInfos.Where(d => Match(d.Name))).Select(d => d.FullName).ToList();
                var files = SortFiles(fileInfos.Where(f => Match(f.Name))).Select(f => f.FullName).ToList();
                int total = dirInfos.Count + fileInfos.Count;
                int shown = dirs.Count + files.Count;

                RefreshHeader(shown, total);

                foreach (var d in dirs.Take(160)) list.Children.Add(Row(d, true));
                foreach (var f in files.Take(240)) list.Children.Add(Row(f, false));

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
                RefreshHeader();
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

        RefreshHeader();
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
        upBtnRef = upBtn;

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
        sortBtnRef = sortBtn;

        var newBtn = WBtn("+", "Создать папку");
        newBtn.Padding = new Thickness(6, 2, 6, 2);
        newBtn.Margin = new Thickness(4, 0, 0, 0);
        newBtn.Click += (_, _) =>
        {
            if (missing || !Directory.Exists(currentPath)) return;
            var input = PromptText("Новая папка", "Имя папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(input)) return;
            try { Directory.CreateDirectory(System.IO.Path.Combine(currentPath, input.Trim())); }
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
        container.Children.Add(top);
        container.Children.Add(scroll);

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
            try { Directory.CreateDirectory(System.IO.Path.Combine(currentPath, input.Trim())); }
            catch (Exception ex) { Log.Info($"folder widget: create folder failed — {ex.Message}"); }
            finally { Rebuild(); }
        };
        menu.Items.Add(newFolder);

        menu.Items.Add(new Separator());
        var refreshItem = new MenuItem { Header = "Обновить" };
        refreshItem.Click += (_, _) => Rebuild();
        menu.Items.Add(refreshItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(RemoveWidgetItem());
        container.ContextMenu = menu;
        _root.ContextMenu = null;

        AllowDrop = true;
        container.AllowDrop = true;
        scroll.AllowDrop = true;
        list.AllowDrop = true;

        void OnDragOver(object? s, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !missing ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        void OnDrop(object? s, DragEventArgs e)
        {
            try
            {
                if (missing || !Directory.Exists(currentPath)) return;
                if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
                foreach (var p in paths)
                {
                    try { MoveOrCopyInto(p, currentPath); }
                    catch (Exception ex) { Log.Info($"folder widget: drop failed {p} — {ex.Message}"); }
                }
            }
            catch (Exception ex) { Log.Info($"folder widget: drop failed — {ex.Message}"); }
            finally { Rebuild(); }
            e.Handled = true;
        }

        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;

        Timer(5000, Rebuild);

        _relayout = (_, h) => scroll.MaxHeight = Math.Max(80, h - 92);
        return container;
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

            Win32.SetWindowPos(hwnd, new IntPtr(1) /* HWND_BOTTOM */, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>Каждые 2 секунды возвращаем окно на дно: иначе оно всплывает после кликов.</summary>
    private void KeepOnDesktopLayer()
    {
        if (!ShellServices.Instance.Settings.Current.DesktopWidgets) return;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => SendToDesktopLayer();
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

