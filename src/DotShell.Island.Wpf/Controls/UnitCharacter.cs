namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Audio;

/// <summary>
/// Юнит-персонаж в капсуле: фигурка из точек с характером.
///
/// Две сетки: компактная 12×16 (капсула до 46 px) и подробная 20×24 (крупный юнит).
/// Подробная включается сама по высоте — вручную ничего переключать не нужно.
///
/// Восемь настроений:
///   Idle — дышит, моргает, следит глазами;
///   Curious — курсор рядом: глаза шире, голова поворачивается;
///   Happy — клик: подпрыгивает, жмурится, летят точки;
///   Loved — много кликов подряд: глаза-дуги, румянец и сердечки;
///   Annoyed — слишком много кликов: глаза-полоски, брови, мотает головой;
///   Surprised — громкий бас: глаза во всю ширь, рот кругом;
///   Sad — 3 минуты без внимания: взгляд вниз, блик гаснет;
///   Sleepy — 9 минут без внимания: щёлочки, «z-z».
///
/// Живёт на CompositionTarget.Rendering с ограничением ~20 кадров/с и корректной остановкой.
/// </summary>
public sealed class UnitCharacter : FrameworkElement
{
    public enum UnitKind { Goth, Skeleton }

    /// <summary>Настроение персонажа.</summary>
    public enum Mood { Idle, Curious, Happy, Loved, Annoyed, Surprised, Sad, Sleepy }

    // Компактная сетка: тело без глаз и рта — лицо рисуется процедурно.
    private static readonly string[] GothSmall =
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

    private static readonly string[] SkeletonSmall =
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

    // Подробная сетка 20×24: косая чёлка, чокер, платье с бантом, платформы.
    private static readonly string[] GothLarge =
    {
        // 20×24: чёлка до бровей, лицо 8 точек, чокер, платье с бантом, платформы.
        ".......oooooo.......",
        ".....ooHHHHHHoo.....",
        "....oHHhhhhhhHHo....",
        "...oHHhhhhhhhhHHo...",
        "...ohhhhhhhhhhhho...",
        "...ohhhhhhhhhhhho...",
        "...ohhhhhhhhhhhho...",
        "...ohhffffffffhho...",
        "...ohhffffffffhho...",
        "...ohhffffffffhho...",
        "...ohhffffffffhho...",
        "...ohhffffffffhho...",
        "....ohhffffffhho....",
        "....ohhffffffhho....",
        ".....occcccco.......",
        "....oaaaaaaaaao.....",
        "...oaaaaaaaaaaao....",
        "...oaaaaaaaaaaao....",
        "....oaaaaaaaaao.....",
        "....oaaabbaaao......",
        "....oaaaaaaaaao.....",
        "......oo....oo......",
        "......ll....ll......",
        ".....LLL....LLL.....",
    };

    private static readonly string[] SkeletonLarge =
    {
        // 20×24: череп с рёбрами, глазницы рисуются процедурно (тёмным).
        ".......oooooo.......",
        ".....oowwwwwwoo.....",
        "....owwwwwwwwwwo....",
        "...owwwwwwwwwwwwo...",
        "...owwwwwwwwwwwwo...",
        "...owwwwwwwwwwwwo...",
        "...owwwwwwwwwwwwo...",
        "...owwwwwwwwwwwwo...",
        "....owwwwwwwwwwo....",
        ".....owwwwwwwwo.....",
        "..owwWWWWWWWWWWwwo..",
        "..owwwwwwwwwwwwwwo..",
        "..owwwkkwwwwkkwwwo..",
        "..owwwkkwwwwkkwwwo..",
        "..owwwkkwwwwkkwwwo..",
        "...owwkkwwwwkkwwo...",
        "....owwwwwwwwwwo....",
        ".....owwwwwwwwo.....",
        "......oo....oo......",
        "......ww....ww......",
        "......ww....ww......",
        "......ww....ww......",
        "......ww....ww......",
        ".....WWWW..WWWW.....",
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
        public double Size;
    }

    private readonly DateTime _started = DateTime.Now;
    private readonly List<Particle> _particles = new();

    private DateTime _nextBlink = DateTime.Now.AddSeconds(4);
    private DateTime _blinkUntil = DateTime.MinValue;
    private DateTime _happyUntil = DateTime.MinValue;
    private DateTime _lovedUntil = DateTime.MinValue;
    private DateTime _annoyedUntil = DateTime.MinValue;
    private DateTime _surprisedUntil = DateTime.MinValue;
    private DateTime _lastInteraction = DateTime.Now;
    private DateTime _lastPet = DateTime.MinValue;

    private int _recentPets;
    private bool _running;
    private UnitKind _kind = UnitKind.Goth;
    private Mood _mood = Mood.Idle;
    private bool _autoMood = true;

    public TimeSpan SadAfter { get; set; } = TimeSpan.FromMinutes(3);
    public TimeSpan SleepyAfter { get; set; } = TimeSpan.FromMinutes(9);

    public UnitCharacter()
    {
        Width = 40;
        Height = 42;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Hand;

        Loaded += (_, _) => StartLoop();
        Unloaded += (_, _) => _running = false;
        MouseLeftButtonDown += (_, e) => { Pet(); e.Handled = true; };
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

    public Mood MoodForIdle(TimeSpan idle)
    {
        if (idle > SleepyAfter) return Mood.Sleepy;
        if (idle > SadAfter) return Mood.Sad;
        return Mood.Idle;
    }

    public TimeSpan IdleFor => DateTime.Now - _lastInteraction;

    public int ParticleCount => _particles.Count;

    public bool IsHappy => _mood is Mood.Happy or Mood.Loved;

    /// <summary>Погладить: обычный клик радует, частые — сначала «обожает», потом «недовольна».</summary>
    public void Pet()
    {
        var now = DateTime.Now;
        _lastInteraction = now;

        // Серия кликов: считаем те, что идут подряд, без паузы больше 2.5 с.
        _recentPets = (now - _lastPet) < TimeSpan.FromSeconds(2.5) ? _recentPets + 1 : 1;
        _lastPet = now;

        if (_recentPets >= 6)
        {
            _mood = Mood.Annoyed;
            _annoyedUntil = now.AddSeconds(2);
            _recentPets = 0;
        }
        else if (_recentPets >= 3)
        {
            _mood = Mood.Loved;
            _lovedUntil = now.AddSeconds(2.2);
            SpawnParticles(now, accent: true, count: 12);
        }
        else
        {
            _mood = Mood.Happy;
            _happyUntil = now.AddMilliseconds(1500);
            SpawnParticles(now, accent: false, count: 9);
        }

        InvalidateVisual();
    }

    /// <summary>Громкий бас пугает — удивление на пару секунд.</summary>
    public void Surprise()
    {
        var now = DateTime.Now;

        // Настроение ставим всегда: это явный вызов, а не фоновая реакция,
        // и в витрине (AutoMood = false) он тоже должен срабатывать.
        _surprisedUntil = now.AddSeconds(1.2);
        _mood = Mood.Surprised;

        InvalidateVisual();
    }

    private void SpawnParticles(DateTime now, bool accent, int count)
    {
        var palette = ColorPalette.Accent;

        for (int i = 0; i < count; i++)
        {
            _particles.Add(new Particle
            {
                X = ActualWidth / 2 + Random.Shared.Next(-10, 10),
                Y = ActualHeight * 0.45,
                Vx = Random.Shared.NextDouble() * 1.8 - 0.9,
                Vy = -1.2 - Random.Shared.NextDouble() * 1.6,
                Born = now,
                Red = accent ? palette.R : (byte)0xFF,
                Green = accent ? palette.G : (byte)0xFF,
                Blue = accent ? palette.B : (byte)0xFF,
                Size = accent ? 1.25 : 1.0,
            });
        }
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

        if (now >= _nextBlink && now >= _blinkUntil
            && _mood is Mood.Idle or Mood.Curious or Mood.Sad)
        {
            _blinkUntil = now.AddMilliseconds(130);
            _nextBlink = now.AddSeconds(_mood == Mood.Sad ? 6 : 3);
        }

        UpdateMood(now);
        UpdateParticles(now);
        InvalidateVisual();
    }

    private void UpdateMood(DateTime now)
    {
        if (!_autoMood) return;

        // Временные реакции держат приоритет над фоновым настроением.
        if (now < _annoyedUntil) { _mood = Mood.Annoyed; return; }
        if (now < _lovedUntil) { _mood = Mood.Loved; return; }
        if (now < _surprisedUntil) { _mood = Mood.Surprised; return; }
        if (now < _happyUntil) { _mood = Mood.Happy; return; }

        var idle = now - _lastInteraction;

        if (idle > SadAfter) { _mood = MoodForIdle(idle); return; }

        // Курсор рядом — любопытство (только если не грустит и не спит).
        bool near = false;
        try
        {
            var mouse = Mouse.GetPosition(this);
            near = mouse.X >= -12 && mouse.Y >= -12
                   && mouse.X <= ActualWidth + 12 && mouse.Y <= ActualHeight + 12;
        }
        catch { }

        _mood = near ? Mood.Curious : Mood.Idle;
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

        bool large = ActualHeight >= 46;

        string[] body = (Kind, large) switch
        {
            (UnitKind.Goth, true) => GothLarge,
            (UnitKind.Goth, false) => GothSmall,
            (UnitKind.Skeleton, true) => SkeletonLarge,
            _ => SkeletonSmall,
        };

        int cols = large ? 20 : 12;
        int rows = large ? 24 : 16;

        double cell = Math.Max(2, Math.Min(ActualWidth / cols, ActualHeight / rows));
        double width = cell * cols;
        double height = cell * rows;

        var now = DateTime.Now;
        double t = (now - _started).TotalSeconds;

        double breatheSpeed = _mood switch
        {
            Mood.Sad => 1.0,
            Mood.Sleepy => 0.7,
            Mood.Annoyed => 2.6,
            Mood.Happy or Mood.Loved => 3.2,
            Mood.Surprised => 2.2,
            _ => 1.6,
        };

        double breatheAmplitude = _mood == Mood.Sleepy ? cell * 0.15 : cell * 0.3;
        double breathe = Math.Sin(t * breatheSpeed) * breatheAmplitude;

        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double bounce = -Math.Clamp(bass, 0, 1) * cell * 1.4;

        double droop = _mood is Mood.Sad or Mood.Sleepy ? cell * 0.7 : 0;
        double hop = _mood switch
        {
            Mood.Happy => -Math.Abs(Math.Sin(t * 7)) * cell * 0.8,
            Mood.Loved => -Math.Abs(Math.Sin(t * 5)) * cell * 0.5,
            Mood.Surprised => -Math.Abs(Math.Sin(t * 12)) * cell * 0.4,
            _ => 0,
        };

        // Недовольство: мотает головой.
        double shake = _mood == Mood.Annoyed ? Math.Sin(t * 22) * cell * 0.35 : 0;

        double originX = (ActualWidth - width) / 2 + shake;
        double originY = (ActualHeight - height) / 2 + breathe + bounce + droop + hop;

        // Взгляд за курсором.
        double lookX = 0, lookY = 0;
        try
        {
            var mouse = Mouse.GetPosition(this);
            lookX = Math.Clamp((mouse.X - ActualWidth / 2) / (ActualWidth / 2), -1, 1);
            lookY = Math.Clamp((mouse.Y - ActualHeight / 2) / (ActualHeight / 2), -1, 1);
        }
        catch { }

        if (_mood is Mood.Sad or Mood.Sleepy) { lookX *= 0.2; lookY = 1; }
        if (_mood == Mood.Annoyed) lookX = 0;

        DrawBody(dc, body, originX, originY, cell);
        DrawFace(dc, originX, originY, cell, large, lookX, lookY, now, t);
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

    private void DrawFace(DrawingContext dc, double ox, double oy, double cell, bool large,
        double lookX, double lookY, DateTime now, double t)
    {
        bool skeleton = Kind == UnitKind.Skeleton;

        var accent = ColorPalette.Accent;
        var accentBrush = Frozen(skeleton ? Color.FromRgb(0x0B, 0x0B, 0x0E) : accent);
        var glint = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
        var mouthBrush = Frozen(skeleton ? Color.FromRgb(0x0B, 0x0B, 0x0E) : Color.FromRgb(0x9A, 0x9A, 0xA4));
        var browBrush = Frozen(Color.FromRgb(0x1B, 0x1B, 0x22));

        // Геометрия лица зависит от сетки: компактная / подробная.
        bool bone = Kind == UnitKind.Skeleton;

        double eyeRow = large ? (bone ? 6.6 : 10.4) : (bone ? 6.2 : 6.2);
        double eyeLeft = large ? (bone ? 6.9 : 6.9) : 3.6;
        double eyeRight = large ? (bone ? 10.9 : 10.9) : 8.4;
        double mouthRow = large ? (bone ? 10.4 : 12.7) : 8.4;
        double cheekRow = large ? (bone ? 8.6 : 11.6) : 7.8;
        double faceCenter = large ? 9.0 : 6.0;

        double eyeY = oy + eyeRow * cell;
        double leftEyeX = ox + eyeLeft * cell;
        double rightEyeX = ox + eyeRight * cell;

        bool blink = now < _blinkUntil && _mood is Mood.Idle or Mood.Curious or Mood.Sad;
        double dx = lookX * cell * 0.45;
        double dy = lookY * cell * 0.35;

        double radius = _mood switch
        {
            Mood.Surprised => cell * 0.66,
            Mood.Curious => cell * 0.56,
            Mood.Sad => cell * 0.46,
            _ => cell * 0.5,
        };

        switch (_mood)
        {
            case Mood.Sleepy:
            case Mood.Annoyed:
                foreach (double x in new[] { leftEyeX, rightEyeX })
                    dc.DrawRectangle(accentBrush, null,
                        new Rect(x - cell * 0.48, eyeY, cell * 0.96, Math.Max(1, cell * 0.22)));

                if (_mood == Mood.Annoyed)
                {
                    // Брови домиком — главный признак недовольства.
                    dc.DrawRectangle(browBrush, null,
                        new Rect(leftEyeX - cell * 0.5, eyeY - cell * 0.55, cell * 1.0, Math.Max(1, cell * 0.18)));
                    dc.DrawRectangle(browBrush, null,
                        new Rect(rightEyeX - cell * 0.5, eyeY - cell * 0.55, cell * 1.0, Math.Max(1, cell * 0.18)));
                }
                break;

            case Mood.Happy:
            case Mood.Loved:
                // Жмурится дугами: две точки образуют «галочку».
                foreach (double x in new[] { leftEyeX, rightEyeX })
                {
                    dc.DrawEllipse(accentBrush, null, new Point(x - cell * 0.24, eyeY + cell * 0.2), cell * 0.34, cell * 0.34);
                    dc.DrawEllipse(accentBrush, null, new Point(x + cell * 0.24, eyeY - cell * 0.02), cell * 0.34, cell * 0.34);
                }

                if (_mood == Mood.Loved)
                {
                    // Румянец: две мягкие точки акцентом ниже глаз.
                    var blush = Frozen(Color.FromArgb(150, accent.R, accent.G, accent.B));
                    foreach (double x in new[] { leftEyeX - cell * 0.3, rightEyeX + cell * 0.3 })
                        dc.DrawEllipse(blush, null, new Point(x, oy + cheekRow * cell), cell * 0.42, cell * 0.3);
                }
                break;

            case Mood.Surprised:
                foreach (double x in new[] { leftEyeX, rightEyeX })
                {
                    dc.DrawEllipse(accentBrush, null, new Point(x, eyeY), cell * 0.62, cell * 0.62);
                    dc.DrawEllipse(glint, null, new Point(x + cell * 0.2, eyeY - cell * 0.22), cell * 0.2, cell * 0.2);
                }
                break;

            default:
                if (blink)
                {
                    foreach (double x in new[] { leftEyeX, rightEyeX })
                        dc.DrawRectangle(accentBrush, null,
                            new Rect(x - cell * 0.45, eyeY, cell * 0.9, Math.Max(1, cell * 0.22)));
                }
                else
                {
                    foreach (double x in new[] { leftEyeX, rightEyeX })
                    {
                        dc.DrawEllipse(accentBrush, null, new Point(x + dx, eyeY + dy), radius, radius);

                        if (_mood != Mood.Sad)
                            dc.DrawEllipse(glint, null,
                                new Point(x + dx + cell * 0.17, eyeY + dy - cell * 0.19), cell * 0.17, cell * 0.17);
                    }
                }
                break;
        }

        // --- рот ---
        double mouthY = oy + mouthRow * cell;
        double centerX = ox + faceCenter * cell;

        switch (_mood)
        {
            case Mood.Happy:
            case Mood.Loved:
                dc.DrawEllipse(mouthBrush, null, new Point(centerX - cell * 0.55, mouthY - cell * 0.1), cell * 0.22, cell * 0.22);
                dc.DrawEllipse(mouthBrush, null, new Point(centerX, mouthY + cell * 0.18), cell * 0.22, cell * 0.22);
                dc.DrawEllipse(mouthBrush, null, new Point(centerX + cell * 0.55, mouthY - cell * 0.1), cell * 0.22, cell * 0.22);
                break;

            case Mood.Sad:
                dc.DrawEllipse(mouthBrush, null, new Point(centerX - cell * 0.5, mouthY + cell * 0.1), cell * 0.2, cell * 0.2);
                dc.DrawEllipse(mouthBrush, null, new Point(centerX, mouthY - cell * 0.1), cell * 0.2, cell * 0.2);
                dc.DrawEllipse(mouthBrush, null, new Point(centerX + cell * 0.5, mouthY + cell * 0.1), cell * 0.2, cell * 0.2);
                break;

            case Mood.Sleepy:
                dc.DrawEllipse(mouthBrush, null, new Point(centerX, mouthY), cell * 0.3, cell * 0.2);
                break;

            case Mood.Surprised:
                dc.DrawEllipse(mouthBrush, null, new Point(centerX, mouthY), cell * 0.36, cell * 0.46);
                break;

            case Mood.Annoyed:
                dc.DrawRectangle(mouthBrush, null,
                    new Rect(centerX - cell * 0.42, mouthY, cell * 0.84, Math.Max(1, cell * 0.2)));
                break;

            case Mood.Curious:
                dc.DrawEllipse(mouthBrush, null, new Point(centerX - cell * 0.15, mouthY), cell * 0.26, cell * 0.22);
                break;

            default:
                dc.DrawRectangle(mouthBrush, null,
                    new Rect(centerX - cell * 0.5, mouthY, cell, Math.Max(1, cell * 0.2)));
                break;
        }

        // Сон: «z-z» точками справа сверху.
        if (_mood == Mood.Sleepy)
        {
            var zBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x93));
            double phase = (t % 3) / 3;
            double baseX = ox + (large ? 17.0 : 10.4) * cell;
            double baseY = oy + (large ? 1.4 : 1.2) * cell;

            for (int i = 0; i < 3; i++)
            {
                double offset = (phase + i * 0.33) % 1;
                dc.DrawEllipse(zBrush, null, new Point(
                    baseX + offset * cell * 1.6,
                    baseY - offset * cell * 2.4), cell * (0.16 + 0.12 * (1 - offset)), cell * (0.16 + 0.12 * (1 - offset)));
            }
        }

        // Любопытство: маленький знак «?» точкой над головой.
        if (_mood == Mood.Curious)
        {
            var qBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x93));
            double qx = ox + (large ? 16.8 : 10.2) * cell;
            double qy = oy + (large ? 3.4 : 2.2) * cell;
            dc.DrawEllipse(qBrush, null, new Point(qx, qy), cell * 0.2, cell * 0.2);
            dc.DrawEllipse(qBrush, null, new Point(qx, qy + cell * 0.55), cell * 0.14, cell * 0.14);
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
            double radius = cell * (0.14 + life * 0.16) * particle.Size;

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
