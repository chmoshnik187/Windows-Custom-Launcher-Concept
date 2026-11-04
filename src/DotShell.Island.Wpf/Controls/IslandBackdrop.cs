namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using DotShell.Core.Audio;
using System.Windows.Threading;

/// <summary>
/// «Материя» острова: очень тихий рисунок акцентным цветом ПОД содержимым.
///
/// Зачем: чёрная капсула с одним акцентом выглядела плоско — как выключенный
/// экран с текстом. Плоскость лечится не картинкой, а материалом: мягкий
/// градиент, волны, топографические кольца — всё в цвете акцента и всё
/// настолько тихое, что текст и точки читаются поверх без борьбы.
///
/// Рисунок процедурный (никаких картинок в архиве), поэтому он сам подхватывает
/// цвет из обоев: сменились обои — сменился и закат внутри капсулы.
/// </summary>
public sealed class IslandBackdrop : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private double _phase;
    private DrawingGroup? _cachedGrid;
    private Size _cachedGridSize;

    private string _style = "waves";
    private double _strength = 0.35;
    private double _corner = 22;

    /// <summary>Имя рисунка: none / waves / topo / rays / aurora / grid.</summary>
    public string Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            SyncTimer();
            InvalidateVisual();
        }
    }

    /// <summary>Плотность рисунка 0..1 (ползунок в ТЕМЕ).</summary>
    public double Strength
    {
        get => _strength;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(_strength - value) < 0.001) return;
            _strength = value;
            InvalidateVisual();
        }
    }

    /// <summary>Радиус скругления, под который режется рисунок.</summary>
    public double Corner
    {
        get => _corner;
        set { _corner = value; InvalidateVisual(); }
    }

    public IslandBackdrop()
    {
        IsHitTestVisible = false;

        // 12 кадров в секунду: волны должны дышать, а не жрать процессор.
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        _timer.Tick += (_, _) =>
        {
            _phase += 0.05;
            InvalidateVisual();
        };

        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
        Loaded += (_, _) => StartPulseLoop();
        SizeChanged += (_, _) => InvalidateVisual();
        Unloaded += (_, _) => _timer.Stop();
        Loaded += (_, _) => SyncTimer();
    }

    private void SyncTimer()
    {
        bool animate = _style is "waves" or "rays";
        if (animate && !_timer.IsEnabled) _timer.Start();
        else if (!animate && _timer.IsEnabled) _timer.Stop();
    }

    // Дыхание под музыку: нижние полосы спектра двигают яркость рисунка.
    // Тема остаётся темой, меняется только её интенсивность — как индикатор
    // уровня в железе, а не «цветомузыка».
    private EventHandler? _pulseFrame;
    private DateTime _lastPulse = DateTime.MinValue;

    /// <summary>Реагировать ли яркостью на низкие частоты.</summary>
    public bool PulseWithAudio { get; set; } = true;

    private void StartPulseLoop()
    {
        if (_pulseFrame is not null) return;

        _pulseFrame = (_, _) =>
        {
            if (!PulseWithAudio) return;
            if (DateTime.UtcNow - _lastPulse < TimeSpan.FromMilliseconds(33)) return;
            _lastPulse = DateTime.UtcNow;

            if (AudioSpectrum.Current is { Bass: > 0.01f }) InvalidateVisual();
        };

        System.Windows.Media.CompositionTarget.Rendering += _pulseFrame;
    }

    protected override void OnRender(DrawingContext dc)
    {
        // Яркость рисунка пляшет с басом: 1.0 в тишине, до ~1.8 на ударе.
        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double strength = Math.Min(1.8, _strength * (1 + bass * 0.75));

        if (ActualWidth < 8 || ActualHeight < 8) return;
        // В высококонтрастной теме Windows любой рисунок только мешает читать текст.
        if (SystemParameters.HighContrast) return;

        if (_style == "none" || strength <= 0.01) return;

        var accent = ColorPalette.Accent;
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);

        dc.PushClip(new RectangleGeometry(rect, _corner, _corner));

        // Базовая «материя»: мягкое свечение сверху. Даже без рисунка она
        // убирает ощущение крашеного в чёрный прямоугольника.
        var wash = new LinearGradientBrush(
            WithAlpha(accent, (byte)(14 * strength)),
            WithAlpha(accent, (byte)(4 * strength)),
            90);
        wash.Freeze();
        dc.DrawRectangle(wash, null, rect);

        switch (_style)
        {
            case "waves": DrawWaves(dc, rect, accent); break;
            case "topo": DrawTopo(dc, rect, accent); break;
            case "rays": DrawRays(dc, rect, accent); break;
            case "aurora": DrawAurora(dc, rect, accent); break;
            case "grid": DrawGrid(dc, rect, accent); break;
            case "dots": DrawDotField(dc, rect, accent); break;
        }

        dc.Pop();
    }

    // ------------------------------------------------------------------
    //  Волны заката: несколько горизонтальных линий с медленным дыханием
    // ------------------------------------------------------------------

    private void DrawWaves(DrawingContext dc, Rect rect, Color accent)
    {
        double w = rect.Width, h = rect.Height;

        int lines = Math.Max(3, (int)(h / 26));
        for (int i = 0; i < lines; i++)
        {
            double t = (double)i / Math.Max(1, lines - 1);
            double baseY = h * (0.22 + t * 0.74);
            double amp = 2.5 + t * 7;

            var pen = new Pen(new SolidColorBrush(WithAlpha(accent, (byte)(58 * _strength * (0.45 + t * 0.55)))), 1);
            pen.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(-4, baseY), false, false);
                for (double x = -4; x <= w + 4; x += 7)
                {
                    double y = baseY
                               + Math.Sin(x * 0.013 + _phase * (0.6 + t) + i * 0.7) * amp
                               + Math.Sin(x * 0.0041 - _phase * 0.5 + i * 1.9) * amp * 0.7;
                    ctx.LineTo(new Point(x, y), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    // ------------------------------------------------------------------
    //  Топографические кольца: как карта высот, но в акценте
    // ------------------------------------------------------------------

    private void DrawTopo(DrawingContext dc, Rect rect, Color accent)
    {
        double w = rect.Width, h = rect.Height;
        // Центр внутри капсулы, а не за экраном: кольца должны читаться как
        // карта высот, а не как случайная дуга в углу.
        var center = new Point(w * 0.78, h * 0.82);
        double unit = Math.Min(w, h) * 0.16;

        for (int ring = 1; ring <= 6; ring++)
        {
            double radius = ring * unit;
            var pen = new Pen(
                new SolidColorBrush(WithAlpha(accent, (byte)(58 * _strength * (1.05 - ring * 0.1)))),
                1);
            pen.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int step = 0; step <= 64; step++)
                {
                    double angle = step / 64.0 * Math.PI * 2;
                    double wobble = 1
                        + 0.14 * Math.Sin(angle * 3 + ring * 0.9)
                        + 0.07 * Math.Sin(angle * 5 - ring * 1.7);
                    var point = new Point(
                        center.X + Math.Cos(angle) * radius * wobble * 1.5,
                        center.Y + Math.Sin(angle) * radius * wobble);

                    if (step == 0) ctx.BeginFigure(point, false, true);
                    else ctx.LineTo(point, true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }
    }

    // ------------------------------------------------------------------
    //  Лучи: диагональные полосы света, медленно ползущие
    // ------------------------------------------------------------------

    private void DrawRays(DrawingContext dc, Rect rect, Color accent)
    {
        double w = rect.Width, h = rect.Height;

        for (int i = 0; i < 5; i++)
        {
            double offset = (i * w / 5.0 + _phase * 9) % (w + h) - h;
            double width = 14 + i * 4;

            var brush = new LinearGradientBrush(
                WithAlpha(accent, 0),
                WithAlpha(accent, (byte)(34 * _strength)),
                new Point(0, 0),
                new Point(1, 0));
            brush.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(offset, 0), true, true);
                ctx.LineTo(new Point(offset + width, 0), true, false);
                ctx.LineTo(new Point(offset + width + h * 0.5, h), true, false);
                ctx.LineTo(new Point(offset + h * 0.5, h), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(brush, null, geo);
        }
    }

    // ------------------------------------------------------------------
    //  Аврора: два больших цветных пятна, которые дышат
    // ------------------------------------------------------------------

    private void DrawAurora(DrawingContext dc, Rect rect, Color accent)
    {
        double w = rect.Width, h = rect.Height;

        void Blob(double cx, double cy, double radius, double alpha)
        {
            var brush = new RadialGradientBrush(
                WithAlpha(accent, (byte)(alpha * _strength)),
                WithAlpha(accent, 0));
            brush.Freeze();
            dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius * 0.8);
        }

        double drift = Math.Sin(_phase * 0.7) * w * 0.05;
        Blob(w * 0.25 + drift, h * 0.15, Math.Max(w, h) * 0.5, 52);
        Blob(w * 0.8 - drift, h * 0.85, Math.Max(w, h) * 0.42, 40);
    }

    // ------------------------------------------------------------------
    //  Точечное поле: родной язык Nothing — точки по строгой сетке
    // ------------------------------------------------------------------

    private void DrawDotField(DrawingContext dc, Rect rect, Color accent)
    {
        const double pitch = 7;
        var brush = new SolidColorBrush(WithAlpha(accent, (byte)(120 * _strength)));
        brush.Freeze();

        // Сетка не «на глазок»: начинаем от левого верхнего угла и держим шаг,
        // поэтому поле читается как миллиметровка, а не как шум.
        for (double y = pitch / 2; y < rect.Height; y += pitch)
            for (double x = pitch / 2; x < rect.Width; x += pitch)
                dc.DrawRectangle(brush, null, new Rect(x, y, 1.2, 1.2));
    }

    // ------------------------------------------------------------------
    //  Точечная сетка: тот же язык, что у часов, но еле заметный
    // ------------------------------------------------------------------

    private void DrawGrid(DrawingContext dc, Rect rect, Color accent)
    {
        double step = 9;
        if (_cachedGrid is null || _cachedGridSize != rect.Size)
        {
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                var dot = new SolidColorBrush(WithAlpha(accent, 255));
                dot.Freeze();
                for (double y = step; y < rect.Height; y += step)
                    for (double x = step; x < rect.Width; x += step)
                        ctx.DrawRectangle(dot, null, new Rect(x, y, 1, 1));
            }
            group.Freeze();
            _cachedGrid = group;
            _cachedGridSize = rect.Size;
        }

        // Прозрачность задаём слоем, чтобы не пересобирать сетку на каждый тик.
        dc.PushOpacity(0.42 * _strength);
        dc.DrawDrawing(_cachedGrid);
        dc.Pop();
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb(alpha, color.R, color.G, color.B);
}

