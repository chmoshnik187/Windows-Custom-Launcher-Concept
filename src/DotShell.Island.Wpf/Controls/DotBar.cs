namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;

/// <summary>
/// Точечный индикатор загрузки: дорожка из точек, где «занятая» часть —
/// акцентом, а остальное — тихими точками сетки.
///
/// Почему не цифры: владелец справедливо заметил, что «CPU 14%» точечной
/// матрицей читается хуже, чем полоска. Здесь маленький виджет показывает
/// картинку (сколько занято), а больший добавляет к ней проценты и подписи.
///
/// Контрол тянется по ширине и сам решает, сколько точек влезает.
/// </summary>
public sealed class DotBar : FrameworkElement
{
    private double _value;

    /// <summary>Заполнение 0..1.</summary>
    public double Value
    {
        get => _value;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (Math.Abs(_value - value) < 0.001) return;
            _value = value;
            InvalidateVisual();
        }
    }

    /// <summary>Показывать «погашенные» точки дорожки.</summary>
    public bool ShowTrack { get; set; } = true;

    public DotBar()
    {
        Height = 12;
        SizeChanged += (_, _) => InvalidateVisual();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 12 || h < 4) return;

        double pitch = Math.Max(4, h * 0.85);
        int count = Math.Max(6, (int)(w / pitch));
        double radius = Math.Min(pitch * 0.28, h / 2 - 0.5);
        double y = h / 2;

        var on = new SolidColorBrush(ColorPalette.Accent);
        on.Freeze();
        var off = new SolidColorBrush(Color.FromArgb(0x2A, 0xFF, 0xFF, 0xFF));
        off.Freeze();

        int filled = (int)Math.Round(count * _value);

        for (int i = 0; i < count; i++)
        {
            double x = pitch * (i + 0.5);
            if (i < filled) dc.DrawEllipse(on, null, new Point(x, y), radius, radius);
            else if (ShowTrack) dc.DrawEllipse(off, null, new Point(x, y), radius, radius);
        }
    }
}
