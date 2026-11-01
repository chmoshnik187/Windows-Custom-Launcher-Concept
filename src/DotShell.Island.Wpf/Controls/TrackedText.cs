namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;

/// <summary>
/// Текст с разрядкой (letter-spacing), которой в WPF нет.
///
/// Зачем: у Nothing весь микрокопирайт — крошечные прописные буквы с широкой
/// разрядкой («С Е Й Ч А С · И Г Р А Е Т»). Именно это, а не шрифт само по себе,
/// читается как технический интерфейс: мелкий кегль, воздух между знаками,
/// строгая сетка. Обычный TextBlock так не умеет — буквы липнут друг к другу.
///
/// Разрядка делается через GlyphRun: каждому знаку добавляется своя ширина,
/// поэтому подпись растягивается ровно, а не «на глазок» пробелами.
/// </summary>
public sealed class TrackedText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(nameof(Text), typeof(string), typeof(TrackedText),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    /// <summary>Воздух между знаками в долях кегля (0.22 ≈ 22 %).</summary>
    public static readonly DependencyProperty TrackingProperty =
        DependencyProperty.Register(nameof(Tracking), typeof(double), typeof(TrackedText),
            new FrameworkPropertyMetadata(0.22, FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public static readonly DependencyProperty FontSizeProperty =
        DependencyProperty.Register(nameof(FontSize), typeof(double), typeof(TrackedText),
            new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public static readonly DependencyProperty FontFamilyProperty =
        DependencyProperty.Register(nameof(FontFamily), typeof(FontFamily), typeof(TrackedText),
            new FrameworkPropertyMetadata(new FontFamily("Bahnschrift"),
                FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public static readonly DependencyProperty FontWeightProperty =
        DependencyProperty.Register(nameof(FontWeight), typeof(FontWeight), typeof(TrackedText),
            new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure, OnChanged));

    public static readonly DependencyProperty BrushProperty =
        DependencyProperty.Register(nameof(Brush), typeof(Brush), typeof(TrackedText),
            new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender, OnChanged));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double Tracking { get => (double)GetValue(TrackingProperty); set => SetValue(TrackingProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Brush { get => (Brush)GetValue(BrushProperty); set => SetValue(BrushProperty, value); }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TrackedText)d;
        control.InvalidateMeasure();
        control.InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var run = Build(out double width);
        double height = run is null ? FontSize : FontSize * 1.35;
        return new Size(Math.Ceiling(width) + 1, Math.Ceiling(height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var run = Build(out _);
        if (run is not null) dc.DrawGlyphRun(Brush, run);
    }

    private GlyphRun? Build(out double width)
    {
        width = 0;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        if (!typeface.TryGetGlyphTypeface(out var glyphTypeface)) return null;

        var text = Text ?? string.Empty;
        if (text.Length == 0) return null;

        var indices = new List<ushort>(text.Length);
        var advances = new List<double>(text.Length);
        double extra = FontSize * Tracking;
        double total = 0;

        foreach (var raw in text)
        {
            // Подписи всегда прописные — так требует сетка Nothing.
            var ch = char.ToUpperInvariant(raw);
            if (!glyphTypeface.CharacterToGlyphMap.TryGetValue(ch, out var glyph)) continue;

            indices.Add(glyph);
            var advance = glyphTypeface.AdvanceWidths[glyph] * FontSize + extra;
            advances.Add(advance);
            total += advance;
        }

        if (indices.Count == 0) return null;

        // Последняя литера без хвостового воздуха — иначе подпись «съезжает».
        advances[^1] -= extra;
        total -= extra;
        width = total;

        return new GlyphRun(
            glyphTypeface,
            bidiLevel: 0,
            isSideways: false,
            renderingEmSize: FontSize,
            pixelsPerDip: (float)VisualTreeHelper.GetDpi(this).PixelsPerDip,
            glyphIndices: indices,
            baselineOrigin: new Point(0, FontSize * 0.8),
            advanceWidths: advances,
            glyphOffsets: null,
            characters: null,
            deviceFontName: null,
            clusterMap: null,
            caretStops: null,
            language: XmlLanguage.GetLanguage("ru-RU"));
    }
}
