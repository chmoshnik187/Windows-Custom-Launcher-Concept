namespace DotShell.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// Яркость экрана через гамма-коррекцию (GDI). Без сторонних пакетов и без WMI:
/// работает и на десктопе, и на ноутбуке, где WMI-яркость часто недоступна.
///
/// Ограничение честное: это гамма, а не подсветка матрицы. На 30 % картинка темнеет
/// и цвета уходят в тень — для «сделать потише» этого достаточно, для цветокоррекции нет.
/// Поэтому исходную кривую запоминаем и возвращаем при закрытии.
/// </summary>
public static class DisplayBrightness
{
    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    private static ushort[]? _original;

    /// <summary>
    /// Поставить гамму. factor 1.0 — как есть, 0.3 — заметно темнее.
    /// Возвращает false, если драйвер не дал применить (например, Remote Desktop).
    /// </summary>
    public static bool Set(double factor)
    {
        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return false;

            try
            {
                var ramp = new ushort[3 * 256];

                if (_original is null)
                {
                    var current = new ushort[3 * 256];
                    if (GetDeviceGammaRamp(hdc, current))
                        _original = (ushort[])current.Clone();
                }

                double scale = Math.Clamp(factor, 0.3, 1.0);

                for (int channel = 0; channel < 3; channel++)
                {
                    for (int i = 0; i < 256; i++)
                    {
                        // Квадратичная кривая ближе к человеческому восприятию, чем линейная.
                        double value = i / 255.0 * 65535.0 * scale;
                        ramp[channel * 256 + i] = (ushort)Math.Clamp(value, 0, 65535);
                    }
                }

                return SetDeviceGammaRamp(hdc, ramp);
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdc);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Вернуть исходную гамму (например, при выходе из программы).</summary>
    public static void Restore()
    {
        if (_original is null) return;

        try
        {
            var hdc = GetDC(IntPtr.Zero);
            if (hdc == IntPtr.Zero) return;

            try { SetDeviceGammaRamp(hdc, _original); }
            finally { ReleaseDC(IntPtr.Zero, hdc); }
        }
        catch { }

        _original = null;
    }
}
