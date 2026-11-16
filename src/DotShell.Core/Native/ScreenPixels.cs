namespace DotShell.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// Захват прямоугольника экрана в сырые BGRA-пиксели (GDI BitBlt + GetDIBits).
///
/// Нужен двум вещам, которым мало стандартных API:
///   • проба панели задач — где в ней свободное место между иконками и какого она
///     цвета (акрил панели зависит от обоев, поэтому цвет надо именно читать);
///   • адаптивный цвет острова в режиме «в панели задач».
///
/// Ходим через GetDC(NULL) и DIB-секцию: без System.Drawing и без WPF-битмапов,
/// чтобы можно было вызывать из ядра без UI-потока.
/// </summary>
public static class ScreenPixels
{
    public sealed class Capture
    {
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Stride { get; init; }
        public required byte[] Bgra { get; init; }

        /// <summary>Яркость 0..255 (BGR, взвешенно).</summary>
        public double Luma(int x, int y)
        {
            int i = y * Stride + x * 4;
            return (Bgra[i + 2] * 0.2126 + Bgra[i + 1] * 0.7152 + Bgra[i] * 0.0722);
        }

        public (byte R, byte G, byte B) Pixel(int x, int y)
        {
            int i = y * Stride + x * 4;
            return (Bgra[i + 2], Bgra[i + 1], Bgra[i]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] Colors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits,
        ref BitmapInfo bi, uint usage);

    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>Снять прямоугольник экрана. Координаты — физические пиксели.</summary>
    public static Capture? TryCapture(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || width > 20000 || height > 20000) return null;

        IntPtr screen = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;

        try
        {
            screen = GetDC(IntPtr.Zero);
            if (screen == IntPtr.Zero) return null;

            memDc = CreateCompatibleDC(screen);
            if (memDc == IntPtr.Zero) return null;

            bmp = CreateCompatibleBitmap(screen, width, height);
            if (bmp == IntPtr.Zero) return null;

            old = SelectObject(memDc, bmp);
            if (!BitBlt(memDc, 0, 0, width, height, screen, x, y, SRCCOPY)) return null;

            var bi = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height, // минус = строки сверху вниз, удобнее читать
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                },
                Colors = new uint[3],
            };

            int stride = width * 4;
            var bytes = new byte[stride * height];
            if (GetDIBits(memDc, bmp, 0, (uint)height, bytes, ref bi, 0) == 0) return null;

            return new Capture { Width = width, Height = height, Stride = stride, Bgra = bytes };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Средний цвет области (шаг 2 px, чтобы не читать каждый пиксель).</summary>
    public static (byte R, byte G, byte B) Average(Capture capture, int x0, int y0, int x1, int y1)
    {
        x0 = Math.Clamp(x0, 0, capture.Width - 1);
        y0 = Math.Clamp(y0, 0, capture.Height - 1);
        x1 = Math.Clamp(x1, x0, capture.Width - 1);
        y1 = Math.Clamp(y1, y0, capture.Height - 1);

        long r = 0, g = 0, b = 0, n = 0;
        for (int yy = y0; yy <= y1; yy += 2)
        {
            for (int xx = x0; xx <= x1; xx += 2)
            {
                var (pr, pg, pb) = capture.Pixel(xx, yy);
                r += pr;
                g += pg;
                b += pb;
                n++;
            }
        }

        if (n == 0) return (0, 0, 0);
        return ((byte)(r / n), (byte)(g / n), (byte)(b / n));
    }
}
