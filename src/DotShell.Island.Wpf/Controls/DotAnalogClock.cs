namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// Стрелочные часы из точек: циферблат-кольцо из 60 точек, часовая и минутная
/// стрелки — дорожки из точек, секунды — одинокая бегущая точка по кольцу.
///
/// Зачем вторые часы рядом с цифрами: цифры отвечают за читаемость,
/// а это — за характер. Электронно-аналоговый гибрид в духе Nothing:
/// необычно, но минимально — ни одной лишней линии.
///
/// Тик: раз в секунду (секундная точка), перерисовка дешёвая (DrawEllipse).
/// Когда остров спрятан — таймер стоит.
/// </summary>
public sealed class DotAnalogClock : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private bool _paused;

    // Кисти пересоздаются только на смену палитры, а не на каждый тик секунд.
    private Brush _muted = Brushes.Gray;
    private Brush _ghost = Brushes.Transparent;
    private Brush _accent = Brushes.Red;
    private Brush _white = Brushes.White;

    private void RebuildBrushes()
    {
        _muted = (Brush)FindResource(ColorPalette.TextMutedKey);
        _ghost = new SolidColorBrush(ColorPalette.GhostDotColor);
        _accent = new SolidColorBrush(ColorPalette.Accent);
        _white = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        _muted.Freeze(); _ghost.Freeze(); _accent.Freeze(); _white.Freeze();
    }

    public DotAnalogClock()
    {
        Width = 40;
        Height = 40;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => InvalidateVisual();

        Loaded += (_, _) => { RebuildBrushes(); if (!_paused) _timer.Start(); InvalidateVisual(); };
        Unloaded += (_, _) => _timer.Stop();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(() => { RebuildBrushes(); InvalidateVisual(); });
        DotStyle.Changed += () => Dispatcher.BeginInvoke(InvalidateVisual);
    }

    /// <summary>Остановить тиканье (остров спрятан). Симметрично DotClock.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _timer.Stop();
        else { InvalidateVisual(); _timer.Start(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var now = DateTime.Now;
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 10) return;

        double cx = ActualWidth / 2, cy = ActualHeight / 2;
        double dot = Math.Clamp(DotStyle.Cell * 0.45, 1.2, 3.2);   // радиус точки циферблата
        double ringR = size / 2 - dot - 1;

        var muted = _muted;
        var ghost = _ghost;
        var accent = _accent;
        var white = _white;

        // Циферблат: 60 точек, каждая пятая (часовая метка) чуть крупнее.
        for (int i = 0; i < 60; i++)
        {
            double a = i / 60.0 * Math.PI * 2 - Math.PI / 2;
            bool hourMark = i % 5 == 0;
            double r = hourMark ? dot * 1.5 : dot * 0.8;
            var p = new Point(cx + Math.Cos(a) * ringR, cy + Math.Sin(a) * ringR);
            dc.DrawEllipse(hourMark ? muted : ghost, null, p, r, r);
        }

        // Секунды: одиночная акцентная точка бежит по кольцу.
        {
            double a = now.Second / 60.0 * Math.PI * 2 - Math.PI / 2;
            var p = new Point(cx + Math.Cos(a) * ringR, cy + Math.Sin(a) * ringR);
            dc.DrawEllipse(accent, null, p, dot * 1.1, dot * 1.1);
        }

        // Стрелки — дорожки точек от центра: белая короче (часы), акцентная длиннее (минуты).
        DrawHand(dc, white, cx, cy,
            (now.Hour % 12 + now.Minute / 60.0) / 12.0 * Math.PI * 2 - Math.PI / 2,
            ringR * 0.52, dot * 1.25, 3);
        DrawHand(dc, accent, cx, cy,
            (now.Minute + now.Second / 60.0) / 60.0 * Math.PI * 2 - Math.PI / 2,
            ringR * 0.80, dot * 1.15, 4);
    }

    private static void DrawHand(DrawingContext dc, Brush brush, double cx, double cy,
        double angle, double length, double dotR, int dots)
    {
        for (int i = 1; i <= dots; i++)
        {
            double t = i / (double)dots;
            var p = new Point(cx + Math.Cos(angle) * length * t, cy + Math.Sin(angle) * length * t);
            dc.DrawEllipse(brush, null, p, dotR, dotR);
        }
    }
}
