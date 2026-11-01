namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core.Audio;
using DotShell.Core.DotMatrix;

/// <summary>
/// Мини-эквалайзер-"сталактиты": полоски точек свисают СНИЗУ свёрнутой таблетки,
/// как сосульки с потолка пещеры. Фон полностью прозрачный — только точки,
/// поэтому выглядит будто музыка капает из острова.
///
/// Живёт только пока играет музыка и остров свёрнут: в остальных случаях
/// таймер остановлен, битмап пуст и пикселей нет (клики проходят сквозь окно).
///
/// ЧЕСТНОЕ ОГРАНИЧЕНИЕ: уровни — правдоподобная имитация (волны + случайное
/// блуждание с уклоном в низкие частоты), а не настоящий спектр. Захват звука
/// (WASAPI loopback) — следующий шаг, см. ROADMAP. Движок точек тот же,
/// что и у большого EQ, поэтому смотрится как его младший брат.
/// </summary>
public sealed class MiniEq : FrameworkElement
{
    private const int Bands = 40;

    private readonly DispatcherTimer _timer;

    /// <summary>Хочет ли владелец (остров) анимацию прямо сейчас.</summary>
    private bool _wantRun;
    private readonly Random _random = new();
    private readonly float[] _levels = new float[Bands];

    /// <summary>Пики: головка падает медленнее полосы — глаз читает ритм,
    /// а не мельтешение. Тот же приём, что в аппаратных анализаторах спектра.</summary>
    private readonly float[] _peaks = new float[Bands];
    private readonly float[] _phase = new float[Bands];
    private WriteableBitmap? _bitmap;

    public bool IsRunning
    {
        get => _timer.IsEnabled;
        set
        {
            _wantRun = value;
            if (value && !_timer.IsEnabled) { _timer.Start(); Render(); }
            else if (!value && _timer.IsEnabled) { _timer.Stop(); ZeroAndRender(); }
        }
    }

    public MiniEq()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => { Step(); Render(); };

        for (int i = 0; i < Bands; i++) _phase[i] = (float)(_random.NextDouble() * Math.PI * 2);

        SizeChanged += (_, _) => { _bitmap = null; Render(); };
        Unloaded += (_, _) => _timer.Stop();

        // Контролы пересоздаются при перестройке острова: старый Unloaded гасил
        // таймер, и после возврата полоска оставалась мёртвой.
        Loaded += (_, _) => { if (_wantRun && !_timer.IsEnabled) _timer.Start(); };
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(Render);
        DotStyle.Changed += () => Dispatcher.BeginInvoke(Render);
    }

    /// <summary>
    /// Размер по месту, а не «навсегда 240×64»: в острове контролу задают
    /// размер явно, а в виджете он должен тянуться вместе с окном — иначе
    /// растянутый виджет показывает полоску прижатой к углу («поплыл»).
    /// </summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 240 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 64 : availableSize.Height;
        return new Size(Math.Max(60, width), Math.Max(20, height));
    }

    private void Step()
    {
        var spectrum = AudioSpectrum.Current;
        // Живые полосы — только когда реально есть сигнал. Если захват жив, но в
        // системе тишина, показываем медленное «дыхание» вместо застывших нулей.
        if (spectrum is { IsRunning: true, HasSignal: true })
        {
            var bands = spectrum.Levels;
            for (int i = 0; i < Bands; i++)
            {
                float target = bands[Math.Clamp(i * bands.Length / Bands, 0, bands.Length - 1)];
                // Атака мгновенная, спад плавный: так двигается живой звук.
                // Лёгкое сжатие динамики (степень 0.85) поднимает тихие полосы,
                // но не превращает всё в сплошную стену.
                target = MathF.Pow(Math.Clamp(target, 0f, 1f), 0.85f);
                _levels[i] = target > _levels[i] ? target : _levels[i] * 0.72f + target * 0.28f;
                _peaks[i] = Math.Max(_levels[i], _peaks[i] - 0.015f);
            }
            return;
        }

        // Тишина или захват недоступен: тихая имитация, чтобы полоска жила,
        // но не делала вид, что играет музыка.
        bool silent = spectrum is { IsRunning: true } && !spectrum.HasSignal;
        double t = Environment.TickCount / 1000.0;
        for (int i = 0; i < Bands; i++)
        {
            float bias = 1.0f - (i / (float)Bands) * 0.5f;                 // басы активнее
            float wave = (float)(0.5 + 0.5 * Math.Sin(t * (2.2 + i * 0.31) + _phase[i]));
            float amplitude = silent ? 0.22f : 1.0f;
            float target = Math.Clamp(bias * (0.35f + 0.65f * wave) * amplitude * (0.6f + 0.4f * (float)_random.NextDouble()), 0.02f, 1.0f);
            _levels[i] += (target - _levels[i]) * 0.45f;                  // плавность, без дёргания
            _peaks[i] = Math.Max(_levels[i], _peaks[i] - 0.02f);
        }
    }

    private void ZeroAndRender()
    {
        Array.Clear(_levels);
        Array.Clear(_peaks);
        Render();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (_bitmap is not null)
            dc.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth / Dpi(), _bitmap.PixelHeight / Dpi()));
    }

    private void Render()
    {
        if (ActualWidth < 8 || ActualHeight < 4) return;

        double scale = Dpi();
        // Точки нарочно крупнее половины базового размера: сталактиты висят
        // на прозрачном фоне и должны читаться с расстояния, а не быть блёклыми.
        int cell = Math.Max(2, (int)Math.Round(DotStyle.Cell * 0.75 * scale));
        int gap = Math.Max(1, (int)Math.Round(Math.Max(1, DotStyle.Gap) * 0.5 * scale));
        int pitch = cell + gap;

        int cols = Math.Max(8, (int)(ActualWidth * scale / pitch));
        int rows = Math.Max(3, (int)(ActualHeight * scale / pitch));
        var geo = new DotGrid.Geometry(cols, rows, cols * pitch, rows * pitch, pitch);

        var brightness = new float[cols * rows];
        bool anyLit = false;
        for (int c = 0; c < cols; c++)
        {
            int band = Math.Clamp((int)((double)c / cols * Bands), 0, Bands - 1);
            float level = _levels[band];
            // Степень >1 делает сосульки разной длины: громко — почти до низа,
            // тихо — короткий сталактит. Без неё низ звучит «стеной».
            int lit = (int)Math.Round(Math.Pow(level, 1.15) * rows);

            // Сталактит: растёт СВЕРХУ вниз. Головка яркая, хвост плавно гаснет —
            // получается сосулька, а не палка из одинаковых точек.
            for (int r = 0; r < lit; r++)
            {
                int fromHead = lit - 1 - r;
                brightness[r * cols + c] = 0.18f + 0.82f * (float)Math.Pow(0.78, fromHead);
                anyLit = true;
            }

            // Пик висит ниже головки и медленно опадает — как на настоящем анализаторе.
            int peakRow = (int)Math.Round(_peaks[band] * rows);
            if (peakRow - lit >= 1 && peakRow < rows)
            {
                brightness[peakRow * cols + c] = 1.0f;
                anyLit = true;
            }
        }

        if (!anyLit) { _bitmap = null; InvalidateVisual(); return; }

        uint on = ColorPalette.ToArgb(ColorPalette.Accent);
        var pixels = DotGrid.Rasterize(brightness, geo, on, null); // ghost=null: фон абсолютно прозрачный

        if (_bitmap is null || _bitmap.PixelWidth != geo.Width || _bitmap.PixelHeight != geo.Height)
            _bitmap = new WriteableBitmap(geo.Width, geo.Height, 96 * scale, 96 * scale, PixelFormats.Bgra32, null);

        _bitmap.WritePixels(new Int32Rect(0, 0, geo.Width, geo.Height), pixels, geo.Width * 4, 0);
        InvalidateVisual();
    }

    private double Dpi()
    {
        try
        {
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget is not null) return src.CompositionTarget.TransformToDevice.M11;
        }
        catch { }
        return 1.0;
    }
}


