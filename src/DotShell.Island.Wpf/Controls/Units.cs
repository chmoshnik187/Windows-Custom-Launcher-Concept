namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DotShell.Core.Native;

/// <summary>
/// Юниты: два точечных существа, которые следят за курсором.
///
/// Что умеют (по просьбе владельца):
///   • всё тельце тянется к курсору, а не только зрачки;
///   • клик по юниту — он пугается и радуется вниманию;
///   • если внимания нет час — грустит (глаза полуприкрыты, рот вниз);
///   • если три часа — начинает плакать (слёзы-акценты капают).
///
/// Всё рисуется вручную на DrawingContext: фигур мало, 14 кадров в секунду
/// хватает, а таймер останавливается, когда остров раскрыт или спрятан.
/// </summary>
public sealed class Units : FrameworkElement
{
    private enum Mood { Calm, Surprised, Sad, Crying }

    private readonly DispatcherTimer _timer;
    private bool _paused;

    private double _dirX;
    private double _dirY;
    private double _leanX;
    private double _leanY;

    private Mood _mood = Mood.Calm;
    private DateTime _lastAttention = DateTime.Now;
    private DateTime _reactionUntil = DateTime.MinValue;
    private double _tearPhase;

    /// <summary>Включать ли реакцию на клик (в острове — нет, там клик принадлежит капсуле).</summary>
    public bool Interactive { get; set; }

    private static readonly int[,] Body =
    {
        { 0, 1, 1, 0 },
        { 1, 1, 1, 1 },
        { 1, 1, 1, 1 },
        { 0, 1, 1, 0 },
    };

    public Units()
    {
        Width = 66;
        Height = 26;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _timer.Tick += (_, _) =>
        {
            TrackCursor();
            UpdateMood();
            InvalidateVisual();
        };

        Loaded += (_, _) => { if (!_paused) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();

        if (!Interactive) return;

        // Клик = внимание: юнит пугается, а потом радуется.
        MouseLeftButtonDown += (_, e) =>
        {
            _lastAttention = DateTime.Now;
            _reactionUntil = DateTime.Now.AddMilliseconds(900);
            _mood = Mood.Surprised;
            InvalidateVisual();
            e.Handled = true;
        };
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _timer.Stop();
        else _timer.Start();
    }

    private void TrackCursor()
    {
        if (!Win32.GetCursorPos(out var cursor)) return;

        var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
        double dx = cursor.X - center.X;
        double dy = cursor.Y - center.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) { _dirX = 0; _dirY = 0; return; }

        _dirX = dx / len;
        _dirY = dy / len;

        _leanX += (_dirX - _leanX) * 0.18;
        _leanY += (_dirY - _leanY) * 0.18;

        // Курсор рядом — это тоже внимание (юнит «чувствует» мышь).
        if (len < 320 && Interactive) _lastAttention = DateTime.Now;
    }

    private void UpdateMood()
    {
        if (DateTime.Now < _reactionUntil) return;

        var idle = DateTime.Now - _lastAttention;
        _mood = idle.TotalHours switch
        {
            >= 3 => Mood.Crying,
            >= 1 => Mood.Sad,
            _ => Mood.Calm,
        };

        if (_mood == Mood.Crying) _tearPhase += 0.16;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var body = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        var eye = new SolidColorBrush(ColorPalette.Accent);
        body.Freeze();
        eye.Freeze();

        bool blinking = DateTimeOffset.Now.ToUnixTimeMilliseconds() % 3600 < 150;
        double bounce = _mood == Mood.Crying ? Math.Sin(_tearPhase) * 0.8 : 0;

        DrawUnit(dc, body, eye, new Point(ActualWidth * 0.27, ActualHeight / 2 + bounce), blinking);
        DrawUnit(dc, body, eye, new Point(ActualWidth * 0.73, ActualHeight / 2 + bounce), blinking);
    }

    private void DrawUnit(DrawingContext dc, Brush body, Brush eye, Point center, bool blinking)
    {
        double pitch = Math.Clamp(Math.Min(ActualWidth / 12, ActualHeight / 3.6), 3.0, 9.0);
        double dot = pitch * 0.46;

        // Тельце целиком тянется к курсору: раньше двигались только зрачки,
        // и юнит казался наклейкой.
        double lean = _mood == Mood.Surprised ? 3.2 : 2.2;
        double ox = center.X - _leanX * lean - pitch * 1.5;
        double oy = center.Y - _leanY * lean - pitch * 1.5;

        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
            {
                if (Body[r, c] == 0) continue;
                dc.DrawEllipse(body, null, new Point(ox + c * pitch, oy + r * pitch), dot, dot);
            }

        double ex = _dirX * 1.9;
        double ey = _dirY * 1.9;
        double baseX = center.X - _leanX * lean;
        double baseY = center.Y - 1.5 - _leanY * lean;
        var left = new Point(baseX - 3.2 + ex, baseY + ey);
        var right = new Point(baseX + 3.2 + ex, baseY + ey);

        switch (_mood)
        {
            case Mood.Sad:
                // Полуприкрытые глаза: тонкие полоски.
                dc.DrawEllipse(body, null, left, 1.6, 0.5);
                dc.DrawEllipse(body, null, right, 1.6, 0.5);
                break;

            case Mood.Crying:
                dc.DrawEllipse(body, null, left, 1.6, 0.5);
                dc.DrawEllipse(body, null, right, 1.6, 0.5);
                // Слёзы: акцентная капля под каждым глазом, падает по фазе.
                double fall = 2.0 + Math.Abs(Math.Sin(_tearPhase)) * 4.0;
                dc.DrawEllipse(eye, null, new Point(left.X, left.Y + fall), 0.9, 1.4);
                dc.DrawEllipse(eye, null, new Point(right.X, right.Y + fall * 0.8), 0.9, 1.4);
                break;

            case Mood.Surprised:
                dc.DrawEllipse(eye, null, left, 1.9, 1.9);
                dc.DrawEllipse(eye, null, right, 1.9, 1.9);
                break;

            default:
                if (blinking)
                {
                    dc.DrawEllipse(body, null, left, 1.5, 0.4);
                    dc.DrawEllipse(body, null, right, 1.5, 0.4);
                }
                else
                {
                    dc.DrawEllipse(eye, null, left, 1.5, 1.5);
                    dc.DrawEllipse(eye, null, right, 1.5, 1.5);
                }
                break;
        }
    }
}


