namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// Полоса перемотки: клик и перетаскивание, в стиле точек.
///
/// Раньше здесь стоял обычный ProgressBar — он только показывает и не принимает
/// ни клика, ни перетаскивания. Со стороны это выглядело как сломанный ползунок:
/// тянешь, а трек не перематывается.
///
/// Тонкость: пока идёт перетаскивание, внешнее обновление прогресса (раз в тик)
/// не должно двигать точку — иначе она «вырывается» из-под курсора.
/// </summary>
public sealed class DotSeekBar : FrameworkElement
{
    private double _value;
    private bool _dragging;

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

    private bool _hover;

    /// <summary>Куда перемотать: доля 0..1.</summary>
    public event Action<double>? SeekRequested;

    public DotSeekBar()
    {
        Height = 22;   // по линии 2 px, но попадать мышью должно быть удобно
        Cursor = Cursors.Hand;
        SizeChanged += (_, _) => InvalidateVisual();
        MouseEnter += (_, _) => { _hover = true; InvalidateVisual(); };
        MouseLeave += (_, _) => { _hover = false; InvalidateVisual(); };
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = ActualWidth;
        if (width < 6) return;

        double y = ActualHeight / 2;
        double fraction = Math.Clamp(_value / 100, 0, 1);

        // Прозрачный прямоугольник на всю площадь: без него FrameworkElement
        // попадает под мышь только там, где что-то нарисовано (тонкая линия),
        // и попасть в полосу курсором почти невозможно.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, ActualHeight));

        var accent = new SolidColorBrush(ColorPalette.Accent);
        accent.Freeze();
        var muted = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        muted.Freeze();

        // Дорожка толще под курсором: 2 px читаются как «просто линия», а
        // тянуть по ним неудобно. Наведение — единственная подсказка, что это
        // ползунок, поэтому оно заметное.
        double thickness = _hover || _dragging ? 3 : 2;

        dc.DrawRectangle(muted, null, new Rect(0, y - thickness / 2, width, thickness));

        // Прошедшая часть — не сплошная заливка, а точки: тот же язык, что у
        // остального шелла. Плотность точек показывает прогресс даже боковым зрением.
        double dotStep = 5;
        int dots = (int)Math.Floor(width / dotStep);
        for (int i = 0; i <= dots; i++)
        {
            double dx = i * dotStep;
            if (dx > width - 1) break;

            bool played = dx <= width * fraction;
            if (!played) continue;

            dc.DrawEllipse(accent, null, new Point(dx, y), 1.6, 1.6);
        }

        // Точка-голова ползунка: видна всегда, иначе непонятно, что это можно тянуть.
        double x = width * fraction;

        var halo = new SolidColorBrush(Color.FromArgb(_dragging ? (byte)0x55 : (byte)0x33,
            ColorPalette.Accent.R, ColorPalette.Accent.G, ColorPalette.Accent.B));
        halo.Freeze();

        dc.DrawEllipse(halo, null, new Point(x, y), _hover || _dragging ? 9 : 7, _hover || _dragging ? 9 : 7);
        dc.DrawEllipse(accent, null, new Point(x, y), _hover || _dragging ? 4.6 : 3.4, _hover || _dragging ? 4.6 : 3.4);

        // Под курсором — время, куда попадёшь. Без него перемотка вслепую.
        if (_hover && Duration > TimeSpan.Zero && width > 40)
        {
            var label = new FormattedText(
                TimeAt(Math.Clamp(_value / 100, 0, 1)),
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                10,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double labelX = Math.Clamp(x - label.Width / 2, 0, width - label.Width);
            var box = new Rect(labelX - 5, y - label.Height - 10, label.Width + 10, label.Height + 4);

            var plate = new SolidColorBrush(Color.FromArgb(0xE6, 0x0B, 0x0B, 0x0E));
            plate.Freeze();
            dc.DrawRoundedRectangle(plate, null, box, 4, 4);
            dc.DrawText(label, new Point(box.X + 5, box.Y + 2));
        }
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

    private string TimeAt(double fraction)
    {
        double seconds = Duration.TotalSeconds * fraction;
        var time = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, Duration.TotalSeconds));

        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }
}
