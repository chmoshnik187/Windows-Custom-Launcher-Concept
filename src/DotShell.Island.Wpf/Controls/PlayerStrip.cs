namespace DotShell.Island.Wpf.Controls;

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Theming;
using DotShell.Core.Media;

/// <summary>
/// Плеер острова. Один контрол — три раскладки, потому что «нравится/не нравится»
/// тут решается не цветом, а композицией:
///
///   cover  — обложка как герой: крупный квадрат, справа текст и управление;
///   dots   — название трека набрано ТОЧКАМИ (родной язык Nothing);
///   row    — компактная строка: всё в одну линию, высота 46.
///
/// Раскладка переключается в ТЕМЕ и живёт в settings.json (`PlayerStyle`).
/// Контрол сам слушает MediaBridge, поэтому окну больше не нужно таскать
/// картинку, таймкод и обработчики обложки.
/// </summary>
public sealed class PlayerStrip : Grid
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Display = new("Bahnschrift");

    private readonly MediaBridge _media;
    private readonly DispatcherTimer _timer;

    private string _style = "cover";

    private TextBlock? _title;
    private DotText? _dots;
    private DotText? _artistDots;
    private TextBlock? _artist;
    private TextBlock? _clock;
    private DotSeekBar? _progress;
    private Image? _art;
    private Button? _play;

    /// <summary>Кнопки, покрашенные акцентом вручную: их цвет надо обновлять.</summary>
    private readonly List<Button> _accentButtons = new();

    /// <summary>Раскладка: cover | dots | row.</summary>
    public string Style
    {
        get => _style;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? "cover" : value;
            if (_style == next) return;
            _style = next;
            Rebuild();
            Refresh();
        }
    }

    public PlayerStrip()
    {
        _media = ShellServices.Instance.Media;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => RefreshProgress();

        // Стиль таймлайна из настроек: витрина (--slider-demo) показывает варианты.
        if (_progress is not null)
        {
            _progress.Variant = ShellServices.Instance.Settings.Current.SeekStyle;
        }
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop();

        _media.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(Refresh);

        // Акцент меняется вместе с обоями (ThemeBus плавно ведёт цвет), а DotText
        // рисуется один раз и запоминает цвет. Раньше заголовок оставался стартовым
        // красным, сколько бы ни менялась тема. Слушаем палитру и перерисовываем —
        // но только при заметной разнице, иначе растеризация на каждый кадр съест CPU.
        ColorPalette.PaletteChanged += OnPaletteChanged;
        Unloaded += (_, _) => ColorPalette.PaletteChanged -= OnPaletteChanged;

        Rebuild();
        Refresh();
    }


    private Color _appliedDotColor = Colors.Transparent;

    private void OnPaletteChanged()
    {
        var accent = ColorPalette.Accent;
        if (Math.Abs(accent.R - _appliedDotColor.R) + Math.Abs(accent.G - _appliedDotColor.G) +
            Math.Abs(accent.B - _appliedDotColor.B) < 10) return;

        Dispatcher.BeginInvoke(() =>
        {
            if (_dots is null) return;

            _appliedDotColor = accent;
            _dots.DotColor = _coverAccent ?? accent;
            _dots.RequestRender();

            // Кнопки красились СНИМКОМ кисти при создании — пауза оставалась
            // стартово-красной, сколько бы ни менялся акцент.
            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();
            foreach (var accentButton in _accentButtons) accentButton.Foreground = accentBrush;
        });
    }
    // ------------------------------------------------------------------
    //  Раскладки
    // ------------------------------------------------------------------

    private void Rebuild()
    {
        Children.Clear();
        ColumnDefinitions.Clear();
        RowDefinitions.Clear();

        _title = null;
        _dots = null;
        _artistDots = null;
        _artist = null;
        _clock = null;
        _progress = null;
        _art = null;
        _play = null;

        switch (_style)
        {
            case "dots": BuildDots(); break;
            case "row": BuildRow(); break;
            default: BuildCover(); break;
        }
    }

    /// <summary>
    /// Обложка-герой + название точками: крупный арт слева, справа заголовок
    /// точками, под ним одна строка управления.
    ///
    /// Почему так: раньше транспорт висел у правого края, а между ним и текстом
    /// оставалась пустая полоса. Теперь кнопки стоят прямо под текстом, а
    /// прогресс тянется до правого края — пустоты нет, рука и глаз рядом.
    /// </summary>
    private void BuildCover()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        bool arcStyle = ShellServices.Instance.Settings.Current.SeekStyle == "arc";

        if (arcStyle)
        {
            // Стиль 8+10: обложка внутри кольца, кольцо — и прогресс, и пульс.
            var cover = Artwork(96);
            cover.CornerRadius = new CornerRadius(48);
            cover.VerticalAlignment = VerticalAlignment.Center;
            cover.Margin = new Thickness(6);

            var ring = new DotSeekBar
            {
                Variant = "arc",
                Width = 112,
                Height = 112,
                ShowTimeHint = false,
                VerticalAlignment = VerticalAlignment.Center,
            };

            ring.SeekRequested += fraction =>
                _ = _media.SeekAsync(TimeSpan.FromSeconds(_media.Duration.TotalSeconds * fraction));

            _progress = ring;

            var stack = new Grid { Width = 112, Height = 112, VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(cover);
            stack.Children.Add(ring);

            SetColumn(stack, 0);
            Children.Add(stack);
        }
        else
        {
            var art = Artwork(96);
            art.VerticalAlignment = VerticalAlignment.Stretch;
            SetColumn(art, 0);
            Children.Add(art);
        }

        var content = new Grid { Margin = new Thickness(16, 2, 0, 2), VerticalAlignment = VerticalAlignment.Center };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        content.Children.Add(Micro("СЕЙЧАС ИГРАЕТ", 8, 0.3, "DsTextMuted", new Thickness(0, 0, 0, 2)));

        // Строка названия: слева точки, справа — транспорт на том же уровне.
        // Так кнопки оказываются «в строке заголовка», а не отдельным островом
        // у правого края, и взгляд не прыгает через пустое поле.
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _dots = new DotText
        {
            // Волна: заголовок читается как подсвеченный, а не однотонный.
            Wave = 0.55,
            SourceFontSize = 32,
            CellSize = 2,
            Gap = 1,
            // Колонки считаются по длине строки в SetTrack: при жёстких 90
            // названия короче «РАЗБИВАЙ М» обрезались на середине слова.
            FixedColumns = 24,
            Height = 50,
            DotColor = ColorPalette.Accent,
            GhostColor = Colors.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleRow.Children.Add(_dots);

        var transport = Transport(40, 50);
        transport.VerticalAlignment = VerticalAlignment.Center;
        transport.HorizontalAlignment = HorizontalAlignment.Right;
        transport.Margin = new Thickness(8, 0, -6, 0);
        SetColumn(transport, 1);
        titleRow.Children.Add(transport);

        SetRow(titleRow, 1);
        content.Children.Add(titleRow);

        // Исполнитель. Три варианта — потому что точечная матрица на мелком
        // кегле упирается в физику: точек на букву мало, и знак «замыливается».
        // fine — мельче ячейка, точек на знак больше, поэтому читается лучше.
        // Исполнитель — обычным текстом: точками он читался хуже, чем название,
        // и это было верное замечание. Точки остаются привилегией заголовка.
        _artist = new TextBlock
        {
            FontFamily = Display,
            FontSize = 11,
            Foreground = Res("DsTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0),
        };
        SetRow(_artist, 2);
        content.Children.Add(_artist);
        // Под текстом — только линия прогресса и таймкод, во всю ширину.
        var line = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (!arcStyle)
        {
            _progress = Progress();
            _progress.Margin = new Thickness(0, 0, 10, 0);
            _progress.VerticalAlignment = VerticalAlignment.Center;
            line.Children.Add(_progress);
        }

        _clock = Clock(9);
        SetColumn(_clock, 1);
        line.Children.Add(_clock);

        SetRow(line, 3);
        content.Children.Add(line);
        SetColumn(content, 1);
        Children.Add(content);

        SizeChanged += (_, _) => SyncDotColumns();
    }

    /// <summary>
    /// Сколько точек влезает по ширине: DotText умеет рендерить только
    /// фиксированную сетку, поэтому считаем колонки от реального места.
    /// Так название не уезжает за край и не оставляет пустую половину.
    /// </summary>
    private void SyncDotColumns()
    {
        if (_dots is null) return;

        double artWidth = Children.Count > 0 && Children[0] is FrameworkElement art ? art.Width : 104;
        double available = ActualWidth - artWidth - 16 - 8;
        if (available < 80) return;

        double pitch = Math.Max(1, _dots.CellSize + _dots.Gap);
        int columns = (int)Math.Floor(available / pitch);
        columns = Math.Clamp(columns, 20, 140);

        if (_dots.FixedColumns != columns)
        {
            _dots.FixedColumns = columns;
            _dots.RequestRender();
        }
    }
    /// <summary>Название точками: тяжёлый, «машинный» заголовок.</summary>
    private void BuildDots()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var art = Artwork(56);
        SetColumn(art, 0);
        Children.Add(art);

        var text = new StackPanel { Margin = new Thickness(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };

        _dots = new DotText
        {
            // Волна: заголовок читается как подсвеченный, а не однотонный.
            Wave = 0.55,
            SourceFontSize = 28,
            FixedColumns = 60,
            CellSize = 2,
            Gap = 1,
            Width = 180,
            Height = 40,
            DotColor = ColorPalette.Accent,
            GhostColor = Colors.Transparent,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        text.Children.Add(_dots);

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        _artist = new TextBlock
        {
            FontFamily = Display,
            FontSize = 11,
            Foreground = Res("DsTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        meta.Children.Add(_artist);

        _clock = Clock(9);
        _clock.Margin = new Thickness(10, 0, 0, 0);
        meta.Children.Add(_clock);
        text.Children.Add(meta);

        SetColumn(text, 1);
        Children.Add(text);

        var transport = Transport(40, 50);
        SetColumn(transport, 2);
        Children.Add(transport);

        _progress = Progress();
        _progress.Margin = new Thickness(0, 10, 0, 0);
        SetRow(_progress, 1);
        SetColumnSpan(_progress, 3);
        Children.Add(_progress);
    }
    /// <summary>Одна строка: обложка, «трек · артист», таймкод, транспорт.</summary>
    private void BuildRow()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var art = Artwork(38);
        art.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(art, 0);
        Children.Add(art);

        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
        };
        _title = new TextBlock
        {
            FontFamily = Display,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("DsText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 260,
        };
        line.Children.Add(_title);

        var dot = new TextBlock
        {
            Text = "  ·  ",
            FontFamily = Display,
            FontSize = 12,
            Foreground = Res("DsTextMuted"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(dot);

        _artist = new TextBlock
        {
            FontFamily = Display,
            FontSize = 11,
            Foreground = Res("DsTextMuted"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 200,
            VerticalAlignment = VerticalAlignment.Center,
        };
        line.Children.Add(_artist);

        SetColumn(line, 1);
        Children.Add(line);

        _clock = Clock(9);
        _clock.VerticalAlignment = VerticalAlignment.Center;
        _clock.Margin = new Thickness(0, 0, 8, 0);
        SetColumn(_clock, 2);
        Children.Add(_clock);

        var transport = Transport(34);
        transport.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(transport, 3);
        Children.Add(transport);

        _progress = Progress();
        _progress.Margin = new Thickness(0, 8, 0, 0);
        SetRow(_progress, 1);
        SetColumnSpan(_progress, 4);
        Children.Add(_progress);
    }

    /// <summary>
    /// Сколько колонок нужно точечному тексту: DotText умеет только
    /// фиксированную сетку, поэтому считаем от длины строки и кегля.
    /// </summary>
    private static int DotColumnsFor(string text, double emSize)
    {
        if (string.IsNullOrEmpty(text)) return 8;
        double estimated = text.Length * emSize * 0.62;
        int columns = (int)Math.Ceiling(estimated / 3.0);
        return Math.Clamp(columns, 8, 220);
    }
    /// <summary>Белый из палитры — цвет подписи-исполнителя.</summary>
/// <summary>Белый с лёгкой прозрачностью: подпись не должна спорить с названием.</summary>
    private static Color FadedWhite()
    {
        var c = WhiteColor();
        return Color.FromArgb(210, c.R, c.G, c.B);
    }

    private static Color WhiteColor() =>
        (Application.Current?.TryFindResource("DsText") as SolidColorBrush)?.Color ?? Colors.White;
    // ------------------------------------------------------------------
    //  Кирпичи
    // ------------------------------------------------------------------

    private Border Artwork(double size)
    {
        _art = new Image { Stretch = Stretch.UniformToFill };
        // Форма обложки из настроек: квадрат, скругление или круг.
        string shape = ShellServices.Instance.Settings.Current.CoverShape;
        double radius = shape switch
        {
            "square" => 0,
            "circle" => size / 2,
            _ => Math.Max(2, size * 0.08),
        };

        var box = new Border
        {
            Width = size,
            Height = size,
            Background = Res("DsSurfaceAlt"),
            ClipToBounds = true,
            CornerRadius = new CornerRadius(radius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.Child = _art;
        RefreshArtwork();
        return box;
    }

    private TrackedText Micro(string text, double size, double tracking, string brushKey, Thickness margin)
    {
        var control = new TrackedText
        {
            Text = text,
            FontSize = size,
            Tracking = tracking,
            Brush = Res(brushKey),
            Margin = margin,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        return control;
    }

    /// <summary>
    /// Полоса перемотки. Раньше здесь стоял ProgressBar: он только показывает
    /// и не принимает ни клика, ни перетаскивания — со стороны выглядел как
    /// сломанный ползунок. Теперь DotSeekBar.
    /// </summary>
    private DotSeekBar Progress()
    {
        var bar = new DotSeekBar { VerticalAlignment = VerticalAlignment.Center };
        bar.SeekRequested += fraction =>
        {
            double total = _media.Duration.TotalSeconds;
            if (total <= 0) return;
            double target = total * fraction;
            Log.Info($"seek: {fraction:P0} → {target:F0} с из {total:F0}");
            _ = _media.SeekAsync(TimeSpan.FromSeconds(target));
        };
        return bar;
    }

    private TextBlock Clock(double size) => new()
    {
        FontFamily = Display,
        FontSize = size,
        Foreground = Res("DsTextMuted"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Транспорт: тонкие знаки без кружков и рамок.</summary>
    private StackPanel Transport(double playSize, double height = 0)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (height > 0) panel.Height = height;


        Button Btn(string glyph, bool accent, RoutedEventHandler onClick, double size)
        {
            var button = new Button
            {
                Content = glyph,
                FontFamily = IconFont,
                FontSize = size,
                Foreground = accent ? Res("DsAccent") : Res("DsTextMuted"),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Width = size + 16,
                Height = size + 16,
                Cursor = System.Windows.Input.Cursors.Hand,
                Focusable = false,
                Template = TransportTemplate(),
            };
            if (accent) _accentButtons.Add(button);
            button.Click += onClick;
            return button;
        }

        panel.Children.Add(Btn("\uE892", false, (_, _) => _ = _media.PreviousAsync(), 12));
        _play = Btn("\uE768", true, (_, _) => _ = _media.TogglePlayAsync(), playSize * 0.42);
        panel.Children.Add(_play);
        panel.Children.Add(Btn("\uE893", false, (_, _) => _ = _media.NextAsync(), 12));
        return panel;
    }

    private static ControlTemplate TransportTemplate()
    {
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

        var border = new FrameworkElementFactory(typeof(System.Windows.Controls.Border));
        border.SetValue(BackgroundProperty, Brushes.Transparent);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    // ------------------------------------------------------------------
    //  Обновление
    // ------------------------------------------------------------------

    private void Refresh()
    {
        var playing = _media.IsPlaying;

        if (_title is not null)
            _title.Text = string.IsNullOrWhiteSpace(_media.Title) ? "ничего не играет" : _media.Title;

        if (_dots is not null)
        {
            var title = string.IsNullOrWhiteSpace(_media.Title) ? "НЕТ ТРЕКА" : _media.Title.ToUpperInvariant();
            _dots.Text = title;

            // Колонки по фактической длине: иначе фиксированные 90 режут короткие
            // названия на середине слова, а длинные вылезают за блок.
            _dots.FixedColumns = DotColumnsFor(title, _dots.SourceFontSize);
            _dots.RequestRender();
        }

        var artist = string.IsNullOrWhiteSpace(_media.Artist) ? _media.SourceApp : _media.Artist;

        if (_artist is not null) _artist.Text = artist;

        if (_artistDots is not null)
        {
            _artistDots.Text = artist.ToUpperInvariant();
            _artistDots.FixedColumns = DotColumnsFor(artist, _artistDots.SourceFontSize);
            _artistDots.RequestRender();
        }

        if (_play is not null) _play.Content = playing ? "\uE769" : "\uE768";

        RefreshArtwork();
        RefreshProgress();
    }

    private void RefreshArtwork()
    {
        if (_art is null) return;

        var bytes = _media.ArtworkBytes;
        if (bytes is null || bytes.Length == 0) { _art.Source = null; return; }

        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _art.Source = image;
            ApplyCoverAccent(image);
        }
        catch { _art.Source = null; }
    }

    /// <summary>Цвет заголовка, взятый из обложки текущего трека (null — брать акцент темы).</summary>
    private Color? _coverAccent;

    /// <summary>
    /// Взять цвет из обложки: уменьшаем до 40 px и ищем характерный оттенок
    /// тем же ColorExtractor, что и обои. Если обложка серая или уверенность
    /// низкая — возвращаемся к акценту темы, иначе заголовок стал бы серым.
    /// </summary>
    private void ApplyCoverAccent(BitmapSource image)
    {
        try
        {
            var settings = ShellServices.Instance.Settings.Current;
            if (!settings.PlayerAccentFromCover)
            {
                _coverAccent = null;
                ApplyDotColor(ColorPalette.Accent);
                return;
            }

            double scale = Math.Min(1.0, 40.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);

            int w = converted.PixelWidth, h = converted.PixelHeight;
            if (w < 4 || h < 4) return;

            var buffer = new byte[w * h * 4];
            converted.CopyPixels(buffer, w * 4, 0);

            var result = ColorExtractor.FromBgra(buffer, w, h);
            if (result.Confidence < 0.12)
            {
                _coverAccent = null;
                ApplyDotColor(ColorPalette.Accent);
                return;
            }

            _coverAccent = NormalizeForBlack(ColorPalette.FromArgb(result.Accent));
            ApplyDotColor(_coverAccent.Value);
        }
        catch
        {
            _coverAccent = null;
        }
    }

    /// <summary>
    /// Подтянуть цвет обложки, чтобы он читался на чёрном.
    ///
    /// Тёмные обложки (а их большинство) дают почти чёрный оттенок: заголовок
    /// сливается с фоном. Поднимаем яркость до 0.62 и насыщенность до 0.45 —
    /// оттенок остаётся «своим», но текст видно.
    /// </summary>
    private static Color NormalizeForBlack(Color color)
    {
        var (h, s, v) = ColorExtractor.RgbToHsv(color.R, color.G, color.B);

        if (v < 0.62) v = 0.62;
        if (s < 0.45) s = 0.45;

        var (r, g, b) = ColorExtractor.HsvToRgb(h, s, v);
        return Color.FromRgb(r, g, b);
    }
    /// <summary>Пересчитать цвет заголовка заново (после переключения источника).</summary>
    public void RefreshThemeColors()
    {
        if (_art?.Source is BitmapSource current) ApplyCoverAccent(current);
        else
        {
            _coverAccent = null;
            ApplyDotColor(ColorPalette.Accent);
        }
    }
    /// <summary>Перекрасить точки заголовка (с перерисовкой матрицы).</summary>
    private void ApplyDotColor(Color color)
    {
        if (_dots is null) return;
        _dots.DotColor = color;
        _dots.RequestRender();
    }

    private void RefreshProgress()
    {
        if (_progress is null) return;

        double total = _media.Duration.TotalSeconds;
        double now = _media.LivePosition.TotalSeconds;
        _progress.Value = total > 0 ? Math.Clamp(now / total * 100, 0, 100) : 0;
        _progress.Duration = _media.Duration;


        if (_clock is not null)
            _clock.Text = total > 0 ? $"{Format(_media.LivePosition)} / {Format(_media.Duration)}" : string.Empty;

        static string Format(TimeSpan t) => t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"m\:ss");
    }
}





















