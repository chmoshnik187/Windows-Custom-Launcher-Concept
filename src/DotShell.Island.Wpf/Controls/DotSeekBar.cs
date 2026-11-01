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

    /// <summary>Куда перемотать: доля 0..1.</summary>
    public event Action<double>? SeekRequested;

    public DotSeekBar()
    {
        Height = 22;   // по линии 2 px, но попадать мышью должно быть удобно
        Cursor = Cursors.Hand;
        SizeChanged += (_, _) => InvalidateVisual();
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

        // Дорожка: тонкая линия, как и была, — но теперь живая.
        dc.DrawRectangle(muted, null, new Rect(0, y - 1, width, 2));
        dc.DrawRectangle(accent, null, new Rect(0, y - 1, width * fraction, 2));

        // Точка-голова ползунка: видна всегда, иначе непонятно, что это можно тянуть.
        double x = width * fraction;

        var halo = new SolidColorBrush(Color.FromArgb(0x33, ColorPalette.Accent.R,
            ColorPalette.Accent.G, ColorPalette.Accent.B));
        halo.Freeze();

        dc.DrawEllipse(halo, null, new Point(x, y), 7, 7);
        dc.DrawEllipse(accent, null, new Point(x, y), 3.4, 3.4);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (ActualWidth <= 0) return;

        _dragging = true;
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
