 namespace DotShell.Island.Wpf.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core.DotMatrix;

/// <summary>
/// Точечный абзац: текст с переносами строк, набранный точками.
///
/// Зачем отдельный контрол. <see cref="DotText"/> растягивает одну строку на всю
/// ширину: он годится для часов, заголовков и коротких меток, но не для ответа
/// модели — у абзаца нет одной строки. Здесь строки считаются сами (по словам),
/// каждая растеризуется в свою сетку, а точки рисуются столбиком.
///
/// Геометрия, из которой всё следует: точка живёт в ячейке размером CellSize с
/// зазором Gap, то есть минимальный шаг — 2 пикселя. Чтобы буква читалась, на неё
/// нужно 5–6 ячеек, а ширина символа — примерно 0.55 от кегля. Отсюда крупный
/// SourceFontSize (по умолчанию 20): при 11 px буквы сливаются в кашу — проверено.
/// </summary>
public sealed class DotParagraph : FrameworkElement
{
    // ---------------- свойства ----------------

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DotParagraph),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }
    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(DotParagraph),
        new FrameworkPropertyMetadata(2.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(DotParagraph),
        new FrameworkPropertyMetadata(1.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    /// <summary>Кегль исходного текста до превращения в точки. Меньше 16 — нечитаемо.</summary>
    public double SourceFontSize
    {
        get => (double)GetValue(SourceFontSizeProperty);
        set => SetValue(SourceFontSizeProperty, value);
    }
    public static readonly DependencyProperty SourceFontSizeProperty = DependencyProperty.Register(
        nameof(SourceFontSize), typeof(double), typeof(DotParagraph),
        new FrameworkPropertyMetadata(20.0,
            FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }
    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(DotParagraph),
        new FrameworkPropertyMetadata(Colors.White, OnChanged));

    public Color GhostColor
    {
        get => (Color)GetValue(GhostColorProperty);
        set => SetValue(GhostColorProperty, value);
    }
    public static readonly DependencyProperty GhostColorProperty = DependencyProperty.Register(
        nameof(GhostColor), typeof(Color), typeof(DotParagraph),
        new FrameworkPropertyMetadata(Color.FromArgb(0, 0, 0, 0), OnChanged));

    // ---------------- внутреннее ----------------

    private readonly List<BitmapSource> _lineBitmaps = new();
    private double _lineHeight;
    private double _width;
    private string _lastSignature = string.Empty;

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DotParagraph paragraph) paragraph.Rebuild();
    }

    public DotParagraph()
    {
        Loaded += (_, _) => Rebuild();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(Rebuild);
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected override Size MeasureOverride(Size availableSize)
    {
        double limit = double.IsInfinity(availableSize.Width) ? 560 : availableSize.Width;
        Rebuild(limit);
        return new Size(Math.Min(_width, limit), Math.Max(1, _lineBitmaps.Count * _lineHeight));
    }

    protected override void OnRender(DrawingContext dc)
    {
        double y = 0;
        foreach (var line in _lineBitmaps)
        {
            dc.DrawImage(line, new Rect(0, y, line.PixelWidth / Scale, line.PixelHeight / Scale));
            y += _lineHeight;
        }
    }

    private void Rebuild(double widthHint = -1)
    {
        if (string.IsNullOrWhiteSpace(Text)) { _lineBitmaps.Clear(); InvalidateMeasure(); return; }

        double limit = widthHint > 0 ? widthHint : (ActualWidth > 8 ? ActualWidth : 560);
        var signature = $"{Text}|{limit:0}|{CellSize}|{Gap}|{SourceFontSize}|{DotColor}|{GhostColor}";
        if (signature == _lastSignature) return;

        _lineBitmaps.Clear();
        _lastSignature = signature;

        try { Build(limit); }
        catch { _lineBitmaps.Clear(); }

        InvalidateMeasure();
        InvalidateVisual();
    }

    private void Build(double limit)
    {
        double scale = Scale;
        int cell = Math.Max(2, (int)Math.Round(CellSize * scale));
        int gap = (int)Math.Round(Gap * scale);
        int pitch = cell + gap;

        var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        int rows = Math.Max(3, (int)Math.Ceiling(SourceFontSize * scale * 1.35 / pitch));
        _lineHeight = rows * pitch / scale;

        foreach (var line in Wrap(Text, typeface, limit))
        {
            _lineBitmaps.Add(RenderLine(line, typeface, pitch, rows, scale));
        }

        _width = _lineBitmaps.Count == 0 ? 0 : _lineBitmaps.Max(b => b.PixelWidth) / scale;
    }

    /// <summary>Разбиение на строки по словам: перенос по ширине, а не по буквам.</summary>
    private IEnumerable<string> Wrap(string text, Typeface typeface, double limit)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var lines = new List<string>();

        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(" "); continue; }

            var current = new System.Text.StringBuilder();
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                var measured = new FormattedText(candidate, CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, SourceFontSize, Brushes.White, pixelsPerDip);

                if (measured.Width <= limit || current.Length == 0)
                {
                    current.Clear().Append(candidate);
                }
                else
                {
                    lines.Add(current.ToString());
                    current.Clear().Append(word);
                }
            }

            if (current.Length > 0) lines.Add(current.ToString());
        }

        return lines;
    }

    private BitmapSource RenderLine(string line, Typeface typeface, int pitch, int rows, double scale)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var formatted = new FormattedText(line, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, SourceFontSize, Brushes.White, pixelsPerDip);

        int cols = Math.Max(1, (int)Math.Ceiling(formatted.Width * scale / pitch));
        int pw = cols * pitch;
        int ph = rows * pitch;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawText(formatted, new Point(0, 0));
        }

        var bitmap = new RenderTargetBitmap(pw, ph, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var geometry = new DotGrid.Geometry(cols, rows, pw, ph, pitch);
        var grid = DotGrid.Sample(pixels, bitmap.PixelWidth, bitmap.PixelHeight, geometry);

        uint on = ColorPalette.ToArgb(DotColor);
        uint? ghost = GhostColor.A == 0 ? null : ColorPalette.ToArgb(GhostColor);
        var dots = DotGrid.Rasterize(grid, geometry, on, ghost);

        return BitmapSource.Create(geometry.Width, geometry.Height, 96 * scale, 96 * scale,
            PixelFormats.Pbgra32, null, dots, geometry.Width * 4);
    }
}
