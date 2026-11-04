namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Audio;

/// <summary>
/// Юнит-персонаж в свёрнутой капсуле: маленькая фигурка вместо двух точек-глаз.
///
/// Рисуется по клеточной маске, и каждая клетка — ТОЧКА, а не квадрат: тот же язык,
/// что у острова. Зазор между точками — часть образа, поэтому радиус чуть меньше клетки.
///
/// Живость: дыхание, моргание раз в 3–6 секунд, взгляд за курсором, подпрыгивание на бас.
/// Цикл на CompositionTarget.Rendering с ограничением ~20 кадров/с и корректной остановкой.
/// </summary>
public sealed class UnitCharacter : FrameworkElement
{
    public enum UnitKind { Goth, Skeleton }

    // Чиби-готка: боб с чёлкой до бровей, бледное лицо, глаза акцентом с бликом,
    // чокер, платье с бантом, платформы. Сетка 12×16: одна клетка — одна точка.
    private static readonly string[] GothSprite =
    {
        "....oooo....",
        "..ooHHHHoo..",
        ".oHHhhhhHHo.",
        ".ohhhhhhhho.",
        ".ohhhhhhhho.",
        ".ofeffffefo.",
        ".ogffffffgo.",
        ".offfmmfffo.",
        ".ohhffffhho.",
        "..ohffffho..",
        "...occcco...",
        "..oaaaaaao..",
        "..oabbbbao..",
        "..oaaaaaao..",
        "...ollllo...",
        "..oLLooLLo..",
    };

    // Скелетик: тот же размер и та же точка, иначе переключение «прыгает».
    private static readonly string[] SkeletonSprite =
    {
        "....oooo....",
        "..ooWWWWoo..",
        ".oWwwwwwwWo.",
        ".owwwwwwwwo.",
        ".owkkwwkkwo.",
        ".owkkwwkkwo.",
        ".owwwkkwwwo.",
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

    private readonly DateTime _started = DateTime.Now;
    private DateTime _nextBlink = DateTime.Now.AddSeconds(4);
    private DateTime _blinkUntil = DateTime.MinValue;
    private bool _running;

    private UnitKind _kind = UnitKind.Goth;

    /// <summary>Кого рисуем: готка или скелетик.</summary>
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

    public UnitCharacter()
    {
        Width = 40;
        Height = 42;
        SnapsToDevicePixels = true;

        Loaded += (_, _) => StartLoop();
        Unloaded += (_, _) => _running = false;
    }

    private void StartLoop()
    {
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        // 20 кадров в секунду: дыхание и моргание плавные, а рендер не греется.
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
            _blinkUntil = now.AddMilliseconds(130);
            _nextBlink = now.AddSeconds(3 + Random.Shared.Next(0, 4));
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var sprite = Kind == UnitKind.Skeleton ? SkeletonSprite : GothSprite;

        double cell = Math.Max(2, Math.Min(ActualWidth / 12.0, ActualHeight / 16.0));
        double width = cell * 12;
        double height = cell * 16;

        double breathe = Math.Sin((DateTime.Now - _started).TotalSeconds * 1.6) * cell * 0.3;
        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double bounce = -Math.Clamp(bass, 0, 1) * cell * 1.4;

        int lookX = 0;
        try
        {
            var mouse = System.Windows.Input.Mouse.GetPosition(this);
            if (mouse.X < width * 0.35) lookX = -1;
            else if (mouse.X > width * 0.65) lookX = 1;
        }
        catch { }

        bool blink = DateTime.Now < _blinkUntil;

        double originX = (ActualWidth - width) / 2;
        double originY = (ActualHeight - height) / 2 + breathe + bounce;

        for (int row = 0; row < sprite.Length; row++)
        {
            var line = sprite[row];

            for (int col = 0; col < line.Length; col++)
            {
                char symbol = line[col];
                if (symbol == '.') continue;

                double x = originX + col * cell;
                double y = originY + row * cell;
                double cx = x + cell / 2;
                double cy = y + cell / 2;

                // Обводка чуть тоньше: держит силуэт, но не превращается в кайму.
                double radius = cell * (symbol == 'o' ? 0.40 : 0.47);

                var brush = BrushFor(symbol);

                if (blink && symbol == 'e')
                {
                    // Моргание: вместо глаза — короткая полоска ресниц.
                    dc.DrawRectangle(brush, null, new Rect(x, y + cell * 0.42, cell, Math.Max(1, cell * 0.24)));
                    continue;
                }

                if (symbol == 'e' && lookX != 0) cx += lookX * cell * 0.32;

                dc.DrawEllipse(brush, null, new Point(cx, cy), radius, radius);
            }
        }
    }

    private static Brush BrushFor(char symbol)
    {
        var accent = ColorPalette.Accent;

        Color color = symbol switch
        {
            'o' => Color.FromRgb(0x38, 0x38, 0x42),      // обводка
            'h' => Color.FromRgb(0x1B, 0x1B, 0x22),      // волосы
            'H' => Color.FromRgb(0x35, 0x35, 0x40),      // блик волос
            'f' => Color.FromRgb(0xED, 0xED, 0xEF),      // лицо
            'e' => accent,                                // глаза — акцент
            'g' => Color.FromRgb(0xFF, 0xFF, 0xFF),      // блик в глазу
            'm' => Color.FromRgb(0x9A, 0x9A, 0xA4),      // рот
            'c' => accent,                                // чокер
            'a' => Color.FromRgb(0x26, 0x26, 0x2E),      // платье
            'b' => accent,                                // бант
            'l' => Color.FromRgb(0x2A, 0x2A, 0x32),      // колготки
            'L' => Color.FromRgb(0x46, 0x46, 0x52),      // платформы
            'w' => Color.FromRgb(0xE8, 0xE8, 0xEA),      // кости
            'W' => Color.FromRgb(0xC4, 0xC4, 0xCC),      // тень костей
            'k' => Color.FromRgb(0x0B, 0x0B, 0x0E),      // глазницы
            _ => Colors.Transparent,
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
