namespace DotShell.Core.Theming;

/// <summary>
/// Подбор акцентного цвета из картинки (обоев).
///
/// Как это работает (и почему именно так):
///  1. Берём пиксели, уменьшенные до ~48×48 — детали не важны, важен общий колорит.
///  2. Отбрасываем серое и почти чёрное/белое: из них акцент не получается,
///     иначе на чёрно-белых обоях всё станет грязно-серым.
///  3. Раскладываем оставшиеся по "корзинам" оттенка (hue) шагом 10°.
///  4. Побеждает корзина с наибольшим весом = количество × средняя насыщенность.
///     Так мы берём не "самый частый", а "самый характерный" цвет.
///  5. Возвращаем средний цвет корзины, слегка подтянув насыщенность и яркость —
///     на тёмном фоне акцент должен читаться.
///
/// Дополнительно возвращаем палитру из 5 цветов — она понадобится темам,
/// которые красят не только акцент, но и вторичные элементы.
/// </summary>
public static class ColorExtractor
{
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public uint ToArgb() => 0xFF000000u | ((uint)R << 16) | ((uint)G << 8) | B;
    }

    public sealed class Result
    {
        public required uint Accent { get; init; }
        public required uint[] Palette { get; init; }
        /// <summary>Насколько мы уверены (0..1). Низкая = обои почти однотонные/серые.</summary>
        public required double Confidence { get; init; }
    }

    /// <param name="bgra">Пиксели в формате BGRA8 (именно так отдаёт RenderTargetBitmap).</param>
    public static Result FromBgra(byte[] bgra, int width, int height)
    {
        // --- 1. корзины по оттенку ---
        const int buckets = 36; // 360° / 10°
        var count = new int[buckets];
        var sumSat = new double[buckets];
        var sumR = new long[buckets];
        var sumG = new long[buckets];
        var sumB = new long[buckets];

        // --- для палитры: грубое квантование RGB (4 уровня на канал = 64 корзины) ---
        var qCount = new int[64];
        var qR = new long[64];
        var qG = new long[64];
        var qB = new long[64];

        int considered = 0;

        // Шаг выборки: не идём по каждому пикселю, это лишняя работа.
        int step = Math.Max(1, Math.Min(width, height) / 64);

        for (int y = 0; y < height; y += step)
        for (int x = 0; x < width; x += step)
        {
            int i = (y * width + x) * 4;
            if (i + 3 >= bgra.Length) continue;

            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];

            // квантованная палитра — считаем по всем пикселям
            int qi = ((r >> 6) << 4) | ((g >> 6) << 2) | (b >> 6);
            qCount[qi]++; qR[qi] += r; qG[qi] += g; qB[qi] += b;

            var (h, s, v) = RgbToHsv(r, g, b);
            if (s < 0.16 || v < 0.10 || v > 0.97) continue; // серое / чёрное / белое

            int bi = (int)(h / 10.0) % buckets;
            count[bi]++;
            sumSat[bi] += s;
            sumR[bi] += r; sumG[bi] += g; sumB[bi] += b;
            considered++;
        }

        // --- 4. выбираем победителя ---
        int best = -1;
        double bestScore = 0;
        for (int i = 0; i < buckets; i++)
        {
            if (count[i] == 0) continue;
            double score = count[i] * (sumSat[i] / count[i]);
            if (score > bestScore) { bestScore = score; best = i; }
        }

        var palette = BuildPalette(qCount, qR, qG, qB);

        if (best < 0)
        {
            // Обои полностью серые/чёрные — берём самый частый цвет и делаем его чуть светлее.
            return new Result { Accent = palette[0], Palette = palette, Confidence = 0.05 };
        }

        double n = count[best];
        double ar = sumR[best] / n, ag = sumG[best] / n, ab = sumB[best] / n;
        var (ah, asat, av) = RgbToHsv((byte)ar, (byte)ag, (byte)ab);

        // --- 5. доводим до "акцента": насыщеннее и не слишком тёмный ---
        asat = Math.Clamp(asat * 1.25, 0.35, 0.95);
        av = Math.Clamp(Math.Max(av, 0.55), 0.55, 0.98);
        var (fr, fg, fb) = HsvToRgb(ah, asat, av);

        double confidence = Math.Clamp(considered / 400.0, 0, 1);
        return new Result { Accent = new Rgb(fr, fg, fb).ToArgb(), Palette = palette, Confidence = confidence };
    }

    private static uint[] BuildPalette(int[] qCount, long[] qR, long[] qG, long[] qB)
    {
        var order = Enumerable.Range(0, qCount.Length).OrderByDescending(i => qCount[i]).Take(5).ToList();
        var result = new uint[5];
        for (int k = 0; k < 5; k++)
        {
            if (k < order.Count && qCount[order[k]] > 0)
            {
                int i = order[k], c = qCount[i];
                result[k] = new Rgb((byte)(qR[i] / c), (byte)(qG[i] / c), (byte)(qB[i] / c)).ToArgb();
            }
            else
            {
                result[k] = 0xFF1A1A1C;
            }
        }
        return result;
    }

    public static (double h, double s, double v) RgbToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = ((g - b) / d) % 6;
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
            if (h < 0) h += 360;
        }
        double s = max < 1e-9 ? 0 : d / max;
        return (h, s, max);
    }

    public static (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r, g, b;
        switch ((int)(h / 60) % 6)
        {
            case 0: (r, g, b) = (c, x, 0); break;
            case 1: (r, g, b) = (x, c, 0); break;
            case 2: (r, g, b) = (0, c, x); break;
            case 3: (r, g, b) = (0, x, c); break;
            case 4: (r, g, b) = (x, 0, c); break;
            default: (r, g, b) = (c, 0, x); break;
        }
        return ((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
