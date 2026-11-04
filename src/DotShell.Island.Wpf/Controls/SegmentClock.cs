namespace DotShell.Island.Wpf.Controls;

using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// Часы «как на старых электронных», но в языке шелла: каждая цифра — сетка
/// 3×5 из точек, то есть те же палочки, только собранные из точек.
///
/// Зачем: обычная точечная матрица из шрифта на маленьком размере замыливалась —
/// 12:19 читалось как узор. Сетка 3×5 всегда даёт узнаваемую цифру, даже когда
/// на всю ширину приходится по 3–4 точки на знак.
///
/// Контрол сам подбирает размер под доступное место и по ширине, и по высоте.
/// </summary>
public sealed class SegmentClock : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private DateTime _now = DateTime.Now;

    public SegmentClock()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            var now = DateTime.Now;
            if (now.Minute == _now.Minute) return;
            _now = now;
            InvalidateVisual();
        };

        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        SizeChanged += (_, _) => InvalidateVisual();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    // Цифры 3×5: единица — точка. Классическая «калькуляторная» матрица.
    private static readonly string[] Digits =
    {
        "111101101101111", // 0
        "010010010010010", // 1
        "111001111100111", // 2
        "111001111001111", // 3
        "101101111001001", // 4
        "111100111001111", // 5
        "111100111101111", // 6
        "111001001001001", // 7
        "111101111101111", // 8
        "111101111001111", // 9
    };

    private const int DigitW = 3;
    private const int DigitH = 5;
    private const double ColonCells = 1.0;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 120 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 40 : availableSize.Height;
        return new Size(Math.Max(40, w), Math.Max(16, h));
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double w = ActualWidth, h = ActualHeight;
        if (w < 24 || h < 12) return;

        var brush = new SolidColorBrush(ColorPalette.Accent);
        brush.Freeze();

        // Сетка: 4 цифры + двоеточие, между знаками по ячейке воздуха.
        // 4 цифры по (3 ячейки + 1 воздух) и двоеточие с воздухом: 18 ячеек.
        // Раньше здесь было 16 — цифры не влезали и последняя обрезалась.
        double cellsW = (DigitW + 1) * 4 + (ColonCells + 1);
        double pitch = Math.Min(w / (cellsW + 0.4), h / (DigitH + 0.8));
        if (pitch <= 0.5) return;

        // Живость: часы дышат с басом или мерцают редко — по настройке.
        string liveliness = ShellServices.Instance.Settings.Current.DotLiveliness;
        double alive = liveliness switch
        {
            "bass" => 1 + (DotShell.Core.Audio.AudioSpectrum.Current?.Bass ?? 0f) * 0.45,
            "rare" => 1 + 0.12 * Math.Sin(DateTime.Now.TimeOfDay.TotalSeconds * 0.8),
            _ => 1,
        };

        double radius = Math.Max(0.6, pitch * 0.35 * alive);
        double totalW = cellsW * pitch;
        double totalH = DigitH * pitch;
        double x0 = (w - totalW) / 2;
        double y0 = (h - totalH) / 2;

        string text = _now.ToString("HH:mm", CultureInfo.InvariantCulture);
        double cursor = 0;

        foreach (var ch in text)
        {
            if (ch == ':')
            {
                double cx = x0 + (cursor + 0.35) * pitch;
                Dot(dc, brush, cx, y0 + 1.4 * pitch, radius);
                Dot(dc, brush, cx, y0 + 3.4 * pitch, radius);
                cursor += ColonCells + 1.0;
                continue;
            }

            int digit = ch - '0';
            if (digit is >= 0 and <= 9)
            {
                var pattern = Digits[digit];
                for (int row = 0; row < DigitH; row++)
                    for (int col = 0; col < DigitW; col++)
                    {
                        if (pattern[row * DigitW + col] != '1') continue;
                        Dot(dc, brush, x0 + (cursor + col + 0.5) * pitch, y0 + (row + 0.5) * pitch, radius);
                    }
            }

            cursor += DigitW + 1.0;
        }
    }

    private static void Dot(DrawingContext dc, Brush brush, double x, double y, double r) =>
        dc.DrawEllipse(brush, null, new Point(x, y), r, r);
}

