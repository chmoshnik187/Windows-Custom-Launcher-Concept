namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Мини-график курса: линия из точек, как и всё в шелле.
///
/// Зачем свой, а не Chart: нужна одна ломаная без осей, легенд и сеток,
/// зато в цвете акцента и с толстыми «точками-каплями» на перегибах.
/// Ось времени не подписывается — рядом стоят даты начала и конца.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public Sparkline()
    {
        Height = 56;
        SizeChanged += (_, _) => InvalidateVisual();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    /// <summary>Значения по порядку слева направо.</summary>
    public IReadOnlyList<double> Values
    {
        get => _values;
        set
        {
            _values = value ?? Array.Empty<double>();
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 24 || h < 16 || _values.Count < 2) return;

        double min = _values.Min();
        double max = _values.Max();
        double span = max - min;
        if (span < 0.0001) span = Math.Max(0.01, max * 0.001);

        var accent = new SolidColorBrush(ColorPalette.Accent);
        accent.Freeze();
        var faded = new SolidColorBrush(ColorPalette.AccentFaded);
        faded.Freeze();
        var ghost = new SolidColorBrush(ColorPalette.GhostDotColor);
        ghost.Freeze();

        double padX = 6, padY = 8;
        double usableW = Math.Max(1, w - padX * 2);
        double usableH = Math.Max(1, h - padY * 2);

        Point At(int i)
        {
            double x = padX + usableW * i / (_values.Count - 1);
            double y = padY + usableH * (1 - (_values[i] - min) / span);
            return new Point(x, y);
        }

        // Сетка-подложка: три горизонтальные линии, очень тихие.
        for (int i = 0; i <= 2; i++)
        {
            double y = padY + usableH * i / 2.0;
            dc.DrawLine(new Pen(ghost, 1), new Point(padX, y), new Point(w - padX, y));
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(At(0), false, false);
            for (int i = 1; i < _values.Count; i++) ctx.LineTo(At(i), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 1.4), geometry);

        // Точки: каждая пятая плюс последняя — «капли», как в остальном шелле.
        for (int i = 0; i < _values.Count; i++)
        {
            bool last = i == _values.Count - 1;
            if (!last && i % Math.Max(1, _values.Count / 14) != 0) continue;
            var point = At(i);
            dc.DrawEllipse(last ? accent : faded, null, point, last ? 2.6 : 1.6, last ? 2.6 : 1.6);
        }
    }
}
