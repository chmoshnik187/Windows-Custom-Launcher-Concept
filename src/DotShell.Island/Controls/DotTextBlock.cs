namespace DotShell.Island.Controls;

using System.Runtime.InteropServices.WindowsRuntime;
using DotShell.Core.DotMatrix;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.UI;

/// <summary>
/// ТОТ САМЫЙ КОНТРОЛ, ради которого всё затевалось.
///
/// Показывает ЛЮБОЙ текст в виде сетки точек. Как он это делает:
///   1. Внутри спрятан обычный TextBlock (прозрачный, Opacity=0).
///      Он нужен только как "печка": WinUI сам растеризует текст любым шрифтом.
///   2. RenderTargetBitmap фотографирует этот TextBlock в массив пикселей.
///   3. DotGrid.Sample() превращает пиксели в сетку яркостей (ячейка = точка).
///   4. DotGrid.Rasterize() рисует из яркостей кружки в новый битмап.
///   5. Битмап кладётся в Image.
///
/// Почему так, а не "встроенный точечный шрифт":
/// этот конвейер работает с ЧЕМ УГОДНО. Тот же DotGrid.Sample, только вместо
/// текста подаёшь иконку приложения или обложку альбома — и получаешь их в точках.
/// Одна реализация на всю эстетику продукта.
///
/// Цена: RenderTargetBitmap — не бесплатная операция (единицы миллисекунд).
/// Для часов раз в минуту и для названия трека при смене трека — вообще не заметно.
/// Для анимаций на 60 fps так делать НЕ надо — там нужен Win2D или pixel shader.
/// </summary>
public sealed class DotTextBlock : Control
{
    private Image? _dotsImage;
    private TextBlock? _sourceText;
    private bool _busy;
    private string _lastSignature = string.Empty;

    // ---------- свойства ----------

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(DotTextBlock),
        new PropertyMetadata(string.Empty, OnPropertyChanged));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }
    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(DotTextBlock),
        new PropertyMetadata(4.0, OnPropertyChanged));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(DotTextBlock),
        new PropertyMetadata(2.0, OnPropertyChanged));

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }
    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(DotTextBlock),
        new PropertyMetadata(Colors.White, OnPropertyChanged));

    /// <summary>Цвет "погасших" точек сетки. Transparent = не рисовать сетку.</summary>
    public Color GhostColor
    {
        get => (Color)GetValue(GhostColorProperty);
        set => SetValue(GhostColorProperty, value);
    }
    public static readonly DependencyProperty GhostColorProperty = DependencyProperty.Register(
        nameof(GhostColor), typeof(Color), typeof(DotTextBlock),
        new PropertyMetadata(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), OnPropertyChanged));

    public double SourceFontSize
    {
        get => (double)GetValue(SourceFontSizeProperty);
        set => SetValue(SourceFontSizeProperty, value);
    }
    public static readonly DependencyProperty SourceFontSizeProperty = DependencyProperty.Register(
        nameof(SourceFontSize), typeof(double), typeof(DotTextBlock),
        new PropertyMetadata(22.0, OnPropertyChanged));

    public FontFamily SourceFontFamily
    {
        get => (FontFamily)GetValue(SourceFontFamilyProperty);
        set => SetValue(SourceFontFamilyProperty, value);
    }
    public static readonly DependencyProperty SourceFontFamilyProperty = DependencyProperty.Register(
        nameof(SourceFontFamily), typeof(FontFamily), typeof(DotTextBlock),
        new PropertyMetadata(new FontFamily("Cascadia Mono, Consolas, Segoe UI"), OnPropertyChanged));

    public FontWeight SourceFontWeight
    {
        get => (FontWeight)GetValue(SourceFontWeightProperty);
        set => SetValue(SourceFontWeightProperty, value);
    }
    public static readonly DependencyProperty SourceFontWeightProperty = DependencyProperty.Register(
        nameof(SourceFontWeight), typeof(FontWeight), typeof(DotTextBlock),
        new PropertyMetadata(FontWeights.SemiBold, OnPropertyChanged));

    /// <summary>
    /// Принудительная ширина сетки в ячейках. Нужно для часов:
    /// "11" узкое, "00" широкое — без фиксированной ширины цифры будут прыгать.
    /// 0 = по факту текста.
    /// </summary>
    public int FixedColumns
    {
        get => (int)GetValue(FixedColumnsProperty);
        set => SetValue(FixedColumnsProperty, value);
    }
    public static readonly DependencyProperty FixedColumnsProperty = DependencyProperty.Register(
        nameof(FixedColumns), typeof(int), typeof(DotTextBlock),
        new PropertyMetadata(0, OnPropertyChanged));

    /// <summary>Реальная ширина сетки в логических px после последней отрисовки.</summary>
    public double ContentWidth { get; private set; }

    /// <summary>Реальная высота сетки в логических px после последней отрисовки.</summary>
    public double ContentHeight { get; private set; }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DotTextBlock self) self.RequestRender();
    }

    public DotTextBlock()
    {
        DefaultStyleKey = typeof(DotTextBlock);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _dotsImage = GetTemplateChild("PART_Dots") as Image;
        _sourceText = GetTemplateChild("PART_Source") as TextBlock;
        RequestRender();
    }

    public void RequestRender()
    {
        // Считаем "подпись" того, что должно быть нарисовано. Если она не изменилась —
        // не перерисовываем. Это защита от лишней работы при каждом чихе layout'а.
        var signature = $"{Text}|{CellSize}|{Gap}|{SourceFontSize}|{FixedColumns}|{DotColor}|{GhostColor}|{SourceFontFamily.Source}|{SourceFontWeight.Weight}";
        if (signature == _lastSignature) return;
        _lastSignature = signature;

        _ = RenderAsync();
    }

    private async Task RenderAsync()
    {
        if (_busy) return;
        if (_sourceText is null || _dotsImage is null) return;

        _busy = true;
        try
        {
            // Переносим свойства на скрытый TextBlock.
            _sourceText.Text = Text;
            _sourceText.FontSize = SourceFontSize;
            _sourceText.FontFamily = SourceFontFamily;
            _sourceText.FontWeight = SourceFontWeight;
            _sourceText.TextWrapping = TextWrapping.NoWrap;

            // Двух проходов не избежать: сначала узнаём размер текста, потом рисуем.
            // Первый проход — прикидочный (ширина ещё не зафиксирована).
            _sourceText.Width = double.NaN;
            _sourceText.Measure(new Size(10000, 1000));
            double textW = Math.Ceiling(_sourceText.DesiredSize.Width);
            double textH = Math.Ceiling(_sourceText.DesiredSize.Height);
            if (textW < 1 || textH < 1) return;

            // Сетка считается в ФИЗИЧЕСКИХ пикселях, иначе на масштабе 125/150%
            // точки упрутся в дробные координаты и станут разными по размеру.
            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            int cell = Math.Max(2, (int)Math.Round(CellSize * scale));
            int gap = (int)Math.Round(Gap * scale);
            int pitch = cell + gap;

            int cols = FixedColumns > 0
                ? FixedColumns
                : Math.Max(1, (int)Math.Ceiling(textW * scale / pitch));
            int rows = Math.Max(1, (int)Math.Ceiling(textH * scale / pitch));

            // Фиксируем размер источника и замеряем ещё раз уже по новой ширине:
            // иначе RenderTargetBitmap может отдать растр не того размера, который мы ждём.
            _sourceText.Width = cols * pitch / scale;
            _sourceText.Height = rows * pitch / scale;
            _sourceText.Measure(new Size(cols * pitch / scale, rows * pitch / scale));
            _sourceText.UpdateLayout();

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(_sourceText);

            int pixelW = rtb.PixelWidth;
            int pixelH = rtb.PixelHeight;
            if (pixelW <= 0 || pixelH <= 0) return;

            var buffer = await rtb.GetPixelsAsync();
            var pixels = buffer.ToArray();

            // Если масштаб экрана не 100%, фактический размер растра может отличаться
            // от расчётного — пересчитываем сетку по факту.
            var geo = new DotGrid.Geometry(cols, rows, cols * pitch, rows * pitch, pitch);
            if (pixelW != geo.Width || pixelH != geo.Height)
            {
                cols = Math.Max(1, pixelW / pitch);
                rows = Math.Max(1, pixelH / pitch);
                geo = new DotGrid.Geometry(cols, rows, cols * pitch, rows * pitch, pitch);
            }

            var grid = DotGrid.Sample(pixels, pixelW, pixelH, geo);

            uint on = ToArgb(DotColor);
            uint? ghost = GhostColor.A == 0 ? null : ToArgb(GhostColor);
            var dots = DotGrid.Rasterize(grid, geo, on, ghost);

            var wb = new WriteableBitmap(geo.Width, geo.Height);
            using (var stream = wb.PixelBuffer.AsStream())
                stream.Write(dots, 0, dots.Length);
            wb.Invalidate();

            _dotsImage.Source = wb;
            _dotsImage.Width = geo.Width / scale;
            _dotsImage.Height = geo.Height / scale;

            ContentWidth = geo.Width / scale;
            ContentHeight = geo.Height / scale;
        }
        catch (Exception ex)
        {
            Log.Info($"DotTextBlock render failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    private static uint ToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
