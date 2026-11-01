namespace DotShell.Core.DotMatrix;

/// <summary>
/// ЯДРО ВСЕЙ ЭСТЕТИКИ. Единственная функция, которая делает "Nothing-вид":
/// сетка яркостей -> картинка из кружков.
///
/// На входе — массив brightness[0..1] размером cols*rows. Откуда он берётся,
/// этому модулю всё равно: из текста, из иконки приложения, из обложки альбома,
/// из кадра обоев. Поэтому один и тот же код красит и часы, и плеер, и лаунчер.
///
/// Считается на CPU в обычный массив байтов BGRA. Для сетки 60×16 это ~1000 точек —
/// доли миллисекунды. Если позже захочешь точки на весь экран (обои) — этот же
/// алгоритм переносится в pixel shader один в один, формула расстояния та же.
/// </summary>
public static class DotGrid
{
    public readonly record struct Geometry(int Cols, int Rows, int Width, int Height, int Pitch)
    {
        /// <summary>Сколько ячеек влезет в заданную ширину/высоту.</summary>
        public static Geometry FromSize(double width, double height, int cell, int gap)
        {
            int pitch = Math.Max(1, cell + gap);
            int cols = Math.Max(1, (int)Math.Floor(width / pitch));
            int rows = Math.Max(1, (int)Math.Floor(height / pitch));
            return new Geometry(cols, rows, cols * pitch, rows * pitch, pitch);
        }
    }

    /// <summary>
    /// Нарисовать сетку точек.
    /// </summary>
    /// <param name="brightness">Яркость каждой ячейки, 0..1, по строкам слева направо.</param>
    /// <param name="argb">Цвет "горящих" точек, 0xAARRGGBB.</param>
    /// <param name="ghostArgb">Цвет "погасших" точек сетки. null = не рисовать.</param>
    /// <param name="threshold">Яркость, ниже которой ячейка считается пустой.</param>
    /// <param name="minRadiusFactor">Минимальный радиус точки (доля ячейки) — чтобы тусклые пиксели не пропадали.</param>
    /// <returns>Массив BGRA8 размером Width*Height*4.</returns>
    public static byte[] Rasterize(
        float[] brightness,
        Geometry geo,
        uint argb,
        uint? ghostArgb = null,
        float threshold = 0.07f,
        float minRadiusFactor = 0.16f)
    {
        int w = geo.Width, h = geo.Height;
        var buffer = new byte[w * h * 4];

        byte aA = (byte)((argb >> 24) & 0xFF);
        byte aR = (byte)((argb >> 16) & 0xFF);
        byte aG = (byte)((argb >> 8) & 0xFF);
        byte aB = (byte)(argb & 0xFF);

        bool ghost = ghostArgb is not null && ghostArgb.Value != 0;
        byte gA = ghost ? (byte)((ghostArgb!.Value >> 24) & 0xFF) : (byte)0;
        byte gR = ghost ? (byte)((ghostArgb!.Value >> 16) & 0xFF) : (byte)0;
        byte gG = ghost ? (byte)((ghostArgb!.Value >> 8) & 0xFF) : (byte)0;
        byte gB = ghost ? (byte)(ghostArgb!.Value & 0xFF) : (byte)0;

        double halfPitch = geo.Pitch / 2.0;

        for (int py = 0; py < h; py++)
        {
            int cy = (int)(py / geo.Pitch);
            double dyCenter = py + 0.5 - (cy * geo.Pitch + halfPitch);
            int rowBase = py * w * 4;

            for (int px = 0; px < w; px++)
            {
                int cx = (int)(px / geo.Pitch);
                int idx = cy * geo.Cols + cx;
                if (idx < 0 || idx >= brightness.Length) continue;

                float v = brightness[idx];
                bool on = v >= threshold;
                if (!on && !ghost) continue;

                double dxCenter = px + 0.5 - (cx * geo.Pitch + halfPitch);
                double dist = Math.Sqrt(dxCenter * dxCenter + dyCenter * dyCenter);

                // Радиус: полная точка при яркости >= 0.55, дальше уменьшается.
                double factor = on
                    ? minRadiusFactor + (1.0 - minRadiusFactor) * Math.Clamp(v / 0.55, 0, 1)
                    : minRadiusFactor * 0.65;
                double radius = halfPitch * factor;
                if (dist > radius) continue;

                // Антиалиасинг: мягкий край в 1 px, чтобы точки не "звенели".
                double edge = Math.Clamp(radius - dist, 0, 1);
                double coverage = edge;

                // "Горящие" точки дополнительно гаснут вместе с яркостью источника.
                double alphaFactor = on ? Math.Clamp(0.35 + v, 0, 1) : 1.0;
                double alpha = coverage * alphaFactor;
                if (alpha <= 0.004) continue;

                int o = rowBase + px * 4;
                if (on)
                {
                    buffer[o + 0] = Blend(buffer[o + 0], aB, alpha);
                    buffer[o + 1] = Blend(buffer[o + 1], aG, alpha);
                    buffer[o + 2] = Blend(buffer[o + 2], aR, alpha);
                    buffer[o + 3] = Blend(buffer[o + 3], aA, alpha);
                }
                else
                {
                    buffer[o + 0] = Blend(buffer[o + 0], gB, alpha * 0.5);
                    buffer[o + 1] = Blend(buffer[o + 1], gG, alpha * 0.5);
                    buffer[o + 2] = Blend(buffer[o + 2], gR, alpha * 0.5);
                    buffer[o + 3] = Blend(buffer[o + 3], gA, alpha * 0.5);
                }
            }
        }

        return buffer;
    }

    private static byte Blend(byte dst, byte src, double alpha)
    {
        double a = Math.Clamp(alpha, 0, 1);
        return (byte)Math.Clamp(dst * (1 - a) + src * a, 0, 255);
    }

    /// <summary>
    /// Превратить пиксели (BGRA) в сетку яркостей: на каждую ячейку берём МАКСИМУМ
    /// альфы/яркости внутри неё. Максимум, а не среднее — иначе тонкие штрихи
    /// шрифта "растворяются" и цифры превращаются в кашу.
    /// </summary>
    public static float[] Sample(byte[] pixels, int pixelWidth, int pixelHeight, Geometry geo)
    {
        var grid = new float[geo.Cols * geo.Rows];
        if (pixelWidth <= 0 || pixelHeight <= 0) return grid;

        for (int r = 0; r < geo.Rows; r++)
        {
            int y0 = r * geo.Pitch;
            int y1 = Math.Min(pixelHeight, y0 + geo.Pitch);
            for (int c = 0; c < geo.Cols; c++)
            {
                int x0 = c * geo.Pitch;
                int x1 = Math.Min(pixelWidth, x0 + geo.Pitch);

                float max = 0;
                for (int y = y0; y < y1; y++)
                {
                    int rowBase = y * pixelWidth * 4;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = rowBase + x * 4;
                        if (i + 3 >= pixels.Length) continue;

                        byte b = pixels[i], g = pixels[i + 1], rCh = pixels[i + 2], a = pixels[i + 3];
                        // Если источник с альфой (текст) — верим альфе.
                        // Если без (фото/иконка) — считаем воспринимаемую яркость.
                        float value = a > 0
                            ? a / 255f
                            : (0.2126f * rCh + 0.7152f * g + 0.0722f * b) / 255f;
                        if (value > max) max = value;
                    }
                }
                grid[r * geo.Cols + c] = max;
            }
        }
        return grid;
    }
}
