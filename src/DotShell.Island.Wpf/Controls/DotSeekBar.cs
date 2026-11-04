namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core.Audio;

/// <summary>
/// Полоса перемотки и вообще любой «ползунок» — один контрол с вариантами отрисовки.
///
/// Владелец выбрал из списка идей восемь стилей таймлайна (бусины, рельс с иголкой,
/// деления, волна, пески, дуга, пульс под бас, кассета) и три стиля ползунков
/// (капсула, блоки, точки с хвостом). Все они живут здесь: один контрол, разные
/// рендереры — так настройка «стиль» переключает и таймлайн, и громкость.
///
/// Тонкость, которая уже была и остаётся: пока идёт перетаскивание, внешнее
/// обновление прогресса не должно двигать точку — иначе она «вырывается» из-под курсора.
/// </summary>
public sealed class DotSeekBar : FrameworkElement
{
    private double _value;
    private string _variant = "beads";
    private bool _dragging;
    private bool _hover;

    public DotSeekBar()
    {
        Height = 22;
        Cursor = Cursors.Hand;
        SizeChanged += (_, _) => InvalidateVisual();
        MouseEnter += (_, _) => { _hover = true; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = false; InvalidateVisual(); };
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    /// <summary>Прогресс 0..100. Игнорируется, пока пользователь тянет ползунок.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (_dragging) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (Math.Abs(_value - clamped) < 0.2) return;
            _value = clamped;
            InvalidateVisual();
        }
    }

    /// <summary>Длительность трека: по ней считается время под курсором.</summary>
    public TimeSpan Duration { get; set; }

    /// <summary>Показывать всплывающее время под курсором (только для таймлайна).</summary>
    public bool ShowTimeHint { get; set; } = true;

    /// <summary>
    /// Стиль отрисовки. Таймлайн: beads, rail, segments, wave, sand, arc, pulse, cassette.
    /// Ползунки: capsule, blocks, dots.
    /// </summary>
    public string Variant
    {
        get => _variant;
        set
        {
            if (_variant == value) return;
            _variant = value;
            InvalidateVisual();
        }
    }

    /// <summary>Куда перемотать: доля 0..1.</summary>
    public event Action<double>? SeekRequested;

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 8) return;

        // Прозрачная подложка: без неё мышь ловится только по нарисованным точкам.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));

        double fraction = Math.Clamp(_value / 100, 0, 1);

        switch (_variant)
        {
            case "rail": DrawRail(dc, width, height, fraction); break;
            case "segments": DrawSegments(dc, width, height, fraction); break;
            case "wave": DrawWave(dc, width, height, fraction); break;
            case "sand": DrawSand(dc, width, height, fraction); break;
            case "arc": DrawArc(dc, width, height, fraction); break;
            case "cassette": DrawCassette(dc, width, height, fraction); break;
            case "capsule": DrawCapsule(dc, width, height, fraction); break;
            case "blocks": DrawBlocks(dc, width, height, fraction); break;
            case "dots": DrawDots(dc, width, height, fraction); break;
            case "pulse": DrawBeads(dc, width, height, fraction, pulse: true); break;
            default: DrawBeads(dc, width, height, fraction, pulse: false); break;
        }

        if (ShowTimeHint && (_hover || _dragging) && Duration > TimeSpan.Zero && width > 60 && _variant != "arc")
            DrawTimeHint(dc, width, height, fraction);
    }

    // ---------------------------------------------------------------- таймлайн

    /// <summary>Бусины: прошедшее — плотные точки, впереди — призрачные.</summary>
    private void DrawBeads(DrawingContext dc, double width, double height, double fraction, bool pulse)
    {
        double y = height / 2;
        const double step = 6;

        float bass = pulse ? AudioSpectrum.Current?.Bass ?? 0f : 0f;
        double radius = 1.6 + bass * 1.6;
        double head = 3.4 + bass * 2.2;

        var accent = AccentBrush();
        var ghost = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        ghost.Freeze();

        int count = (int)Math.Floor(width / step);
        for (int i = 0; i <= count; i++)
        {
            double x = i * step;
            bool played = x <= width * fraction;
            dc.DrawEllipse(played ? accent : ghost, null, new Point(x, y), played ? radius : 1.1, played ? radius : 1.1);
        }

        DrawHead(dc, width * fraction, y, head, bass);
    }

    /// <summary>Рельс и иголка: линия, прошедшее ярче, голова — вертикальная игла.</summary>
    private void DrawRail(DrawingContext dc, double width, double height, double fraction)
    {
        double y = height / 2;
        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        dc.DrawRectangle(muted, null, new Rect(0, y - 1, width, 2));
        dc.DrawRectangle(accent, null, new Rect(0, y - 1, width * fraction, 2));

        double x = width * fraction;
        double needle = _hover || _dragging ? 13 : 9;

        dc.DrawRectangle(accent, null, new Rect(x - 1, y - needle / 2, 2, needle));
        dc.DrawEllipse(accent, null, new Point(x, y), 2.6, 2.6);
    }

    /// <summary>Деления ресивера: 24 сегмента, прошедшие акцентом.</summary>
    private void DrawSegments(DrawingContext dc, double width, double height, double fraction)
    {
        const int count = 24;
        const double gap = 3;
        double seg = Math.Max(2, (width - gap * (count - 1)) / count);
        double y = height / 2;
        double h = _hover || _dragging ? 7 : 5;

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        int played = (int)Math.Round(fraction * count);

        for (int i = 0; i < count; i++)
        {
            double x = i * (seg + gap);
            var brush = i < played ? accent : muted;
            dc.DrawRoundedRectangle(brush, null, new Rect(x, y - h / 2, seg, h), 1, 1);
        }
    }

    /// <summary>
    /// Волна: узор «как у трека». Настоящей волноформы нет — плеер её не отдаёт,
    /// поэтому рисуем стабильный псевдо-узор, завязанный на длительность: он не
    /// меняется между кадрами и читается как форма трека.
    /// </summary>
    private void DrawWave(DrawingContext dc, double width, double height, double fraction)
    {
        const double step = 3;
        int count = Math.Max(8, (int)(width / step));
        double mid = height / 2;
        double maxBar = height * 0.42;

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        double seed = Duration.TotalSeconds > 0 ? Duration.TotalSeconds : 120;

        for (int i = 0; i < count; i++)
        {
            double t = (double)i / count;
            double a = Math.Abs(Math.Sin(i * 0.37 + seed * 0.11));
            double b = Math.Abs(Math.Sin(i * 0.11 - seed * 0.07));
            double shape = 0.25 + (a * 0.55 + b * 0.2);

            if (i % 7 == 0) shape *= 0.45;
            if (i % 23 == 0) shape = 0.95;

            double bar = Math.Max(2, shape * maxBar * 2);
            double x = i * step;
            bool played = t <= fraction;

            dc.DrawRectangle(played ? accent : muted, null, new Rect(x, mid - bar / 2, 2, bar));
        }
    }

    /// <summary>Пески: прошедшее осыпается точками вниз.</summary>
    private void DrawSand(DrawingContext dc, double width, double height, double fraction)
    {
        double mid = height / 2;
        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        const double step = 5;
        int count = (int)(width / step);

        for (int i = 0; i <= count; i++)
        {
            double x = i * step;
            bool played = x <= width * fraction;

            if (!played)
            {
                dc.DrawRectangle(muted, null, new Rect(x, mid - 0.6, 1.2, 1.2));
                continue;
            }

            double age = 1 - (x / Math.Max(1, width * fraction));
            double fall = Math.Min(mid - 4, age * (mid - 2) * (0.4 + 0.6 * Math.Abs(Math.Sin(i * 0.7))));
            double size = 1.6 - age * 0.6;

            dc.DrawEllipse(accent, null, new Point(x, mid + fall), size, size);
        }

        DrawHead(dc, width * fraction, mid, 3.2, 0);
    }

    /// <summary>Дуга: круговой прогресс. Для таймлайна в высоком блоке.</summary>
    private void DrawArc(DrawingContext dc, double width, double height, double fraction)
    {
        double radius = Math.Max(8, Math.Min(width, height) / 2 - 3);
        var centre = new Point(width / 2, height / 2);

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        var track = new Pen(muted, 2);
        track.Freeze();

        // Толщина дуги дышит с басом: это и есть объединение стилей 8 и 10 —
        // кольцо показывает прогресс и одновременно реагирует на удар.
        float bass = AudioSpectrum.Current?.Bass ?? 0f;
        double thickness = 3 + bass * 2.4;

        var progress = new Pen(accent, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        progress.Freeze();

        dc.DrawEllipse(null, track, centre, radius, radius);

        if (fraction <= 0.001) return;

        double angle = 360 * fraction;
        var geo = new StreamGeometry();

        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(centre.X, centre.Y - radius), false, false);
            ctx.ArcTo(
                PointOnCircle(centre, radius, angle - 90),
                new Size(radius, radius),
                0,
                angle > 180,
                SweepDirection.Clockwise,
                true,
                false);
        }

        geo.Freeze();
        dc.DrawGeometry(null, progress, geo);

        // Голова дуги — точка, чтобы было видно, где именно «сейчас».
        var head = PointOnCircle(centre, radius, angle - 90);
        dc.DrawEllipse(accent, null, head, thickness * 1.1 + 1, thickness * 1.1 + 1);
    }

    /// <summary>Кассета: две катушки и лента между ними.</summary>
    private void DrawCassette(DrawingContext dc, double width, double height, double fraction)
    {
        double y = height / 2;
        double reel = Math.Min(height / 2 - 1, 9);

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        var left = new Point(reel + 2, y);
        var right = new Point(width - reel - 2, y);

        dc.DrawEllipse(null, new Pen(muted, 1), left, reel, reel);
        dc.DrawEllipse(null, new Pen(muted, 1), right, reel, reel);

        dc.DrawEllipse(accent, null, left, reel * fraction * 0.6 + 1.5, reel * fraction * 0.6 + 1.5);
        dc.DrawEllipse(accent, null, right, reel * (1 - fraction) * 0.6 + 1.5, reel * (1 - fraction) * 0.6 + 1.5);

        dc.DrawRectangle(muted, null, new Rect(left.X + reel, y - 0.6, right.X - left.X - reel * 2, 1.2));
        dc.DrawRectangle(accent, null, new Rect(left.X + reel, y - 0.6,
            (right.X - left.X - reel * 2) * fraction, 1.2));
    }

    // ---------------------------------------------------------------- ползунки

    /// <summary>Капсула: закруглённая заливка до ползунка.</summary>
    private void DrawCapsule(DrawingContext dc, double width, double height, double fraction)
    {
        double h = _hover || _dragging ? 8 : 6;
        double y = (height - h) / 2;

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        dc.DrawRoundedRectangle(muted, null, new Rect(0, y, width, h), h / 2, h / 2);
        if (fraction > 0.01)
            dc.DrawRoundedRectangle(accent, null, new Rect(0, y, Math.Max(h, width * fraction), h), h / 2, h / 2);

        double knob = _hover || _dragging ? 9 : 7;
        dc.DrawEllipse(Brushes.White, null, new Point(width * fraction, height / 2), knob / 2, knob / 2);
    }

    /// <summary>Блочный индикатор: квадраты, как громкость в машине.</summary>
    private void DrawBlocks(DrawingContext dc, double width, double height, double fraction)
    {
        const int count = 20;
        const double gap = 2;
        double block = Math.Max(2, (width - gap * (count - 1)) / count);
        double y = height / 2 - 3;
        double h = _hover || _dragging ? 8 : 6;

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        int filled = (int)Math.Round(fraction * count);

        for (int i = 0; i < count; i++)
        {
            double x = i * (block + gap);
            dc.DrawRectangle(i < filled ? accent : muted, null, new Rect(x, y, block, h));
        }
    }

    /// <summary>Точки с хвостом: у ползунка три точки крупнее.</summary>
    private void DrawDots(DrawingContext dc, double width, double height, double fraction)
    {
        double y = height / 2;
        const double step = 6;
        int count = (int)(width / step);

        var accent = AccentBrush();
        var muted = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        double head = width * fraction;

        for (int i = 0; i <= count; i++)
        {
            double x = i * step;
            double distance = Math.Abs(x - head);

            double radius = distance < 3 ? 3.6 : distance < 9 ? 2.6 : distance < 15 ? 2.0 : 1.2;
            bool filled = x <= head;

            dc.DrawEllipse(filled ? accent : muted, null, new Point(x, y), radius, radius);
        }
    }

    // ---------------------------------------------------------------- общее

    private void DrawHead(DrawingContext dc, double x, double y, double radius, float bass)
    {
        var accent = AccentBrush();

        var halo = new SolidColorBrush(Color.FromArgb(_dragging ? (byte)0x66 : (byte)0x33,
            ColorPalette.Accent.R, ColorPalette.Accent.G, ColorPalette.Accent.B));
        halo.Freeze();

        dc.DrawEllipse(halo, null, new Point(x, y), radius * 2.1 + bass * 6, radius * 2.1 + bass * 6);
        dc.DrawEllipse(accent, null, new Point(x, y), radius, radius);
    }

    private void DrawTimeHint(DrawingContext dc, double width, double height, double fraction)
    {
        var label = new FormattedText(
            TimeAt(Math.Clamp(fraction, 0, 1)),
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            10,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        double x = width * fraction;
        double labelX = Math.Clamp(x - label.Width / 2, 0, width - label.Width);
        var box = new Rect(labelX - 5, height / 2 - label.Height - 10, label.Width + 10, label.Height + 4);

        var plate = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0B, 0x0E));
        plate.Freeze();

        dc.DrawRoundedRectangle(plate, null, box, 4, 4);
        dc.DrawText(label, new Point(box.X + 5, box.Y + 2));
    }

    private static Point PointOnCircle(Point centre, double radius, double degrees)
    {
        double rad = degrees * Math.PI / 180;
        return new Point(centre.X + radius * Math.Cos(rad), centre.Y + radius * Math.Sin(rad));
    }

    private static SolidColorBrush AccentBrush()
    {
        // Второй акцент: прогресс и ползунки берут его, если он задан.
        var brush = new SolidColorBrush(ColorPalette.AccentSecond ?? ColorPalette.Accent);
        brush.Freeze();
        return brush;
    }

    private string TimeAt(double fraction)
    {
        double seconds = Duration.TotalSeconds * fraction;
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Math.Max(0, Duration.TotalSeconds)));

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (ActualWidth <= 0) return;

        _dragging = true;
        _hover = true;
        CaptureMouse();
        Apply(e.GetPosition(this).X, commit: false);
        e.Handled = true;
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        base.OnPreviewMouseMove(e);
        if (!_dragging) return;

        Apply(e.GetPosition(this).X, commit: false);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();

        // Перематываем один раз, на отпускании: иначе плеер дёргается на каждый пиксель.
        Apply(e.GetPosition(this).X, commit: true);
        e.Handled = true;
    }

    private void Apply(double x, bool commit)
    {
        double fraction = Math.Clamp(x / ActualWidth, 0, 1);
        _value = fraction * 100;
        InvalidateVisual();

        if (commit) SeekRequested?.Invoke(fraction);
    }
}
