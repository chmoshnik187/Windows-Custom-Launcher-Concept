namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DotShell.Core;

/// <summary>
/// Крупный точечный портрет юнита для больших виджетов.
///
/// Зачем отдельный контрол: в капсуле острова помещается фигурка 12×16/20×24, а в
/// виджете 399×311 места хватает на 104×102 — почти восемь тысяч точек. Это тот же
/// юнит, только в портретном разрешении: рога, нимб, маска, серьга, крыло.
///
/// Рисунок собирается один раз в замороженный DrawingGroup (иначе 8 тысяч эллипсов
/// на каждом кадре убили бы 20 fps). Живое — только дыхание, лёгкий наклон за
/// курсором и искры по клику.
/// </summary>
public sealed class UnitPortrait : FrameworkElement
{
    private static readonly SolidColorBrush Cream = Frozen(Color.FromRgb(0xED, 0xED, 0xEF));
    private static readonly SolidColorBrush CreamDim = Frozen(Color.FromRgb(0xB9, 0xB9, 0xC4));
    private static readonly SolidColorBrush Edge = Frozen(Color.FromRgb(0x3C, 0x3C, 0x46));
    private static readonly SolidColorBrush Hair = Frozen(Color.FromRgb(0x1B, 0x1B, 0x22));

    private readonly DispatcherTimer _timer;
    private readonly List<Particle> _particles = new();

    private DrawingGroup? _cache;
    private Size _cacheSize;
    private int _cacheAccent;
    private bool _running;
    private DateTime _started = DateTime.Now;
    private double _lean;

    public UnitPortrait()
    {
        Width = 312;
        Height = 306;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        _timer.Tick += (_, _) => Tick();

        Loaded += (_, _) => { _timer.Start(); StartLoop(); };
        Unloaded += (_, _) => { _timer.Stop(); _running = false; };
        MouseLeftButtonDown += (_, e) => { SpawnParticles(); e.Handled = true; };
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

        InvalidateVisual();
    }

    private void Tick()
    {
        try
        {
            var mouse = Mouse.GetPosition(this);
            double target = Math.Clamp((mouse.X - ActualWidth / 2) / (ActualWidth / 2), -1, 1) * 4;
            _lean += (target - _lean) * 0.25;
        }
        catch { }

        UpdateParticles();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var accent = ColorPalette.Accent;
        int accentKey = accent.GetHashCode();

        if (_cache is null || _cacheSize != RenderSize || _cacheAccent != accentKey)
        {
            BuildCache(accent);
            _cacheSize = RenderSize;
            _cacheAccent = accentKey;
        }

        if (_cache is null || RenderSize.Width < 8 || RenderSize.Height < 8) return;

        double t = (DateTime.Now - _started).TotalSeconds;
        double breathe = Math.Sin(t * 1.4) * 1.6;
        double bob = Math.Sin(t * 0.7) * 0.8;

        var offset = new TranslateTransform(_lean + Math.Sin(t * 0.5) * 0.8, breathe + bob);
        dc.PushTransform(offset);
        dc.DrawDrawing(_cache);
        dc.Pop();

        // искры по клику — как у маленького юнита
        foreach (var p in _particles)
        {
            double life = Math.Clamp(1 - (DateTime.Now - p.Born).TotalMilliseconds / 1100.0, 0, 1);
            if (life <= 0) continue;
            var brush = new SolidColorBrush(Color.FromArgb((byte)(life * 220), p.R, p.G, p.B));
            brush.Freeze();
            double r = 1.2 + life * 2.2;
            dc.DrawEllipse(brush, null, new Point(p.X, p.Y), r, r);
        }
    }

    private void BuildCache(Color accent)
    {
        var group = new DrawingGroup();
        using (var ctx = group.Open())
        {
            double cell = Math.Min(ActualWidth / UnitPortraitData.Cols, ActualHeight / UnitPortraitData.Rows);
            double ox = (ActualWidth - cell * UnitPortraitData.Cols) / 2;
            double oy = (ActualHeight - cell * UnitPortraitData.Rows) / 2;

            var accentBrush = new SolidColorBrush(accent);
            accentBrush.Freeze();

            for (int row = 0; row < UnitPortraitData.Body.Length; row++)
            {
                string line = UnitPortraitData.Body[row];
                for (int col = 0; col < line.Length; col++)
                {
                    char symbol = line[col];
                    if (symbol == '.') continue;

                    var (brush, fraction) = symbol switch
                    {
                        'f' => (Cream, 0.46),
                        'H' => (CreamDim, 0.30),
                        'c' => ((Brush)accentBrush, 0.38),
                        'o' => (Edge, 0.20),
                        'h' => (Hair, 0.15),
                        _ => (Cream, 0.3),
                    };

                    double r = Math.Max(0.7, cell * fraction);
                    ctx.DrawEllipse(brush, null,
                        new Point(ox + col * cell + cell / 2, oy + row * cell + cell / 2), r, r);
                }
            }
        }

        group.Freeze();
        _cache = group;
    }

    private void SpawnParticles()
    {
        var accent = ColorPalette.Accent;
        for (int i = 0; i < 12; i++)
        {
            _particles.Add(new Particle
            {
                X = ActualWidth / 2 + Random.Shared.Next(-20, 20),
                Y = ActualHeight * 0.55,
                Vx = Random.Shared.NextDouble() * 2 - 1,
                Vy = -1.4 - Random.Shared.NextDouble() * 1.6,
                Born = DateTime.Now,
                R = i % 3 == 0 ? (byte)0xFF : accent.R,
                G = i % 3 == 0 ? (byte)0xFF : accent.G,
                B = i % 3 == 0 ? (byte)0xFF : accent.B,
            });
        }
    }

    private void UpdateParticles()
    {
        var now = DateTime.Now;
        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            if ((now - p.Born).TotalMilliseconds > 1100)
            {
                _particles.RemoveAt(i);
                continue;
            }
            p.X += p.Vx;
            p.Y += p.Vy;
            p.Vy -= 0.035;
        }
    }

    private sealed class Particle
    {
        public double X, Y, Vx, Vy;
        public DateTime Born;
        public byte R, G, B;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
