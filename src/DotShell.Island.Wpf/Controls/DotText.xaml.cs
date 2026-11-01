namespace DotShell.Island.Wpf.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core.DotMatrix;

/// <summary>
/// ТОТ САМЫЙ КОНТРОЛ, ради которого всё затевалось: любой текст → сетка точек.
///
/// Конвейер:
///   1. Рисуем текст в RenderTargetBitmap любым шрифтом (FormattedText).
///   2. DotGrid.Sample() превращает пиксели в сетку яркостей (ячейка = точка).
///   3. DotGrid.Rasterize() рисует из яркостей кружки в новый битмап.
///   4. Битмап кладётся в Image.
///
/// Почему так, а не "встроенный точечный шрифт":
/// конвейер работает с ЧЕМ УГОДНО. Вместо текста подаёшь иконку приложения
/// или обложку альбома — и получаешь их в точках тем же кодом.
/// Одна реализация на всю эстетику продукта.
///
/// Цена: RenderTargetBitmap — не бесплатная операция (единицы миллисекунд).
/// Для часов раз в минуту и для названия трека при смене трека — незаметно.
/// Для анимаций 60 fps так делать НЕ надо, там нужен GPU.
/// </summary>
public partial class DotText : UserControl
{
    private static readonly Typeface[] TypefaceCandidates =
    {
        new("Consolas"),
        new("Cascadia Mono"),
        new("Segoe UI"),
        new("Tahoma")
    };

    private bool _rendering;
    private string _lastSignature = string.Empty;

    // ---------- свойства ----------

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DotText),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderInputChanged));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }
    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(DotText),
        new FrameworkPropertyMetadata(4.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderInputChanged));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(DotText),
        new FrameworkPropertyMetadata(2.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderInputChanged));

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }
    /// <summary>
    /// «Объём» точечного текста: 0 — ровный цвет, 1 — сильная волна.
    /// Яркость точки зависит от её места по горизонтали, поэтому заголовок
    /// читается как подсвеченная волна, а не как однотонная заливка.
    /// </summary>
    public double Wave
    {
        get => (double)GetValue(WaveProperty);
        set => SetValue(WaveProperty, value);
    }

    public static readonly DependencyProperty WaveProperty = DependencyProperty.Register(
        nameof(Wave), typeof(double), typeof(DotText),
        new PropertyMetadata(0.0, (d, _) => ((DotText)d).RequestRender()));
    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(DotText),
        new FrameworkPropertyMetadata(Colors.White, OnRenderInputChanged));

    /// <summary>Цвет "погасших" точек сетки. Transparent = сетку не рисовать.</summary>
    public Color GhostColor
    {
        get => (Color)GetValue(GhostColorProperty);
        set => SetValue(GhostColorProperty, value);
    }
    public static readonly DependencyProperty GhostColorProperty = DependencyProperty.Register(
        nameof(GhostColor), typeof(Color), typeof(DotText),
        new FrameworkPropertyMetadata(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), OnRenderInputChanged));

    public double SourceFontSize
    {
        get => (double)GetValue(SourceFontSizeProperty);
        set => SetValue(SourceFontSizeProperty, value);
    }
    public static readonly DependencyProperty SourceFontSizeProperty = DependencyProperty.Register(
        nameof(SourceFontSize), typeof(double), typeof(DotText),
        new FrameworkPropertyMetadata(24.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderInputChanged));

    /// <summary>
    /// Принудительная ширина сетки в ячейках. Нужно для часов:
    /// "11" узкое, "00" широкое — без фиксации ширины цифры будут прыгать.
    /// 0 = по факту текста.
    /// </summary>
    public int FixedColumns
    {
        get => (int)GetValue(FixedColumnsProperty);
        set => SetValue(FixedColumnsProperty, value);
    }
    public static readonly DependencyProperty FixedColumnsProperty = DependencyProperty.Register(
        nameof(FixedColumns), typeof(int), typeof(DotText),
        new FrameworkPropertyMetadata(0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnRenderInputChanged));

    /// <summary>Сколько ячеек влезло по ширине после последней отрисовки.</summary>
    public int Columns { get; private set; }

    /// <summary>Сколько ячеек по высоте после последней отрисовки.</summary>
    public int Rows { get; private set; }

    private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DotText self) self.RequestRender();
    }

    public DotText()
    {
        InitializeComponent();
        Loaded += (_, _) => RequestRender();
        IsVisibleChanged += (_, _) => RequestRender();
    }

    /// <summary>Перерисовать. Повторные вызовы с теми же входными данными игнорируются.</summary>
    public void RequestRender()
    {
        var signature = string.Join("|", Text, CellSize, Gap, SourceFontSize, FixedColumns,
            DotColor.ToString(), GhostColor.ToString());
        if (signature == _lastSignature && DotsImage.Source is not null) return;
        _lastSignature = signature;
        Render();
    }

    private void Render()
    {
        if (_rendering || DotsImage is null) return;
        if (string.IsNullOrEmpty(Text))
        {
            DotsImage.Source = null;
            return;
        }

        _rendering = true;
        try
        {
            double scale = GetDpiScale();

            int cellPx = Math.Max(2, (int)Math.Round(CellSize * scale));
            int gapPx = (int)Math.Round(Gap * scale);
            int pitch = cellPx + gapPx;

            // --- 1. измеряем текст ---
            var typeface = TypefaceCandidates[0];
            var formatted = new FormattedText(
                Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, SourceFontSize, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double textWidthDiu = Math.Ceiling(formatted.WidthIncludingTrailingWhitespace);
            double textHeightDiu = Math.Ceiling(formatted.Height);
            if (textWidthDiu < 1 || textHeightDiu < 1) return;

            int cols = FixedColumns > 0
                ? FixedColumns
                : Math.Max(1, (int)Math.Ceiling(textWidthDiu * scale / pitch));
            int rows = Math.Max(1, (int)Math.Ceiling(textHeightDiu * scale / pitch));

            int pw = cols * pitch;
            int ph = rows * pitch;

            // --- 2. растеризуем текст в битмап ---
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                // Перерисовываем текст уже под точный размер сетки,
                // иначе при FixedColumns он окажется не по центру ячейки.
                var ft = new FormattedText(
                    Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, SourceFontSize, Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip);
                dc.DrawText(ft, new Point(0, 0));
            }

            var rtb = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(visual);

            int stride = rtb.PixelWidth * 4;
            var pixels = new byte[stride * rtb.PixelHeight];
            rtb.CopyPixels(pixels, stride, 0);

            // --- 3. пиксели → сетка яркостей → кружки ---
            var geo = new DotGrid.Geometry(cols, rows, pw, ph, pitch);
            var grid = DotGrid.Sample(pixels, rtb.PixelWidth, rtb.PixelHeight, geo);

            uint on = ColorPalette.ToArgb(DotColor);
            uint? ghost = GhostColor.A == 0 ? null : ColorPalette.ToArgb(GhostColor);
            var dotPixels = DotGrid.Rasterize(grid, geo, on, ghost);

            // Волна: гасим точки по горизонтали синусом. Меняем только цвет,
            // альфу не трогаем — иначе точки начнут «просвечивать» и форма сломается.
            if (Wave > 0.01)
                ApplyWave(dotPixels, geo.Width, geo.Height, Wave);

            var bitmap = BitmapSource.Create(
                geo.Width, geo.Height, 96 * scale, 96 * scale,
                PixelFormats.Bgra32, null, dotPixels, geo.Width * 4);
            bitmap.Freeze(); // обязательно: иначе битмап привяжется к потоку и будет тормозить

            DotsImage.Source = bitmap;

            Columns = cols;
            Rows = rows;

            // Размер контрола — в логических единицах WPF.
            Width = geo.Width / scale;
            Height = geo.Height / scale;
        }
        catch (Exception ex)
        {
            Log.Info($"DotText render failed for '{Text}': {ex.Message}");
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>Синусоидальная яркость по колонкам: слева ярче, к центру гаснет и обратно.</summary>
    private static void ApplyWave(byte[] pixels, int width, int height, double strength)
    {
        double amplitude = Math.Clamp(strength, 0, 1) * 0.62;
        double cycles = Math.Max(2, width / 150.0);   // несколько волн по ширине, а не одна

        for (int y = 0; y < height; y++)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int pixel = row + x * 4;
                if (pixels[pixel + 3] == 0) continue;      // прозрачная точка — пропускаем

                double phase = (double)x / Math.Max(1, width) * Math.PI * 2 * cycles;
                double factor = 1.0 - amplitude * (0.5 + 0.5 * Math.Sin(phase));

                pixels[pixel + 0] = (byte)(pixels[pixel + 0] * factor);
                pixels[pixel + 1] = (byte)(pixels[pixel + 1] * factor);
                pixels[pixel + 2] = (byte)(pixels[pixel + 2] * factor);
            }
        }
    }
    protected override Size MeasureOverride(Size availableSize)
    {
        // Размер уже выставлен в Render(); здесь только подтверждаем его,
        // чтобы родитель не пытался растянуть контрол и не размазал точки.
        if (!double.IsNaN(Width) && !double.IsNaN(Height))
            return new Size(Width, Height);

        return base.MeasureOverride(availableSize);
    }

    private double GetDpiScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
                return source.CompositionTarget.TransformToDevice.M11;
        }
        catch { }
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }
}
