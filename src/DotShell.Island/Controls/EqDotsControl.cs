namespace DotShell.Island.Controls;

using System.Runtime.InteropServices.WindowsRuntime;
using DotShell.Core.DotMatrix;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

/// <summary>
/// Эквалайзер из точек.
///
/// Здесь важно не "как получить данные об аудио" (это задача Фазы 2, где мы
/// подключаемся к Equalizer APO), а то, что визуализация уже работает на ТОМ ЖЕ
/// движке DotGrid, что и часы, и обложка, и иконки. То есть весь интерфейс
/// собран из одного кирпича — именно это и считывается как единый стиль Nothing.
///
/// Сейчас полосы живут на случайном блуждании (выглядит как настоящая музыка).
/// Когда подключим реальный анализатор, заменится только источник массива levels.
/// </summary>
public sealed class EqDotsControl : Control
{
    private Image? _canvas;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private readonly Random _random = new();
    private float[] _levels = new float[16];

    public Color DotColor
    {
        get => (Color)GetValue(DotColorProperty);
        set => SetValue(DotColorProperty, value);
    }
    public static readonly DependencyProperty DotColorProperty = DependencyProperty.Register(
        nameof(DotColor), typeof(Color), typeof(EqDotsControl),
        new PropertyMetadata(Colors.White, OnPropertyChanged));

    public Color GhostColor
    {
        get => (Color)GetValue(GhostColorProperty);
        set => SetValue(GhostColorProperty, value);
    }
    public static readonly DependencyProperty GhostColorProperty = DependencyProperty.Register(
        nameof(GhostColor), typeof(Color), typeof(EqDotsControl),
        new PropertyMetadata(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), OnPropertyChanged));

    public double CellSize
    {
        get => (double)GetValue(CellSizeProperty);
        set => SetValue(CellSizeProperty, value);
    }
    public static readonly DependencyProperty CellSizeProperty = DependencyProperty.Register(
        nameof(CellSize), typeof(double), typeof(EqDotsControl),
        new PropertyMetadata(4.0, OnPropertyChanged));

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(EqDotsControl),
        new PropertyMetadata(2.0, OnPropertyChanged));

    /// <summary>Стоп-кран: когда вкладка не видна, анимацию надо выключать. Иначе жжём CPU впустую.</summary>
    public bool IsRunning
    {
        get => _timer?.IsRunning ?? false;
        set
        {
            if (_timer is null) return;
            if (value && !_timer.IsRunning) _timer.Start();
            else if (!value && _timer.IsRunning) _timer.Stop();
        }
    }

    private static void OnPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EqDotsControl self) self.Render();
    }

    public EqDotsControl()
    {
        DefaultStyleKey = typeof(EqDotsControl);
        SizeChanged += (_, _) => Render();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _canvas = GetTemplateChild("PART_Canvas") as Image;

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(70);
        _timer.Tick += (_, _) => { Step(); Render(); };

        Step();
        Render();
    }

    private void Step()
    {
        for (int i = 0; i < _levels.Length; i++)
        {
            // Случайное блуждание с возвратом к центру + лёгкий "уклон" в низкие частоты,
            // чтобы картинка походила на реальную музыку, а не на шум.
            float bias = 1.0f - (i / (float)_levels.Length) * 0.45f;
            _levels[i] += (float)(_random.NextDouble() - 0.5) * 0.28f;
            _levels[i] = Math.Clamp(_levels[i] * 0.92f + 0.08f * bias, 0.05f, 1.0f);
        }
    }

    private void Render()
    {
        if (_canvas is null || ActualWidth < 4 || ActualHeight < 4) return;

        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        int cell = Math.Max(2, (int)Math.Round(CellSize * scale));
        int gap = (int)Math.Round(Gap * scale);
        int pitch = cell + gap;

        int cols = Math.Max(4, (int)(ActualWidth * scale / pitch));
        int rows = Math.Max(4, (int)(ActualHeight * scale / pitch));
        var geo = new DotGrid.Geometry(cols, rows, cols * pitch, rows * pitch, pitch);

        // Раскладываем 16 полос на cols ячеек и "зажигаем" снизу вверх.
        var brightness = new float[cols * rows];
        for (int c = 0; c < cols; c++)
        {
            int band = (int)((double)c / cols * _levels.Length);
            float level = _levels[Math.Clamp(band, 0, _levels.Length - 1)];
            int litRows = (int)Math.Round(level * rows);

            for (int r = 0; r < rows; r++)
            {
                // r=0 — верхняя строка, а растём снизу, поэтому переворачиваем индекс.
                int fromBottom = rows - 1 - r;
                if (fromBottom >= litRows) continue;

                // Верхняя точка столбца ярче — она "головка" полосы.
                bool isHead = fromBottom == litRows - 1;
                brightness[r * cols + c] = isHead ? 1.0f : 0.45f;
            }
        }

        uint on = ToArgb(DotColor);
        uint? ghost = GhostColor.A == 0 ? null : ToArgb(GhostColor);
        var pixels = DotGrid.Rasterize(brightness, geo, on, ghost);

        var wb = new WriteableBitmap(geo.Width, geo.Height);
        using (var stream = wb.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        wb.Invalidate();

        _canvas.Source = wb;
    }

    private static uint ToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
}
