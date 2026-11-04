namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Audio;

/// <summary>
/// Юнит-персонаж в свёрнутой капсуле: маленькая фигурка вместо двух точек-глаз.
///
/// Рисуется по клеточной маске: каждый символ — одна «точка» персонажа. Так фигурка
/// остаётся в языке острова (точки, а не сглаженные картинки) и читается в 32–40 px.
///
/// Живость: дыхание (покачивание), моргание раз в 3–6 секунд, взгляд за курсором,
/// подпрыгивание на бас. Всё на CompositionTarget.Rendering, 20 кадров в секунду —
/// чаще не нужно, а батарею бережёт.
/// </summary>
public sealed class UnitCharacter : FrameworkElement
{
    public enum UnitKind { Goth, Skeleton }

    private static readonly string[] GothSprite =
    {
        "....hhhh....",
        "...hhhhhh...",
        "..hhhhhhhh..",
        "..hhhhhhhh..",
        "..hffffffh..",
        "..hfeffefh..",
        "b.hffmmffh.b",
        "...hffffh...",
        "....aaaa....",
        "...aaaaaa...",
        "..aaaaaaaa..",
        "..aabbaaaa..",
        "...aaaaaa...",
        "...aa..aa...",
        "...aa..aa...",
        "..bbb..bbb..",
    };
    private static readonly string[] SkeletonSprite =
    {
        "....wwww....",
        "...wwwwww...",
        "..wwwwwwww..",
        "..wkkwwkkw..",
        "..wkwwwwkw..",
        "..wwkwwkww..",
        "..wwwwwwww..",
        "...wwkkww...",
        "....wwww....",
        "...wwwwww...",
        "..w.w..w.w..",
        "..w.w..w.w..",
        "..w.w..w.w..",
        "...w.ww.w...",
        "....w..w....",
        "...ww..ww...",
    };

    private DateTime _started = DateTime.Now;
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
        Height = 40;
        IsHitTestVisible = true;
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
        // 20 кадров в секунду достаточно: дыхание и моргание плавные, а рендер не греется.
        if (DateTime.Now.Millisecond % 50 > 5) return;

        if (PresentationSource.FromVisual(this) is null)
        {
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
            return;
        }

        if (DateTime.Now >= _nextBlink && DateTime.Now >= _blinkUntil)
        {
            _blinkUntil = DateTime.Now.AddMilliseconds(130);
            _nextBlink = DateTime.Now.AddSeconds(3 + Random.Shared.Next(0, 4));
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

        // Дыхание: фигурка покачивается на пол-клетки.
        double breathe = Math.Sin((DateTime.Now - _started).TotalSeconds * 1.6) * cell * 0.35;

        // Бас подбрасывает: чем громче низ, тем выше прыжок.
        double bass = AudioSpectrum.Current?.Bass ?? 0f;
        double bounce = -Math.Clamp(bass, 0, 1) * cell * 1.6;

        // Взгляд: глаза смотрят в сторону курсора (на 1 клетку).
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

                Brush brush = BrushFor(symbol);

                double x = originX + col * cell;
                double y = originY + row * cell;

                if (blink && symbol == 'e')
                {
                    // Моргание: вместо глаза — тонкая полоска ресниц.
                    dc.DrawRectangle(brush, null, new Rect(x, y + cell * 0.4, cell, Math.Max(1, cell * 0.3)));
                    continue;
                }

                if (symbol == 'e' && lookX != 0)
                {
                    dc.DrawRectangle(brush, null, new Rect(x + lookX * cell * 0.4, y, cell, cell));
                    continue;
                }

                dc.DrawRectangle(brush, null, new Rect(x, y, cell, cell));
            }
        }
    }

    private Brush BrushFor(char symbol)
    {
        var accent = ColorPalette.Accent;

        Color color = symbol switch
        {
            'h' => Color.FromRgb(0x1A, 0x1A, 0x20),      // волосы: почти чёрный
            'f' => Color.FromRgb(0xEC, 0xEC, 0xEE),      // лицо: светлое
            'e' => accent,                                // глаза: акцент
            'm' => Color.FromRgb(0x8A, 0x8A, 0x93),      // рот: приглушённый
            'a' => Color.FromRgb(0x2A, 0x2A, 0x32),      // платье
            'b' => accent,                                // отделка платья
            'w' => Color.FromRgb(0xE8, 0xE8, 0xEA),      // кости
            'k' => Color.FromRgb(0x0B, 0x0B, 0x0E),      // глазницы/тени
            _ => Colors.Transparent,
        };

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
