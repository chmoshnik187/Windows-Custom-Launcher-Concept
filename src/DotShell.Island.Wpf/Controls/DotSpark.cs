namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using DotShell.Core;

/// <summary>
/// Спарклайн точками: история загрузки (CPU, RAM, GPU) в языке острова.
///
/// Почему точки, а не линия: линия в 2 px спорит с обводкой капсулы, а точки читаются
/// как «пиксели истории» и совпадают с точечной графикой интерфейса.
///
/// Высокие значения (от 80%) рисуются акцентом — всплеск видно боковым зрением.
/// </summary>
public sealed class DotSpark : FrameworkElement
{
    private double[] _values = Array.Empty<double>();
    private Color _color = Color.FromRgb(0xED, 0xED, 0xEF);

    public double[] Values
    {
        get => _values;
        set
        {
            _values = value ?? Array.Empty<double>();
            InvalidateVisual();
        }
    }

    public Color Color
    {
        get => _color;
        set
        {
            _color = value;
            InvalidateVisual();
        }
    }

    public DotSpark()
    {
        Height = 26;
        SnapsToDevicePixels = true;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        if (_values.Length == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        int count = _values.Length;
        double step = ActualWidth / Math.Max(1, count - 1);
        double cell = Math.Min(step * 0.85, ActualHeight / 10.0);
        double radius = Math.Max(1, cell * 0.5);

        var accent = ColorPalette.Accent;
        var normal = Frozen(new SolidColorBrush(_color));
        var accentBrush = Frozen(new SolidColorBrush(accent));
        for (int i = 0; i < count; i++)
        {
            double value = Math.Clamp(_values[i], 0, 100);
            double x = i * step;
            double y = ActualHeight - Math.Max(radius, ActualHeight * value / 100.0);

            // Нули не рисуем: иначе график начинается с пустой дорожки бледных точек,
            // и непонятно, где кончается история и начинается «ничего».
            if (value <= 0) continue;

            var brush = value >= 80 ? accentBrush : normal;

            dc.DrawEllipse(brush, null, new Point(x + radius / 2, y), radius, radius);
        }
    }

    private static SolidColorBrush Frozen(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
