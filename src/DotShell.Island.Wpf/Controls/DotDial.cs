namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Циферблат из точек: круг из N точек, пройденная часть — акцентом,
/// и на «голове» едет одна крупная точка.
///
/// Зачем: цифры вроде «04:37» — это бухгалтерия, а таймер должен
/// *показывать* время. Круг читается боковым зрением: сколько осталось,
/// видно без разглядывания чисел, а бегущая точка даёт ощущение хода.
///
/// Стиль тот же, что у всего шелла: точки, акцент, ничего лишнего.
/// </summary>
public sealed class DotDial : FrameworkElement
{
    /// <summary>Доля пройденного 0..1. DependencyProperty — чтобы работал Binding.</summary>
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(DotDial),
        new PropertyMetadata(0.0, (d, _) => ((DotDial)d).InvalidateVisual()));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, Math.Clamp(value, 0, 1));
    }

    /// <summary>Есть ли активный отсчёт (в покое круг тусклее).</summary>
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.Register(
        nameof(Active), typeof(bool), typeof(DotDial),
        new PropertyMetadata(false, (d, _) => ((DotDial)d).InvalidateVisual()));

    public bool Active
    {
        get => (bool)GetValue(ActiveProperty);
        set => SetValue(ActiveProperty, value);
    }

    /// <summary>Сколько точек по кругу. 60 — как секунды.</summary>
    public int DotCount { get; set; } = 60;

    public DotDial()
    {
        SizeChanged += (_, _) => InvalidateVisual();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 20 || h < 20) return;

        double radius = Math.Min(w, h) / 2 - 8;
        if (radius < 10) return;

        var center = new Point(w / 2, h / 2);

        var accent = new SolidColorBrush(ColorPalette.Accent);
        accent.Freeze();
        var ghost = new SolidColorBrush(ColorPalette.GhostDotColor);
        ghost.Freeze();
        var dim = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
        dim.Freeze();

        int count = Math.Max(12, DotCount);
        double dot = Math.Clamp(radius * 0.045, 1.1, 2.2);
        int filled = (int)Math.Round(count * Progress);

        for (int i = 0; i < count; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / count;
            var point = new Point(
                center.X + radius * Math.Cos(angle),
                center.Y + radius * Math.Sin(angle));

            // Каждая пятая точка крупнее — так круг читается как циферблат.
            double size = i % 5 == 0 ? dot * 1.5 : dot;

            // Пройденное — акцентом и чуть крупнее: контраст с «остатком»
            // должен читаться сразу, иначе круг выглядит однородным.
            if (i < filled) dc.DrawEllipse(accent, null, point, size * 1.25, size * 1.25);
            else if (Active) dc.DrawEllipse(dim, null, point, size, size);
            else dc.DrawEllipse(ghost, null, point, size, size);
        }

        // «Голова»: одна точка крупнее остальных, на конце пройденного пути.
        if (!Active) return;

        double headAngle = -Math.PI / 2 + Progress * 2 * Math.PI;
        var head = new Point(
            center.X + radius * Math.Cos(headAngle),
            center.Y + radius * Math.Sin(headAngle));

        dc.DrawEllipse(accent, null, head, dot * 2.8, dot * 2.8);
    }
}
