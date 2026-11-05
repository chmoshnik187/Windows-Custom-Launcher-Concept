namespace DotShell.Island.Wpf.Desktop;

using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

/// <summary>
/// Монохромные иконки рабочего стола.
///
/// Как это работает: у ярлыка (.lnk) есть поле IconLocation — строка вида
/// «C:\...\app.exe,0». Мы вытаскиваем настоящую иконку, перерисовываем её в один цвет
/// с сохранением силуэта (яркость → альфа), кладём .ico в свою папку и подменяем
/// IconLocation на этот файл. Оригинальное значение запоминаем, чтобы вернуть всё как
/// было — без этого «поигрался и забыл» превращается в «сломал рабочий стол».
///
/// Сознательное решение: не трогаем содержимое ярлыков, только картинку. Ссылки,
/// аргументы, рабочие папки остаются нетронутыми.
/// </summary>
public static class DesktopIcons
{
    /// <summary>Где лежат наши .ico и карта оригиналов.</summary>
    private static string CacheDir => Path.Combine(
        DotShell.Core.Settings.AppSettings.SettingsDirectory, "desktop-icons");

    private static string MapPath => Path.Combine(CacheDir, "originals.json");

    // ---------- Win32 ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] icons, uint[] ids, uint count, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private const int ShcneAssocChanged = 0x08000000;

    // ---------- ярлыки ----------

    /// <summary>Ярлыки на рабочем столе: личный и общий. Только верхний уровень.</summary>
    public static List<string> Shortcuts(string? folder = null)
    {
        var result = new List<string>();

        var folders = folder is null
            ? new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            }
            : new[] { folder };

        foreach (var f in folders)
        {
            try
            {
                    if (string.IsNullOrEmpty(f) || !Directory.Exists(f)) continue;
                    result.AddRange(Directory.GetFiles(f, "*.lnk", SearchOption.TopDirectoryOnly));
            }
            catch { }
        }

        return result;
    }

    /// <summary>Перекрасить ярлыки в один цвет. Возвращает число перекрашенных.</summary>
    public static int MakeMonochrome(IEnumerable<string> links, Color tint, out List<string> failed)
    {
        Directory.CreateDirectory(CacheDir);
        var originals = LoadMap();
        var done = 0;
        failed = new List<string>();

        foreach (var link in links)
        {
            try
            {
                var info = ReadShortcut(link);
                if (info is null) { failed.Add(link); continue; }

                // Уже наш файл — повторно не перекрашиваем, но оригинал помним.
                if (!originals.ContainsKey(link)) originals[link] = info.Value.IconLocation;

                var png = ExtractGrayIcon(info.Value, tint);
                if (png is null) { failed.Add(link); continue; }

                // Оттенок входит в имя файла: при смене акцента получается новый .ico,
                // и проводник Windows перечитывает иконку. С прежним именем он держал
                // старую копию из своего кэша, и цвет не менялся до перезагрузки.
                var icoPath = Path.Combine(CacheDir, SafeName(link) + "-" + TintKey(tint) + ".ico");
                WriteIco(icoPath, png);
                WriteShortcutIcon(link, icoPath);
                done++;
            }
            catch
            {
                failed.Add(link);
            }
        }

        SaveMap(originals);
        RefreshShell();
        return done;
    }

    /// <summary>Вернуть ярлыкам их прежние иконки.</summary>
    public static int Restore(out List<string> failed)
    {
        failed = new List<string>();
        var originals = LoadMap();
        var done = 0;

        foreach (var (link, icon) in originals.ToList())
        {
            try
            {
                if (!File.Exists(link)) continue;
                WriteShortcutIcon(link, icon);
                done++;
            }
            catch { failed.Add(link); }
        }

        try { if (File.Exists(MapPath)) File.Delete(MapPath); } catch { }
        RefreshShell();
        return done;
    }

    /// <summary>Сколько ярлыков сейчас в монохроме по нашей карте.</summary>
    public static int CountManaged()
    {
        try { return LoadMap().Count; } catch { return 0; }
    }

    // ---------- иконки ----------

    /// <summary>Короткий ключ оттенка для имени файла кэша.</summary>
    private static string TintKey(Color tint) =>
        $"{tint.R:x2}{tint.G:x2}{tint.B:x2}";

    private static byte[]? ExtractGrayIcon(ShortcutInfo info, Color tint)
    {
        var source = info.IconSource();
        if (string.IsNullOrEmpty(source)) return null;

        // Достаём крупную иконку: 256 подходит и для плиток, и для «Крупных значков».
        var icons = new IntPtr[1];
        var ids = new uint[1];
        uint extracted = PrivateExtractIcons(source, info.IconIndex, 256, 256, icons, ids, 1, 0);
        if (extracted == 0 || icons[0] == IntPtr.Zero)
        {
            extracted = PrivateExtractIcons(source, info.IconIndex, 48, 48, icons, ids, 1, 0);
            if (extracted == 0 || icons[0] == IntPtr.Zero) return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            return EncodeGray(bitmap, tint);
        }
        finally
        {
            if (icons[0] != IntPtr.Zero) DestroyIcon(icons[0]);
        }
    }

    /// <summary>
    /// Иконка → один цвет. Альфа = яркость пикселя (с подъёмом контраста), поэтому
    /// светлые части становятся плотными, фон исчезает.
    /// </summary>
    private static byte[]? EncodeGray(BitmapSource source, Color tint)
    {
        try
        {
            int w = source.PixelWidth, h = source.PixelHeight;
            if (w < 4 || h < 4) return null;

            // Приводим к 32-битному виду, чтобы читать пиксели единообразно.
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var pixels = new byte[stride * h];
            converted.CopyPixels(pixels, stride, 0);

            var output = new byte[stride * h];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];

                // Взвешенная яркость (как считает глаз), нормированная на альфу.
                double alpha = a / 255.0;
                double luma = (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255.0 * alpha;

                // Подъём контраста: без него полупрозрачные края иконок исчезают.
                luma = Math.Clamp((luma - 0.12) / 0.76, 0, 1);
                byte alphaOut = (byte)Math.Round(255 * Math.Pow(luma, 0.85));

                output[i] = (byte)(tint.B * alphaOut / 255);
                output[i + 1] = (byte)(tint.G * alphaOut / 255);
                output[i + 2] = (byte)(tint.R * alphaOut / 255);
                output[i + 3] = alphaOut;
            }

            var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, output, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch { return null; }
    }

    /// <summary>
    /// ICO с PNG-слоями (Vista+ понимает): 256, 48, 32, 16. Разные размеры нужны, иначе
    /// Windows растягивает один и иконка мылится в «Мелких значках».
    /// </summary>
    private static void WriteIco(string path, byte[] png256)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        var layers = new List<byte[]>
        {
            png256,
            Downscale(png256, 48),
            Downscale(png256, 32),
            Downscale(png256, 16),
        };

        writer.Write((ushort)0);              // reserved
        writer.Write((ushort)1);              // type: icon
        writer.Write((ushort)layers.Count);

        int offset = 6 + 16 * layers.Count;

        foreach (var layer in layers)
        {
            var frame = BitmapFrame.Create(new MemoryStream(layer));
            int size = Math.Max(1, Math.Min(256, frame.PixelWidth));

            writer.Write((byte)(size == 256 ? 0 : size));   // width (0 = 256)
            writer.Write((byte)(size == 256 ? 0 : size));   // height
            writer.Write((byte)0);                          // palette
            writer.Write((byte)0);                          // reserved
            writer.Write((ushort)1);                        // planes
            writer.Write((ushort)32);                       // bpp
            writer.Write(layer.Length);
            writer.Write(offset);
            offset += layer.Length;
        }

        foreach (var layer in layers) writer.Write(layer);
    }

    private static byte[] Downscale(byte[] png, int size)
    {
        using var input = new MemoryStream(png);
        var frame = BitmapFrame.Create(input);

        var scaled = new TransformedBitmap(frame,
            new ScaleTransform((double)size / frame.PixelWidth, (double)size / frame.PixelHeight));

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(scaled));

        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    // ---------- COM ярлыков ----------

    private readonly record struct ShortcutInfo(string Target, string IconLocation, int IconIndex)
    {
        /// <summary>Откуда брать картинку: файл иконки, если задан, иначе цель ярлыка.</summary>
        public string IconSource()
        {
            var location = IconLocation;
            if (!string.IsNullOrWhiteSpace(location))
            {
                var comma = location.LastIndexOf(',');
                if (comma > 0) location = location[..comma];
                location = location.Trim().Trim('"');
                if (File.Exists(location)) return location;
            }

            return File.Exists(Target) ? Target : string.Empty;
        }
    }

    private static ShortcutInfo? ReadShortcut(string link)
    {
        dynamic? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;

            shell = Activator.CreateInstance(type);
            if (shell is null) return null;

            dynamic shortcut = shell.CreateShortcut(link);
            string target = shortcut.TargetPath as string ?? string.Empty;
            string icon = shortcut.IconLocation as string ?? string.Empty;

            int index = 0;
            var comma = icon.LastIndexOf(',');
            if (comma > 0 && int.TryParse(icon[(comma + 1)..], out var parsed)) index = parsed;

            return new ShortcutInfo(target, icon, index);
        }
        catch { return null; }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void WriteShortcutIcon(string link, string iconLocation)
    {
        dynamic? shell = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;

            shell = Activator.CreateInstance(type);
            dynamic shortcut = shell.CreateShortcut(link);
            shortcut.IconLocation = iconLocation;
            shortcut.Save();
        }
        catch { }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    private static void RefreshShell()
    {
        try { SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    // ---------- карта оригиналов ----------

    private static Dictionary<string, string> LoadMap()
    {
        try
        {
            if (!File.Exists(MapPath)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(MapPath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                   ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch { return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
    }

    private static void SaveMap(Dictionary<string, string> map)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(MapPath, JsonSerializer.Serialize(map));
        }
        catch { }
    }

    private static string SafeName(string link) =>
        Convert.ToHexString(System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(link)))[..12].ToLowerInvariant();
}