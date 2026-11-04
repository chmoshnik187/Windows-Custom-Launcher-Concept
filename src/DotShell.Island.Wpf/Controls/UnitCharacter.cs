namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Audio;

/// <summary>
/// Юнит-персонаж в капсуле: фигурка из точек с характером.
///
/// Устроено как маленький автомат состояний:
///   • <b>Idle</b>  — дышит, моргает, следит глазами за курсором;
///   • <b>Happy</b> — на клик: подпрыгивает, жмурится, вокруг летят точки-сердечки;
///   • <b>Sad</b>   — если её долго не трогают: смотрит вниз, вздыхает, реже моргает;
///   • <b>Sleepy</b>— если не трогают совсем долго (или ночь): глаза-щёлочки, «z-z» точками.
///
/// Рисуется точками, а не квадратами: та же графика, что у часов и заголовка трека.
/// Цикл на CompositionTarget.Rendering (~20 кадров/с) с корректной остановкой при отвязке.
/// </summary>
public sealed class UnitCharacter : FrameworkElement
{
    public enum UnitKind { Goth, Skeleton }

    /// <summary>Настроение персонажа. Меняется сама; можно выставить вручную (витрина, тесты).</summary>
    public enum Mood { Idle, Happy, Sad, Sleepy }

    // Тело без глаз и рта: лицо рисуется отдельно, чтобы выражения были настоящими.
    private static readonly string[] GothBody =
    {
        "....oooo....",
        "..ooHHHHoo..",
        ".oHHhhhhHHo.",
        ".ohhhhhhhho.",
        ".ohhhhhhhho.",
        ".ohffffffho.",
        ".ohffffffho.",
        ".ohffffffho.",
        ".ohhffffhho.",
        "..ohffffho..",
        "...occcco...",
        "..oaaaaaao..",
        "..oabbbbao..",
        "..oaaaaaao..",
        "...ollllo...",
        "..oLLooLLo..",
    };

    private static readonly string[] SkeletonBody =
    {
        "....oooo....",
        "..ooWWWWoo..",
        ".oWwwwwwwWo.",
        ".owwwwwwwwo.",
        ".owwwwwwwwo.",
        ".owwwwwwwwo.",
        ".owwwwwwwwo.",
        ".owwwwwwwwo.",
        "..owkkkkwo..",
        "...owwwwo...",
        "..oWwwwwWo..",
        ".owwowwowwo.",
        ".owwowwowwo.",
        "..owowwowo..",
        "...owwwwo...",
        "..oWWooWWo..",
    };

    private sealed class Particle
    {
        public double X;
        public double Y;
        public double Vx;
        public double Vy;
        public DateTime Born;
        public byte Red;
        public byte Green;
        public byte Blue;
    }

    private readonly DateTime _started = DateTime.Now;
    private readonly List<Particle> _particles = new();

    private DateTime _nextBlink = DateTime.Now.AddSeconds(4);
    private DateTime _blinkUntil = DateTime.MinValue;
    private DateTime _happyUntil = DateTime.MinValue;
    private DateTime _lastInteraction = DateTime.Now;

    private bool _running;
    private UnitKind _kind = UnitKind.Goth;
    private Mood _mood = Mood.Idle;
    private bool _autoMood = true;

    /// <summary>Через сколько без внимания грустнеет.</summary>
    public TimeSpan SadAfter { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Через сколько без внимания засыпает.</summary>
    public TimeSpan SleepyAfter { get; set; } = TimeSpan.FromMinutes(9);

    public UnitCharacter()
    {
        Width = 40;
        Height = 42;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;

        Loaded += (_, _) => StartLoop();
        Unloaded += (_, _) => _running = false;
        MouseLeftButtonDown += OnTapped;
    }

    public UnitKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            InvalidateVisual();
        }
    }

    /// <summary>Автоматическое настроение по времени и клику. Выключается для витрины.</summary>
    public bool AutoMood
    {
        get => _autoMood;
        set => _autoMood = value;
    }

    public Mood CurrentMood
    {
        get => _mood;
        set
        {
            if (_mood == value) return;
            _mood = value;
            InvalidateVisual();
        }
    }

    /// <summary>Погладить персонажа: он радуется и выпускает точки.</summary>
    public void Pet()
    {
        _lastInteraction = DateTime.Now;
        _happyUntil = DateTime.Now.AddMilliseconds(1500);
        _mood = Mood.Happy;

        // Немного точек-сердечек вбок и вверх.
        for (int i = 0; i < 9; i++)
        {
            _particles.Add(new Particle
            {
                X = ActualWidth / 2 + Random.Shared.Next(-8, 8),
                Y = ActualHeight * 0.45,
                Vx = Random.Shared.NextDouble() * 1.6 - 0.8,
                Vy = -1.2 - Random.Shared.NextDouble() * 1.4,
                Born = DateTime.Now,
                Red = 0xFF,
                Green = 0xFF,
                Blue = 0xFF,
            });
        }

        InvalidateVisual();
    }

    private void OnTapped(object sender, MouseButtonEventArgs e)
    {
        Pet();
        e.Handled = true;
    }

    private void StartLoop()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        if (DateTime.Now.Millisecond % 50 > 5) return;

        if (PresentationSource.FromVisual(this) is null)
        {
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
            return;
        }

        var now = DateTime.Now;

        if (now >= _nextBlink && now >= _blinkUntil)
        {
            _blinkUntil = now.AddMilliseconds(_mood == Mood.Sleepy ? 220 : 130);
            _nextBlink = now.AddSeconds(_mood == Mood.Sad ? 6 : 3);
        }

        UpdateMood(now);
        UpdateParticles(now);
        InvalidateVisual();
    }

    private void UpdateMood(DateTime now)
    {
        if (!_autoMood) return;

        if (_mood == Mood.Happy && now < _happyUntil) return;

        var idle = now - _lastInteraction;

        _mood = idle > SleepyAfter ? Mood.Sleepy
            : idle > SadAfter ? Mood.Sad
            : Mood.Idle;
    }

    private void UpdateParticles(DateTime now)
    {
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var particle = _particles[i];

            if ((now - particle.Born).TotalMilliseconds > 900)
            {
                _particles.RemoveAt(i);
                continue;
            }

            particle.X += particle.Vx;
            particle.Y += particle.Vy;
            particle.Vy -= 0.04;
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var body = Kind == UnitKind.Skeleton ? SkeletonBody : GothBody;

        double cell = Math.Max(2, Math.Min(ActualWidth / 12.0, ActualHeight / 16.0));
        double width = cell * 12;
        double height = cell * 16;

        var now = DateTime.Now;
        double t = (now - _started).TotalSeconds;

        // Дыхание: в грусти медленнее и ниже, в радости подпрыгивает.
        double breatheSpeed = _mood switch
        {
            Mood.Sad => 1.0,
            Mood.Sleepy => 0.7,
            Mood.Happy => 3.2,
            _ => 1.6,
        };

        double breatheAmplitude = _mood == Mood.Sleepy ? cell * 0.15 : cell * 0.3;
        double breathe = Math.Sin(t * breatheSpeed) * breatheAmplitude;

        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double bounce = -Math.Clamp(bass, 0, 1) * cell * 1.4;

        double sadDroop = _mood == Mood.Sad ? cell * 0.6 : _mood == Mood.Sleepy ? cell * 0.9 : 0;

        double originX = (ActualWidth - width) / 2;
        double originY = (ActualHeight - height) / 2 + breathe + bounce + sadDroop;

        // Взгляд: глаза тянутся к курсору, но не дальше полутора клеток.
        double lookX = 0, lookY = 0;
        try
        {
            var mouse = Mouse.GetPosition(this);
            lookX = Math.Clamp((mouse.X - ActualWidth / 2) / (ActualWidth / 2), -1, 1);
            lookY = Math.Clamp((mouse.Y - ActualHeight / 2) / (ActualHeight / 2), -1, 1);
        }
        catch { }

        if (_mood is Mood.Sad or Mood.Sleepy)
        {
            lookX *= 0.2;
            lookY = 1;   // в грусти смотрит вниз
        }

        DrawBody(dc, body, originX, originY, cell);
        DrawFace(dc, originX, originY, cell, lookX, lookY, now, t);
        DrawParticles(dc, cell);
    }

    private void DrawBody(DrawingContext dc, string[] body, double ox, double oy, double cell)
    {
        for (int row = 0; row < body.Length; row++)
        {
            var line = body[row];
            for (int col = 0; col < line.Length; col++)
            {
                char symbol = line[col];
                if (symbol == '.') continue;

                double radius = cell * (symbol == 'o' ? 0.40 : 0.47);
                var brush = BodyBrush(symbol);

                dc.DrawEllipse(brush, null, new Point(
                    ox + col * cell + cell / 2,
                    oy + row * cell + cell / 2), radius, radius);
            }
        }
    }

    private void DrawFace(DrawingContext dc, double ox, double oy, double cell,
        double lookX, double lookY, DateTime now, double t)
    {
        var accent = ColorPalette.Accent;
        var accentBrush = Frozen(accent);
        var glint = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var mouth = Frozen(Color.FromRgb(0x9A, 0x9A, 0xA4));

        double eyeY = oy + 6.2 * cell;
        double leftEyeX = ox + 3.6 * cell;
        double rightEyeX = ox + 8.4 * cell;

        bool blink = now < _blinkUntil && _mood != Mood.Sleepy;
        bool squint = _mood == Mood.Happy;
        bool closed = _mood == Mood.Sleepy;

        // Смещение взгляда: 0.45 клетки — достаточно, чтобы читалось «смотрит».
        double dx = lookX * cell * 0.45;
        double dy = lookY * cell * 0.35;

        if (closed || blink)
        {
            foreach (double x in new[] { leftEyeX, rightEyeX })
                dc.DrawRectangle(accentBrush, null,
                    new Rect(x - cell * 0.45, eyeY, cell * 0.9, Math.Max(1, cell * 0.22)));
        }
        else if (squint)
        {
            // Жмурится: вместо точки — короткая дуга-«галочка».
            foreach (double x in new[] { leftEyeX, rightEyeX })
            {
                dc.DrawEllipse(accentBrush, null, new Point(x - cell * 0.22, eyeY + cell * 0.18), cell * 0.34, cell * 0.34);
                dc.DrawEllipse(accentBrush, null, new Point(x + cell * 0.22, eyeY - cell * 0.05), cell * 0.34, cell * 0.34);
            }
        }
        else
        {
            foreach (double x in new[] { leftEyeX, rightEyeX })
            {
                dc.DrawEllipse(accentBrush, null, new Point(x + dx, eyeY + dy), cell * 0.5, cell * 0.5);

                // Блики: в грусти приглушён, чтобы взгляд был «потухшим».
                if (_mood != Mood.Sad)
                    dc.DrawEllipse(glint, null, new Point(x + dx + cell * 0.16, eyeY + dy - cell * 0.18), cell * 0.17, cell * 0.17);
            }
        }

        // Рот: нейтрально — черта, радость — улыбка, грусть — уголки вниз, сон — маленький овал.
        double mouthY = oy + 8.4 * cell;
        double centerX = ox + 6 * cell;

        switch (_mood)
        {
            case Mood.Happy:
                dc.DrawEllipse(mouth, null, new Point(centerX - cell * 0.55, mouthY - cell * 0.1), cell * 0.22, cell * 0.22);
                dc.DrawEllipse(mouth, null, new Point(centerX, mouthY + cell * 0.18), cell * 0.22, cell * 0.22);
                dc.DrawEllipse(mouth, null, new Point(centerX + cell * 0.55, mouthY - cell * 0.1), cell * 0.22, cell * 0.22);
                break;

            case Mood.Sad:
                dc.DrawEllipse(mouth, null, new Point(centerX - cell * 0.5, mouthY + cell * 0.1), cell * 0.2, cell * 0.2);
                dc.DrawEllipse(mouth, null, new Point(centerX, mouthY - cell * 0.1), cell * 0.2, cell * 0.2);
                dc.DrawEllipse(mouth, null, new Point(centerX + cell * 0.5, mouthY + cell * 0.1), cell * 0.2, cell * 0.2);
                break;

            case Mood.Sleepy:
                dc.DrawEllipse(mouth, null, new Point(centerX, mouthY), cell * 0.3, cell * 0.2);
                break;

            default:
                dc.DrawRectangle(mouth, null, new Rect(centerX - cell * 0.5, mouthY, cell, Math.Max(1, cell * 0.2)));
                break;
        }

        // Сон: «z-z» точками справа сверху.
        if (_mood == Mood.Sleepy)
        {
            var zBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x93));
            double phase = (t % 3) / 3;

            for (int i = 0; i < 3; i++)
            {
                double offset = (phase + i * 0.33) % 1;
                double zx = ox + 10.4 * cell + offset * cell * 1.6;
                double zy = oy + 1.2 * cell - offset * cell * 2.4;
                double r = cell * (0.16 + 0.12 * (1 - offset));

                dc.DrawEllipse(zBrush, null, new Point(zx, zy), r, r);
            }
        }
    }

    private void DrawParticles(DrawingContext dc, double cell)
    {
        var now = DateTime.Now;

        foreach (var particle in _particles)
        {
            double life = Math.Clamp(1 - (now - particle.Born).TotalMilliseconds / 900.0, 0, 1);
            if (life <= 0) continue;

            var brush = Frozen(Color.FromArgb((byte)(life * 210), particle.Red, particle.Green, particle.Blue));
            double radius = cell * (0.14 + life * 0.16);

            dc.DrawEllipse(brush, null, new Point(particle.X, particle.Y), radius, radius);
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush BodyBrush(char symbol)
    {
        var accent = ColorPalette.Accent;

        Color color = symbol switch
        {
            'o' => Color.FromRgb(0x38, 0x38, 0x42),
            'h' => Color.FromRgb(0x1B, 0x1B, 0x22),
            'H' => Color.FromRgb(0x35, 0x35, 0x40),
            'f' => Color.FromRgb(0xED, 0xED, 0xEF),
            'c' => accent,
            'a' => Color.FromRgb(0x26, 0x26, 0x2E),
            'b' => accent,
            'l' => Color.FromRgb(0x2A, 0x2A, 0x32),
            'L' => Color.FromRgb(0x46, 0x46, 0x52),
            'w' => Color.FromRgb(0xE8, 0xE8, 0xEA),
            'W' => Color.FromRgb(0xC4, 0xC4, 0xCC),
            'k' => Color.FromRgb(0x0B, 0x0B, 0x0E),
            _ => Colors.Transparent,
        };

        return Frozen(color);
    }
}
