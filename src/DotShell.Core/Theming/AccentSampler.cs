namespace DotShell.Core.Theming;

using System.Drawing;
using System.Drawing.Imaging;
using DotShell.Core.Native;

/// <summary>
/// Авто-цвет из обоев: раз в N секунд берём кадр рабочего стола,
/// скармливаем его ColorExtractor и плавно двигаем акцент в ThemeBus.
///
/// Три вещи, которые тут важны и которые легко упустить:
///
///  1. НЕ захватывать собственные окна. Иначе возникает петля обратной связи:
///     акцент покрасил остров → сэмплер увидел красный остров → акцент стал ещё краснее.
///     Поэтому список своих HWND передаётся снаружи и "затирается" серым.
///
///  2. Не считать цвет, когда десктоп не виден (что-то в полноэкранном режиме).
///     Кадр всё равно будет чёрным, и тема уползёт в грязь.
///
///  3. Сглаживание. ColorExtractor возвращает "цель", а ThemeBus.EaseToward
///     двигает текущий цвет к ней маленькими шагами — видео-обои не будут
///     заставлять интерфейс мигать.
/// </summary>
public sealed class AccentSampler : IDisposable
{
    private readonly ThemeBus _bus;
    private readonly Func<bool> _shouldSample;
    private readonly Func<IReadOnlyList<IntPtr>> _windowsToIgnore;
    private readonly System.Timers.Timer _timer;

    /// <summary>Последняя измеренная уверенность (0..1) — показываем в отладочной панели.</summary>
    public double LastConfidence { get; private set; }

    /// <summary>Откуда взят цвет: «обои WE: <название>» или «рабочий стол».</summary>
    public string Source { get; private set; } = "рабочий стол";

    /// <summary>Срабатывает после каждого удачного замера. Аргумент — целевой цвет.</summary>
    public event Action<uint>? Sampled;

    public AccentSampler(
        ThemeBus bus,
        int intervalMs = 5000,
        Func<bool>? shouldSample = null,
        Func<IReadOnlyList<IntPtr>>? windowsToIgnore = null)
    {
        _bus = bus;
        _shouldSample = shouldSample ?? (() => true);
        _windowsToIgnore = windowsToIgnore ?? (() => Array.Empty<IntPtr>());
        _timer = new System.Timers.Timer(Math.Max(1000, intervalMs));
        _timer.Elapsed += (_, _) => SampleOnce();
        _timer.AutoReset = true;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();

    public void SampleOnce()
    {
        if (!_bus.Settings.AutoAccent) return;
        if (!_shouldSample()) return;

        try
        {
            // Сначала обои Wallpaper Engine: их превью есть на диске, поэтому цвет
            // берётся даже когда рабочий стол закрыт окнами или игрой.
            // Если WE нет — падаем на снимок рабочего стола, как раньше.
            var wallpaper = DotShell.Core.Wallpaper.WallpaperEngine.Current();
            (byte[]? pixels, int width, int height) frame = (null, 0, 0);

            if (wallpaper is { PreviewPath.Length: > 0 } && File.Exists(wallpaper.PreviewPath))
            {
                frame = LoadImageAsBgra(wallpaper.PreviewPath);
                Source = $"обои WE: {wallpaper.Title}";
            }

            if (frame.pixels is null)
            {
                frame = CaptureDesktop(exclude: _windowsToIgnore());
                var why = DotShell.Core.Wallpaper.WallpaperEngine.LastError;
                Source = string.IsNullOrEmpty(why) ? "рабочий стол" : $"рабочий стол (WE: {why})";
            }

            var (pixels, w, h) = frame;
            if (pixels is null) return;

            var result = ColorExtractor.FromBgra(pixels, w, h);
            LastConfidence = result.Confidence;

            // Совсем низкая уверенность = обои однотонные/серые.
            // Тогда цвет не трогаем, чтобы интерфейс не стал серым.
            if (result.Confidence < 0.06) return;

            _bus.EaseToward(result.Accent);
            Sampled?.Invoke(result.Accent);
        }
        catch
        {
            // Захват экрана может не работать на защищённом контенте (DRM) — это норма.
        }
    }

    /// <summary>
    /// Снимок рабочего стола, уменьшенный до ~64 px по меньшей стороне.
    /// Возвращает байты BGRA + размеры уменьшенной картинки.
    ///
    /// Реализация: один захват в полном разрешении, затем "точечный" даунсэмпл
    /// (берём по одному пикселю на ячейку). Для подбора цвета этого более чем достаточно,
    /// зато не плодим второй битмап и не зависим от капризов GDI+.
    /// </summary>
    /// <summary>
    /// Картинка с диска → маленький BGRA-кадр (до 128 px по большей стороне).
    /// Превью обоев бывает 4K, а для подбора цвета хватает и сотни пикселей.
    /// </summary>
    public static (byte[]? pixels, int width, int height) LoadImageAsBgra(string path, int maxSide = 128)
    {
        try
        {
            using var source = new Bitmap(path);
            double scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
            int w = Math.Max(1, (int)(source.Width * scale));
            int h = Math.Max(1, (int)(source.Height * scale));

            using var small = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                g.DrawImage(source, 0, 0, w, h);
            }

            var data = small.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[w * h * 4];
                for (int y = 0; y < h; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * w * 4, w * 4);
                return (bytes, w, h);
            }
            finally
            {
                small.UnlockBits(data);
            }
        }
        catch
        {
            return (null, 0, 0);
        }
    }
    public static (byte[]? pixels, int width, int height) CaptureDesktop(IReadOnlyList<IntPtr> exclude)
    {
        int vw = Win32.GetSystemMetrics(Win32.SM_CXVIRTUALSCREEN);
        int vh = Win32.GetSystemMetrics(Win32.SM_CYVIRTUALSCREEN);
        if (vw <= 0 || vh <= 0) return (null, 0, 0);

        using var shot = new Bitmap(vw, vh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot))
            g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(vw, vh), CopyPixelOperation.SourceCopy);

        var data = shot.LockBits(new Rectangle(0, 0, vw, vh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = data.Stride;
            var src = new byte[stride * vh];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, src, 0, src.Length);

            double scale = 64.0 / Math.Min(vw, vh);
            int tw = Math.Max(8, (int)(vw * scale));
            int th = Math.Max(8, (int)(vh * scale));
            var dst = new byte[tw * th * 4];

            // Прямоугольники, которые надо игнорировать (в координатах уменьшенной картинки).
            var holes = new List<Rectangle>();
            foreach (var hwnd in exclude)
            {
                if (hwnd == IntPtr.Zero) continue;
                if (!Win32.GetWindowRect(hwnd, out var r)) continue;
                if (r.Width <= 0 || r.Height <= 0) continue;
                holes.Add(new Rectangle(
                    (int)(r.Left * scale) - 2, (int)(r.Top * scale) - 2,
                    (int)(r.Width * scale) + 4, (int)(r.Height * scale) + 4));
            }

            int stepX = Math.Max(1, vw / tw);
            int stepY = Math.Max(1, vh / th);

            for (int y = 0; y < th; y++)
            {
                int sy = Math.Min(vh - 1, y * stepY);
                for (int x = 0; x < tw; x++)
                {
                    int sx = Math.Min(vw - 1, x * stepX);
                    int si = sy * stride + sx * 4;
                    int di = (y * tw + x) * 4;

                    bool inHole = false;
                    for (int k = 0; k < holes.Count; k++)
                        if (holes[k].Contains(x, y)) { inHole = true; break; }

                    if (inHole)
                    {
                        // Нейтральный серый: ColorExtractor отбросит его по низкой насыщенности.
                        dst[di + 0] = 128; dst[di + 1] = 128; dst[di + 2] = 128; dst[di + 3] = 255;
                    }
                    else
                    {
                        dst[di + 0] = src[si + 0];
                        dst[di + 1] = src[si + 1];
                        dst[di + 2] = src[si + 2];
                        dst[di + 3] = 255;
                    }
                }
            }

            return (dst, tw, th);
        }
        finally
        {
            shot.UnlockBits(data);
        }
    }

    public void Dispose() => _timer.Dispose();
}
