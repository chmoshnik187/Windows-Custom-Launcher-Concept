namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
    private readonly Border _root;
    private readonly List<DispatcherTimer> _timers = new();

    public string WidgetType => _type;

    public WidgetWindow(string type, int x, int y, int width = 0, int height = 0)
    {
        _type = type;
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
        remove.Click += (_, _) => WidgetsManager.Close(type);
        _root.ContextMenu = new ContextMenu();
        _root.ContextMenu.Items.Add(remove);

        // У виджетов со своим меню (трей, курсы, погода) меню окна убираем:
        // иначе правый клик открывал только «Убрать виджет», а настройки
        // виджета были недоступны. Пункт «Убрать» добавляется внутрь своего меню.
        if (type is "tray" or "currency" or "weather") _root.ContextMenu = null;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Убираем из Alt+Tab: виджет — не «окно программы», а часть стола.
            var ex = Win32.GetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE).ToInt64();
            ex |= Win32.WS_EX_TOOLWINDOW;
            ex &= ~Win32.WS_EX_APPWINDOW;
            Win32.SetWindowLongPtr(hwnd, Win32.GWL_EXSTYLE, new IntPtr(ex));

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
        var wa = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, wa.Left - ActualWidth + 80, wa.Right - 80);
        Top = Math.Clamp(Top, wa.Top, wa.Bottom - 40);
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
            _root.Child = BuildContent(_type);
        }
        catch { }
    }
    /// <summary>Пункт «Убрать виджет» для собственных меню виджетов.</summary>
    private MenuItem RemoveWidgetItem()
    {
        var item = new MenuItem { Header = "Убрать виджет" };
        item.Click += (_, _) => WidgetsManager.Close(_type);
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
        "eq" => new MiniEq { MinWidth = 120, MinHeight = 36, IsRunning = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch },
        "media" => BuildMedia(),
        "system" => BuildSystem(),
        "units" => new Units { Margin = new Thickness(6), Interactive = true },
        "settings" => BuildSettings(),
        "clean" => BuildClean(),
        "dupes" => BuildDupes(),
        "update" => BuildUpdate(),
        "windows" => BuildWindows(),
        "audio" => BuildAudioRouter(),
        "currency" => BuildCurrency(),
        "tray" => BuildTray(),
        "weather" => BuildWeather(),
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
        var clock = new SegmentClock { Margin = new Thickness(0, 2, 0, 0) };

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(clock);

        _relayout = (w, h) =>
        {
            clock.Width = Math.Max(80, w);
            clock.Height = Math.Max(28, Math.Min(h, 130));
        };

        return panel;
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
    private UIElement BuildMedia()
    {
        var media = ShellServices.Instance.Media;

        var artwork = new Image { Stretch = Stretch.UniformToFill };
        var artBox = new Border
        {
            Width = 60,
            Height = 60,
            CornerRadius = new CornerRadius(12),
            ClipToBounds = true,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 12, 0),
        };
        artBox.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
        artBox.SetResourceReference(Border.BorderBrushProperty, "DsBorder");
        artBox.BorderThickness = new Thickness(1);
        artBox.Child = artwork;

        var title = new TextBlock
        {
            Style = (Style)FindResource("WTitle"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var artist = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var progress = new ProgressBar
        {
            Height = 3,
            Minimum = 0,
            Maximum = 100,
            BorderThickness = new Thickness(0),
            Margin = new Thickness(0, 8, 0, 0),
        };
        progress.SetResourceReference(ProgressBar.ForegroundProperty, "DsAccent");
        progress.SetResourceReference(ProgressBar.BackgroundProperty, "DsBorder");

        var timing = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Margin = new Thickness(0, 3, 0, 0),
        };

        var prev = new Button { Style = (Style)FindResource("WRound"), Content = "⏮" };
        var play = new Button { Style = (Style)FindResource("WRoundAccent"), Content = "▶", Margin = new Thickness(6, 0, 6, 0) };
        var next = new Button { Style = (Style)FindResource("WRound"), Content = "⏭" };
        prev.Click += (_, _) => _ = media.PreviousAsync();
        play.Click += (_, _) => _ = media.TogglePlayAsync();
        next.Click += (_, _) => _ = media.NextAsync();

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
        };
        buttons.Children.Add(prev);
        buttons.Children.Add(play);
        buttons.Children.Add(next);

        var info = new StackPanel();
        info.Children.Add(title);
        info.Children.Add(artist);
        info.Children.Add(progress);
        info.Children.Add(timing);
        info.Children.Add(buttons);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(artBox, 0);
        Grid.SetColumn(info, 1);
        root.Children.Add(artBox);
        root.Children.Add(info);

        // Пересобрать раскладку под текущий размер окна.
        void Relayout()
        {
            double w = ActualWidth > 0 ? ActualWidth : 260;
            double h = ActualHeight > 0 ? ActualHeight : 120;

            bool tiny = h < 118 || w < 235;
            bool big = h >= 210 && w >= 320;

            artBox.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            artBox.Width = artBox.Height = big ? 104 : 60;
            artBox.Margin = new Thickness(0, 0, big ? 16 : 12, 0);

            artist.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            progress.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;
            timing.Visibility = tiny ? Visibility.Collapsed : Visibility.Visible;

            title.FontSize = big ? 18 : 13;
            artist.FontSize = big ? 12 : 10;
            prev.Width = prev.Height = big ? 40 : 30;
            next.Width = next.Height = big ? 40 : 30;
            play.Width = play.Height = big ? 46 : 36;
            timing.FontSize = big ? 11 : 9;

            // Обложку и текст держим по центру: при большой высоте они не должны
            // липнуть к верху, оставляя пустую простыню снизу.
            artBox.VerticalAlignment = big ? VerticalAlignment.Center : VerticalAlignment.Top;
            info.VerticalAlignment = VerticalAlignment.Center;
            root.VerticalAlignment = VerticalAlignment.Center;
        }

        void RefreshArtwork()
        {
            var bytes = media.ArtworkBytes;
            if (bytes is null || bytes.Length == 0) { artwork.Source = null; return; }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.EndInit();
                bmp.Freeze();
                artwork.Source = bmp;
            }
            catch { artwork.Source = null; }
        }

        System.ComponentModel.PropertyChangedEventHandler handler = (_, e) =>
            Dispatcher.BeginInvoke(() =>
            {
                title.Text = string.IsNullOrWhiteSpace(media.Title) ? "ничего не играет" : media.Title;
                artist.Text = string.IsNullOrWhiteSpace(media.Artist) ? media.SourceApp : media.Artist;
                play.Content = media.IsPlaying ? "❚❚" : "▶";
                if (e.PropertyName is nameof(media.ArtworkBytes) or null) RefreshArtwork();
            });

        media.PropertyChanged += handler;
        _mediaUnsubscribe = () => media.PropertyChanged -= handler;
        handler(this, new System.ComponentModel.PropertyChangedEventArgs(null));
        RefreshArtwork();

        // Позиция меняется часто, но обновлять раз в полсекунды достаточно:
        // полоса всё равно не читается быстрее.
        Timer(500, () =>
        {
            var total = media.Duration.TotalSeconds;
            var now = media.Position.TotalSeconds;
            progress.Value = total > 0 ? Math.Clamp(now / total * 100, 0, 100) : 0;

            static string Fmt(TimeSpan t) => t.TotalHours >= 1
                ? t.ToString(@"h\:mm\:ss")
                : t.ToString(@"m\:ss");

            timing.Text = total > 0 ? $"{Fmt(media.Position)} / {Fmt(media.Duration)}" : "";
        });

        Relayout();
        return root;
    }

    /// <summary>
    /// Система: точечные дорожки вместо «цифр точками».
    ///
    /// Маленький виджет — две полоски, по ним мгновенно видно «сколько занято».
    /// Большой — те же полоски плюс проценты, гигабайты и батарея: размер
    /// добавляет информации, а не пустого места.
    /// </summary>
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

        var cpuLabel = Label("CPU");
        var ramLabel = Label("RAM");
        var cpuBar = new DotBar { Height = 12 };
        var ramBar = new DotBar { Height = 12 };

        var details = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = ResBrush("DsTextMuted"),
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        void Refresh()
        {
            int ramPct = stats.TotalMemoryMb > 0
                ? (int)Math.Round((double)stats.UsedMemoryMb / stats.TotalMemoryMb * 100)
                : 0;

            cpuBar.Value = Math.Clamp(stats.Cpu / 100.0, 0, 1);
            ramBar.Value = ramPct / 100.0;

            cpuLabel.Text = $"CPU · {stats.Cpu:F0}%";
            ramLabel.Text = $"RAM · {ramPct}%";

            var battery = stats.HasBattery
                ? $"\nбатарея: {stats.Battery}%{(stats.Charging ? " · заряжается" : "")}"
                : string.Empty;

            details.Text = $"процессор {stats.Cpu:F0}%\n" +
                           $"память {stats.UsedMemoryMb} из {stats.TotalMemoryMb} МБ · {ramPct}%" +
                           battery;
        }
        Refresh();
        Timer(2000, Refresh);

        var panel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(cpuLabel);
        panel.Children.Add(cpuBar);
        ramLabel.Margin = new Thickness(0, 10, 0, 3);
        panel.Children.Add(ramLabel);
        panel.Children.Add(ramBar);
        panel.Children.Add(details);

        _relayout = (w, h) => details.Visibility = h >= 130 ? Visibility.Visible : Visibility.Collapsed;

        return panel;
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

                Dispatcher.BeginInvoke(() =>
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

            Dispatcher.BeginInvoke(() =>
            {
                caption.Text = parts.Count > 0
                    ? $"ЦБ РФ · за {(days == 7 ? "неделю" : days == 30 ? "месяц" : "квартал")}: " + string.Join(" · ", parts)
                    : "курс недоступен — нет связи с ЦБ";
            });
        }

        BuildPeriod();

        // Правый клик — выбор второй валюты (доллар остаётся первым).
        var currencyMenu = CurrencyMenu(() => { });
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
    private Brush ResBrush(string key) => TryFindResource(key) as Brush ?? Brushes.White;
}

