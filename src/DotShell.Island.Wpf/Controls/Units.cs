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

    /// <summary>
    /// Режим «читает»: юнит не следит за курсором, а водит взглядом по строке
    /// слева направо, как человек, читающий уведомление.
    /// </summary>
    public bool Reading
    {
        get => _reading;
        set
        {
            if (_reading == value) return;
            _reading = value;
            _readPhase = 0;
            SetReadingLoop(value);
        }
    }

    private bool _reading;
    private EventHandler? _readingFrame;

    private double _readPhase;

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
            // Тик у контрола, который ещё не подключён к окну, падал на
            // PointToScreen («Visual не подсоединён к PresentationSource»),
            // и после исключения таймер замолкал навсегда — из-за этого
            // не было видно анимации чтения.
            try
            {
                TrackCursor();
                UpdateMood();
                InvalidateVisual();
            }
            catch (InvalidOperationException)
            {
                // окно ещё не показано — ждём следующего тика
            }
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
        if (_reading)
        {

            // Взгляд едет по строке: плавно вправо, затем короткий возврат влево.
            _readPhase += 0.035;
            if (_readPhase >= 1) _readPhase = 0;

            // Читаем строку слева направо, затем быстрый возврат — как глаз
            // перескакивает на начало следующей строки.
            double t = _readPhase < 0.82
                ? _readPhase / 0.82
                : 1 - (_readPhase - 0.82) / 0.18;

            _dirX = -1.0 + 2.0 * t;
            _dirY = 0.45;

            _leanX += (_dirX - _leanX) * 0.25;
            _leanY += (_dirY - _leanY) * 0.25;
            _lastAttention = DateTime.Now;
            return;
        }

        if (!Win32.GetCursorPos(out var cursor)) return;

        // Без PresentationSource PointToScreen бросает исключение.
        if (PresentationSource.FromVisual(this) is null) return;

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


    /// <summary>
    /// Кадровый цикл чтения. Почему не DispatcherTimer: у контрола, который был
    /// создан скрытым, таймер числится включённым, но Tick не приходит — чтение
    /// из-за этого вообще не рисовалось. CompositionTarget.Rendering идёт с
    /// частотой монитора и не зависит от состояния видимости.
    /// </summary>
    private void SetReadingLoop(bool on)
    {
        if (on)
        {
            if (_readingFrame is not null) return;
            _readingFrame = (_, _) =>
            {
                StepReading();
                InvalidateVisual();
            };
            CompositionTarget.Rendering += _readingFrame;
        }
        else if (_readingFrame is not null)
        {
            CompositionTarget.Rendering -= _readingFrame;
            _readingFrame = null;
        }
    }

    /// <summary>Один шаг взгляда: вправо по строке, затем быстрый возврат.</summary>
    private void StepReading()
    {
        _readPhase += 0.006;
        if (_readPhase >= 1) _readPhase = 0;

        double t = _readPhase < 0.82
            ? _readPhase / 0.82
            : 1 - (_readPhase - 0.82) / 0.18;

        _dirX = -1.0 + 2.0 * t;
        _dirY = 0.45;

        _leanX += (_dirX - _leanX) * 0.16;
        _leanY += (_dirY - _leanY) * 0.16;
        _lastAttention = DateTime.Now;
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

        bool blinking = Reading
            ? DateTime.UtcNow.Millisecond % 1700 < 130
            : DateTimeOffset.Now.ToUnixTimeMilliseconds() % 3600 < 150;

        double bounce = _mood == Mood.Crying ? Math.Sin(_tearPhase) * 0.8 : 0;

        // Чтение: лёгкое покачивание корпуса, чтобы движение было заметно
        // боковым зрением, а не только по зрачкам.
        if (Reading) bounce += Math.Sin(_readPhase * Math.PI * 2) * 0.9;

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

        // При чтении зрачки ходят шире: 1.9 px на теле 26 px не читается глазом.
        double eyeScale = Reading ? 3.4 : 1.9;
        double ex = _dirX * eyeScale;
        double ey = _dirY * eyeScale;
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


