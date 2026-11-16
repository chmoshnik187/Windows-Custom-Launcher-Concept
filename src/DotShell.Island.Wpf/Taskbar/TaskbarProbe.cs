namespace DotShell.Island.Wpf.Taskbar;

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using DotShell.Core.Native;

/// <summary>
/// Проба панели задач: свободные пробеги между иконками и цвет панели.
///
/// Зачем не UI Automation: у панели задач Windows 11 кнопки — не HWND, а
/// XAML-острова, и UIA на инсайдерских сборках ломается и стоит десятки мс.
/// Здесь проще и надёжнее — снять полоску панели пикселями и посчитать
/// «занятость» колонок по перепаду яркости:
///
///   • у иконок на границах резкий перепад яркости → колонка занята;
///   • акрил панели между иконками почти ровный → колонка свободна;
///   • непрерывные свободные пробеги — кандидаты, куда встать островом.
///
/// Возвращаем ВСЕ стоящие пробеги, а не один: панель живёт (поиск разворачивается,
/// иконки переставляются), и остров не должен прыгать с места на место из-за
/// одного кадра. Выбор с гистерезисом делает IslandWindow.
/// </summary>
public static class TaskbarProbe
{
    public sealed record GapCandidate(Rect Rect, Color Back, bool IsLight);

    public sealed record Snapshot(Rect Taskbar, IReadOnlyList<GapCandidate> Gaps, double Scale);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>Порог «занятой» колонки. Ниже — акрил без иконок.</summary>
    private const double OccupiedThreshold = 12;

    /// <summary>Свободный пробег короче этого не годится под капсулу.</summary>
    private const int MinGapPx = 220;

    public static Snapshot? Probe(Visual visual, Rect? excludeDip = null)
    {
        try
        {
            IntPtr hwnd = FindWindow("Shell_TrayWnd", null);
            if (hwnd == IntPtr.Zero) return null;

            // DPI панели берём у её окна: у скрытого WPF-окна VisualTreeHelper
            // может вернуть 1.0, и тогда исключение своего сегмента считалось
            // в физических пикселях как в DIP — проба подхватывала собственную
            // отрисовку и уходила в белый цвет.
            double scale = 1;
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { }
            if (scale <= 0) scale = 1;

            if (!GetWindowRect(hwnd, out var rect)) return null;
            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            // Поддерживаем горизонтальную панель; вертикальная (Win11 умеет с 2026)
            // сюда не пройдёт — IslandWindow тогда откатится к обычному низу.
            if (w < 200 || h < 20 || h > 200 || w < h * 2) return null;

            var capture = ScreenPixels.TryCapture(rect.Left, rect.Top, w, h);
            if (capture is null) return null;

            // 1) «Занятость» колонки: максимальный перепад с левым соседом.
            // Колонки, занятые нашим же островом, считаем свободными: иначе
            // проба видит собственную капсулу как «иконку» и рвёт пробег.
            int exLeft = -1, exRight = -2;
            if (excludeDip is { } ex)
            {
                exLeft = (int)Math.Floor(ex.Left * scale) - rect.Left;
                exRight = (int)Math.Ceiling(ex.Right * scale) - rect.Left;
            }

            var score = new double[w];
            for (int x = 1; x < w; x++)
            {
                if (x >= exLeft && x <= exRight) continue;
                double max = 0;
                for (int y = 2; y < h - 2; y += 2)
                {
                    double d = Math.Abs(capture.Luma(x, y) - capture.Luma(x - 1, y));
                    if (d > max) max = d;
                }
                score[x] = max;
            }

            // 2) Сглаживаем: одиночные пиксели не должны рвать пробег.
            var smooth = new double[w];
            for (int x = 0; x < w; x++)
            {
                if (x >= exLeft && x <= exRight) { smooth[x] = 0; continue; }
                double sum = 0;
                int n = 0;
                for (int k = -3; k <= 3; k++)
                {
                    int xx = x + k;
                    if (xx < 0 || xx >= w) continue;
                    sum += score[xx];
                    n++;
                }
                smooth[x] = sum / Math.Max(1, n);
            }

            // 3) Собираем все свободные пробеги.
            var runs = new List<(int Start, int Len)>();
            int runStart = -1;
            for (int x = 0; x <= w; x++)
            {
                bool free = x < w && smooth[x] < OccupiedThreshold;
                if (free)
                {
                    if (runStart < 0) runStart = x;
                }
                else if (runStart >= 0)
                {
                    int len = x - runStart;
                    if (len >= MinGapPx) runs.Add((runStart, len));
                    runStart = -1;
                }
            }

            if (runs.Count == 0) return null;

            var gaps = new List<GapCandidate>();
            foreach (var (start, len) in runs)
            {
                // Цвет берём из центральной полосы панели, но не из-под своего острова.
                int gx0 = start + len / 4;
                int gx1 = start + len * 3 / 4;
                if (exRight > exLeft)
                {
                    int leftPart = exLeft - 1 - start;
                    int rightPart = start + len - 1 - (exRight + 1);
                    if (leftPart >= rightPart && leftPart > 20)
                    {
                        gx0 = start;
                        gx1 = Math.Min(start + len / 2, exLeft - 1);
                    }
                    else if (rightPart > 20)
                    {
                        gx0 = Math.Max(start + len / 2, exRight + 1);
                        gx1 = start + len - 1;
                    }
                }
                int gy0 = Math.Max(2, h / 4);
                int gy1 = Math.Min(h - 3, h * 3 / 4);
                var (r, g, b) = ScreenPixels.Average(capture, gx0, gy0, gx1, gy1);
                double luma = r * 0.2126 + g * 0.7152 + b * 0.0722;

                gaps.Add(new GapCandidate(
                    new Rect((rect.Left + start) / scale, rect.Top / scale, len / scale, h / scale),
                    Color.FromRgb(r, g, b),
                    luma > 140));
            }

            // Сначала широкие: IslandWindow выбирает с гистерезисом, но по умолчанию
            // хочет самый вместительный пробег.
            gaps.Sort((a, b) => b.Rect.Width.CompareTo(a.Rect.Width));

            return new Snapshot(
                new Rect(rect.Left / scale, rect.Top / scale, w / scale, h / scale),
                gaps,
                scale);
        }
        catch
        {
            return null;
        }
    }
}
