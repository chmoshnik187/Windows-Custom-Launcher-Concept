namespace DotShell.Island.Wpf.Desktop;

using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

/// <summary>
/// Обои текущего рабочего стола для «жидкого стекла»: читаем путь из
/// HKCU\Control Panel\Desktop и отдаём замороженный BitmapImage.
///
/// Важно: это не захват экрана. Мы берём сам файл обоев — поэтому нет ни
/// разрешений на запись экрана, ни постоянной нагрузки на GPU. Если обои
/// заданы сплошным цветом/Spotlight или файл пропал, возвращаем null —
/// панель в этом случае остаётся плоской, без «стекла из ничего».
/// </summary>
internal static class WallpaperSample
{
    private static BitmapImage? _cached;
    private static string? _cachedPath;
    private static DateTime _cachedStamp;

    public static BitmapImage? Load()
    {
        try
        {
            var path = ReadWallpaperPath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

            var stamp = File.GetLastWriteTimeUtc(path);
            if (_cachedPath == path && _cachedStamp == stamp && _cached is not null)
                return _cached;

            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnLoad;
            // Обои и так большие: ограничиваем декод, чтобы не держать 8K в памяти.
            image.DecodePixelWidth = 1920;
            image.EndInit();
            image.Freeze();

            _cached = image;
            _cachedPath = path;
            _cachedStamp = stamp;
            return image;
        }
        catch
        {
            return null;
        }
    }

    public static void Invalidate()
    {
        _cached = null;
        _cachedPath = null;
        _cachedStamp = default;
    }

    private static string? ReadWallpaperPath()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", false);
            var path = key?.GetValue("WallPaper") as string;
            if (!string.IsNullOrWhiteSpace(path)) return path;
        }
        catch { }

        try
        {
            var buffer = new char[1024];
            if (SystemParametersInfo(0x0073, buffer.Length, buffer, 0))
            {
                int end = Array.IndexOf(buffer, '\0');
                return end < 0 ? new string(buffer) : new string(buffer, 0, end);
            }
        }
        catch { }

        return null;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool SystemParametersInfo(uint action, int param, char[] buffer, uint winIni);
}
