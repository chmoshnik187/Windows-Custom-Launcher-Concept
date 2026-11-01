namespace DotShell.Island.Wpf;

using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core;
using DotShell.Core.Native;
using DotShell.Core.Settings;

/// <summary>
/// Свои курсоры: рисуем их точками и ставим в систему.
///
/// Курсор — единственное, что остаётся системным, когда всё вокруг уже перекрашено.
/// Формы задаются в координатах 0..1, поэтому один и тот же код даёт картинку любого
/// размера (32, 48, 64) — Windows берёт разный в зависимости от DPI.
///
/// Горячая точка у стрелки — центр точки, у текстового — середина черты. Ошибка здесь
/// означает, что курсор «врёт» на пару пикселей, и попадать по мелочам невозможно.
/// </summary>
public static class CursorStyler
{
    public static string Folder => Path.Combine(AppSettings.SettingsDirectory, "cursors");

    public static bool Enabled { get; private set; }

    private static SettingsStore? _settings;

    /// <summary>
    /// Запомнить настройки. Вызывается до применения: внутри конструктора сервисов
    /// ShellServices.Instance ещё не присвоен, и обращение к нему даёт NullReference.
    /// </summary>
    public static void Attach(SettingsStore settings) => _settings = settings;

    /// <summary>Размеры, которые просит Windows при разном масштабе экрана.</summary>
    private static readonly int[] Sizes = { 32, 48, 64 };

    public static void Enable()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            var files = new Dictionary<string, string>
            {
                ["Arrow"] = MakeSet("arrow", 0.5, 0.5, Arrow),
                ["UpArrow"] = MakeSet("uparrow", 0.5, 0.25, UpArrow),
                ["Hand"] = MakeSet("hand", 0.5, 0.5, Hand),
                ["IBeam"] = MakeSet("ibeam", 0.5, 0.5, IBeam),
                ["Crosshair"] = MakeSet("crosshair", 0.5, 0.5, Crosshair),
                ["Wait"] = MakeSet("wait", 0.5, 0.5, Wait),
                ["Help"] = MakeSet("help", 0.5, 0.5, Help),
                ["No"] = MakeSet("no", 0.5, 0.5, No),
                ["SizeNS"] = MakeSet("sizens", 0.5, 0.5, p => Resize(p, vertical: true)),
                ["SizeWE"] = MakeSet("sizewe", 0.5, 0.5, p => Resize(p, vertical: false)),
                ["SizeNWSE"] = MakeSet("sizenwse", 0.5, 0.5, p => Diagonal(p, downRight: true)),
                ["SizeNESW"] = MakeSet("sizenesw", 0.5, 0.5, p => Diagonal(p, downRight: false)),
                ["SizeAll"] = MakeSet("sizeall", 0.5, 0.5, SizeAll),
                ["NWPen"] = MakeSet("nwpen", 0.5, 0.5, NwPen),
                ["Pin"] = MakeSet("pin", 0.5, 0.5, Pin),
                ["Person"] = MakeSet("person", 0.5, 0.5, Person),
            };
            files["AppStarting"] = files["Wait"];

            var snapshot = CustomCursors.Capture(Folder);
            if (snapshot is not null && snapshot.Paths.Count > 0)
            {
                if (_settings is { } store && string.IsNullOrEmpty(store.Current.CursorOriginal))
                    store.Update(s => s.CursorOriginal = JsonSerializer.Serialize(snapshot));
            }

            bool ok = CustomCursors.Apply(files);
            Enabled = ok;
            _settings?.Update(s => s.CustomCursor = ok);
            Log.Info($"cursors: применено {files.Count}, ok={ok}");
        }
        catch (Exception ex)
        {
            Log.Info($"cursors failed: {ex.Message}");
            Enabled = false;
        }
    }

    public static void Disable()
    {
        try
        {
            var settings = _settings ?? ShellServices.Instance.Settings;
            var json = settings.Current.CursorOriginal;

            if (!string.IsNullOrEmpty(json))
            {
                var snapshot = JsonSerializer.Deserialize<CustomCursors.Snapshot>(json);
                if (snapshot is not null) CustomCursors.Restore(snapshot);
            }

            settings.Update(s => s.CustomCursor = false);
            Enabled = false;
            Log.Info("cursors: вернул системные");
        }
        catch (Exception ex) { Log.Info($"cursors restore failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------
    //  Формы
    // ------------------------------------------------------------------

    /// <summary>Стрелка: точка с белым ободком. Ободок обязателен — без него точка
    /// теряется на тёмных обоях ровно там, где курсор нужен.</summary>
    private static void Arrow(Painter p)
    {
        p.Ring(0.5, 0.5, 0.215, 1.7, Colors.White);
        p.Dot(0.5, 0.5, 0.175, ColorPalette.Accent);
    }

    /// <summary>Ссылка: кольцо без заливки — на кнопки и ссылки не «наезжает».</summary>
    private static void Hand(Painter p)
    {
        p.Ring(0.5, 0.5, 0.30, 1.7, Colors.White);
        p.Ring(0.5, 0.5, 0.19, 1.7, ColorPalette.Accent);
    }

    /// <summary>Текст: три точки по вертикали.</summary>
    private static void IBeam(Painter p)
    {
        p.Dot(0.5, 0.24, 0.055, Colors.White);
        p.Dot(0.5, 0.50, 0.055, ColorPalette.Accent);
        p.Dot(0.5, 0.76, 0.055, Colors.White);
    }

    /// <summary>Прицел: четыре точки вокруг центра.</summary>
    private static void Crosshair(Painter p)
    {
        p.Dot(0.5, 0.20, 0.05, Colors.White);
        p.Dot(0.5, 0.80, 0.05, Colors.White);
        p.Dot(0.20, 0.5, 0.05, Colors.White);
        p.Dot(0.80, 0.5, 0.05, Colors.White);
        p.Dot(0.5, 0.5, 0.045, ColorPalette.Accent);
    }

    /// <summary>Ожидание: кольцо из восьми точек.</summary>
    private static void Wait(Painter p)
    {
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            p.Dot(0.5 + 0.30 * Math.Cos(angle), 0.5 + 0.30 * Math.Sin(angle), 0.05, Colors.White);
        }
    }

    // ------------------------------------------------------------------
    //  Остальные формы: направление должно читаться, поэтому у стрелок
    //  изменения размера есть наконечники, а не просто точки.
    // ------------------------------------------------------------------

    /// <summary>Выбор альтернативы: треугольник вверх над точкой.</summary>
    private static void UpArrow(Painter p)
    {
        p.Triangle(0.5, 0.34, TriangleDirection.Up, 0.16, Colors.White);
        p.Dot(0.5, 0.66, 0.10, ColorPalette.Accent);
    }

    /// <summary>Справка: кольцо с точкой в центре — «наведи и спроси».</summary>
    private static void Help(Painter p)
    {
        p.Ring(0.5, 0.5, 0.30, 1.7, Colors.White);
        p.Dot(0.5, 0.5, 0.10, ColorPalette.Accent);
    }

    /// <summary>Недоступно: кольцо и перечёркивающая линия из точек.</summary>
    private static void No(Painter p)
    {
        p.Ring(0.5, 0.5, 0.30, 1.7, Colors.White);
        for (int i = 0; i < 5; i++)
        {
            double t = 0.30 + i * 0.10;
            p.Dot(0.5 - 0.28 + t * 0.56, 0.5 - 0.28 + t * 0.56, 0.035, ColorPalette.Accent);
        }
    }

    /// <summary>Изменение размера по вертикали: наконечники вверх и вниз.</summary>
    private static void Resize(Painter p, bool vertical)
    {
        if (vertical)
        {
            p.Triangle(0.5, 0.22, TriangleDirection.Up, 0.15, Colors.White);
            p.Triangle(0.5, 0.78, TriangleDirection.Down, 0.15, Colors.White);
            for (int i = 0; i < 3; i++) p.Dot(0.5, 0.38 + i * 0.12, 0.04, ColorPalette.Accent);
        }
        else
        {
            p.Triangle(0.22, 0.5, TriangleDirection.Left, 0.15, Colors.White);
            p.Triangle(0.78, 0.5, TriangleDirection.Right, 0.15, Colors.White);
            for (int i = 0; i < 3; i++) p.Dot(0.38 + i * 0.12, 0.5, 0.04, ColorPalette.Accent);
        }
    }

    /// <summary>Диагональное изменение размера: наконечники по углам.</summary>
    private static void Diagonal(Painter p, bool downRight)
    {
        double a = downRight ? 0.24 : 0.24;
        double b = downRight ? 0.24 : 0.76;

        p.Triangle(a, b, downRight ? TriangleDirection.UpLeft : TriangleDirection.UpRight, 0.15, Colors.White);
        p.Triangle(1 - a, 1 - b, downRight ? TriangleDirection.DownRight : TriangleDirection.DownLeft, 0.15, Colors.White);

        for (int i = 1; i <= 3; i++)
        {
            double t = i / 4.0;
            p.Dot(a + (1 - 2 * a) * t, b + (1 - 2 * b) * t, 0.04, ColorPalette.Accent);
        }
    }

    /// <summary>Перемещение: четыре наконечника.</summary>
    private static void SizeAll(Painter p)
    {
        p.Triangle(0.5, 0.20, TriangleDirection.Up, 0.13, Colors.White);
        p.Triangle(0.5, 0.80, TriangleDirection.Down, 0.13, Colors.White);
        p.Triangle(0.20, 0.5, TriangleDirection.Left, 0.13, Colors.White);
        p.Triangle(0.80, 0.5, TriangleDirection.Right, 0.13, Colors.White);
        p.Dot(0.5, 0.5, 0.06, ColorPalette.Accent);
    }

    /// <summary>Рукописный ввод: наклонная линия с точкой на конце.</summary>
    private static void NwPen(Painter p)
    {
        for (int i = 0; i < 5; i++)
        {
            double t = i / 4.0;
            p.Dot(0.28 + t * 0.34, 0.72 - t * 0.34, 0.045, Colors.White);
        }
        p.Dot(0.72, 0.28, 0.09, ColorPalette.Accent);
    }

    /// <summary>Закрепление: точка с «ножкой».</summary>
    private static void Pin(Painter p)
    {
        p.Dot(0.5, 0.36, 0.14, ColorPalette.Accent);
        p.Ring(0.5, 0.36, 0.22, 1.5, Colors.White);
        for (int i = 0; i < 3; i++) p.Dot(0.5, 0.60 + i * 0.09, 0.035, Colors.White);
    }

    /// <summary>Выбор человека: кольцо-«голова» и точка-«плечи».</summary>
    private static void Person(Painter p)
    {
        p.Ring(0.5, 0.34, 0.16, 1.7, Colors.White);
        p.Dot(0.5, 0.70, 0.13, ColorPalette.Accent);
    }

    private enum TriangleDirection { Up, Down, Left, Right, UpLeft, UpRight, DownLeft, DownRight }
    // ------------------------------------------------------------------
    //  Сборка файлов
    // ------------------------------------------------------------------

    private static string MakeSet(string name, double hotX, double hotY, Action<Painter> draw)
    {
        string? primary = null;

        foreach (var size in Sizes)
        {
            var path = Render(name, size, hotX, hotY, draw);
            primary ??= path;
        }

        return primary!;
    }

    private static string Render(string name, int size, double hotX, double hotY, Action<Painter> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, size, size));
            draw(new Painter(dc, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using var memory = new MemoryStream();
        encoder.Save(memory);

        var path = Path.Combine(Folder, $"{name}-{size}.cur");
        CustomCursors.WriteCursor(path, memory.ToArray(), (int)(hotX * size), (int)(hotY * size), size);
        return path;
    }

    /// <summary>Примитивы в координатах 0..1.</summary>
    private sealed class Painter
    {
        private readonly DrawingContext _dc;
        private readonly int _size;

        public Painter(DrawingContext dc, int size)
        {
            _dc = dc;
            _size = size;
        }

        public void Dot(double x, double y, double r, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _dc.DrawEllipse(brush, null, new Point(x * _size, y * _size), r * _size, r * _size);
        }

        /// <summary>Треугольник-наконечник: направление читается даже в 16 px.</summary>
        public void Triangle(double x, double y, TriangleDirection direction, double size, Color color)
        {
            double s = size * _size;
            var cx = x * _size;
            var cy = y * _size;

            var figure = new PathFigure { IsClosed = true, IsFilled = true };
            var (a, b, c) = Corners(direction, s);
            figure.Segments.Add(new LineSegment(new Point(cx + a.X, cy + a.Y), true));
            figure.Segments.Add(new LineSegment(new Point(cx + b.X, cy + b.Y), true));
            figure.Segments.Add(new LineSegment(new Point(cx + c.X, cy + c.Y), true));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            _dc.DrawGeometry(brush, null, geometry);
        }

        private static (Point A, Point B, Point C) Corners(TriangleDirection direction, double s) => direction switch
        {
            TriangleDirection.Up => (new Point(0, -s), new Point(-s, s), new Point(s, s)),
            TriangleDirection.Down => (new Point(0, s), new Point(-s, -s), new Point(s, -s)),
            TriangleDirection.Left => (new Point(-s, 0), new Point(s, -s), new Point(s, s)),
            TriangleDirection.Right => (new Point(s, 0), new Point(-s, -s), new Point(-s, s)),
            TriangleDirection.UpLeft => (new Point(-s, -s), new Point(s * 0.6, -s * 0.2), new Point(-s * 0.2, s * 0.6)),
            TriangleDirection.UpRight => (new Point(s, -s), new Point(-s * 0.6, -s * 0.2), new Point(s * 0.2, s * 0.6)),
            TriangleDirection.DownLeft => (new Point(-s, s), new Point(-s * 0.2, -s * 0.6), new Point(s * 0.6, s * 0.2)),
            _ => (new Point(s, s), new Point(-s * 0.6, s * 0.2), new Point(s * 0.2, -s * 0.6)),
        };

        public void Ring(double x, double y, double r, double thickness, Color color)
        {
            var pen = new Pen(new SolidColorBrush(color), thickness);
            pen.Freeze();
            _dc.DrawEllipse(null, pen, new Point(x * _size, y * _size), r * _size, r * _size);
        }
    }
}
