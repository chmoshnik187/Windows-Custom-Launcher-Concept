namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core.Audio;
using DotShell.Core.DotMatrix;

/// <summary>
/// Эквалайзер из точек.
///
/// Здесь важно не "как получить данные об аудио" (это шаг 5 роадмапа, где мы
/// подключаемся к Equalizer APO), а то, что визуализация работает на ТОМ ЖЕ
/// движке DotGrid, что и часы, и обложка, и будущие иконки. Весь интерфейс
/// собран из одного кирпича — именно это считывается как единый стиль.
///
/// Сейчас полосы живут на случайном блуждании с уклоном в низкие частоты,
/// поэтому выглядит похоже на настоящую музыку. Когда подключим анализатор,
/// заменится только источник массива _levels.
///
/// ГЛАВНОЕ ПРАВИЛО: анимация обязана останавливаться, когда её никто не видит.
/// Иначе вкладка EQ будет жечь процессор, даже когда остров свёрнут.
/// </summary>
public sealed class EqDots : FrameworkElement
{
    private readonly DispatcherTimer _timer;
    private readonly Random _random = new();
    private readonly float[] _levels = new float[20];
    private WriteableBitmap? _bitmap;

    public bool IsRunning
    {
        get => _timer.IsEnabled;
        set
        {
            if (value && !_timer.IsEnabled) { Step(); _timer.Start(); Render(); }
            else if (!value && _timer.IsEnabled) _timer.Stop();
        }
    }

    public EqDots()
    {
        // 70 мс ≈ 14 кадров в секунду. Для эквалайзера этого достаточно,
        // а процессор грузит вчетверо меньше, чем 60 fps.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _timer.Tick += (_, _) => { Step(); Render(); };

        for (int i = 0; i < _levels.Length; i++) _levels[i] = 0.2f;

        SizeChanged += (_, _) => { _bitmap = null; Render(); };
        Unloaded += (_, _) => _timer.Stop();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(Render);
        DotStyle.Changed += () => Dispatcher.BeginInvoke(Render);
    }

    private void Step()
    {
        // Настоящий спектр: полосы из WASAPI loopback-анализатора ядра.
        var spectrum = AudioSpectrum.Current;
        if (spectrum is { IsRunning: true })
        {
            var bands = spectrum.Levels;
            for (int i = 0; i < _levels.Length; i++)
            {
                float target = bands[Math.Clamp(i * bands.Length / _levels.Length, 0, bands.Length - 1)];
                // Атака мгновенная, спад плавный: глаз читает ритм, а не мельтешение.
                _levels[i] = target > _levels[i] ? target : _levels[i] * 0.85f + target * 0.15f;
            }
            return;
        }

        // Захват недоступен — честная имитация (подписана и в интерфейсе).
        for (int i = 0; i < _levels.Length; i++)
        {
            float bias = 1.0f - (i / (float)_levels.Length) * 0.45f;
            _levels[i] += (float)(_random.NextDouble() - 0.5) * 0.28f;
            _levels[i] = Math.Clamp(_levels[i] * 0.92f + 0.08f * bias, 0.05f, 1.0f);
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        // ВАЖНО: здесь нельзя звать Render() — он заканчивается InvalidateVisual(),
        // а тот снова приводит в OnRender. Получался вечный самоподдерживающийся
        // цикл перерисовок на максимальной скорости: пока открыта вкладка EQ,
        // UI-поток молотил без остановки (жалоба "остров зависает").
        // Кадр рисует только таймер; OnRender лишь кладёт готовый битмап.
        base.OnRender(drawingContext);
        if (_bitmap is not null)
            drawingContext.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth / DpiScale(), _bitmap.PixelHeight / DpiScale()));
    }

    private void Render()
    {
        if (ActualWidth < 8 || ActualHeight < 8) return;

        double scale = DpiScale();
        int cell = Math.Max(2, (int)Math.Round(DotStyle.Cell * scale));
        int gap = (int)Math.Round(DotStyle.Gap * scale);
        int pitch = cell + gap;

        int cols = Math.Max(4, (int)(ActualWidth * scale / pitch));
        int rows = Math.Max(4, (int)(ActualHeight * scale / pitch));
        var geo = new DotGrid.Geometry(cols, rows, cols * pitch, rows * pitch, pitch);

        var brightness = new float[cols * rows];
        for (int c = 0; c < cols; c++)
        {
            int band = (int)((double)c / cols * _levels.Length);
            float level = _levels[Math.Clamp(band, 0, _levels.Length - 1)];
            int litRows = (int)Math.Round(level * rows);

            for (int r = 0; r < rows; r++)
            {
                // r = 0 — верхняя строка, а растём снизу, поэтому переворачиваем индекс.
                int fromBottom = rows - 1 - r;
                if (fromBottom >= litRows) continue;

                // "Головка" полосы ярче хвоста — так читается уровень.
                bool isHead = fromBottom == litRows - 1;
                brightness[r * cols + c] = isHead ? 1.0f : 0.42f;
            }
        }

        uint on = ColorPalette.ToArgb(ColorPalette.Accent);
        uint? ghost = ColorPalette.GhostDotColor.A == 0 ? null : ColorPalette.ToArgb(ColorPalette.GhostDotColor);
        var pixels = DotGrid.Rasterize(brightness, geo, on, ghost);

        if (_bitmap is null || _bitmap.PixelWidth != geo.Width || _bitmap.PixelHeight != geo.Height)
        {
            _bitmap = new WriteableBitmap(geo.Width, geo.Height, 96 * scale, 96 * scale,
                PixelFormats.Bgra32, null);
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, geo.Width, geo.Height), pixels, geo.Width * 4, 0);
        InvalidateVisual();
    }

    private double DpiScale()
    {
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null)
                return source.CompositionTarget.TransformToDevice.M11;
        }
        catch { }
        return 1.0;
    }
}
