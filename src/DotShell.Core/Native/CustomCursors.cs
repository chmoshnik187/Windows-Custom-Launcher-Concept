namespace DotShell.Core.Native;

using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Свои курсоры вместо системных.
///
/// Курсор — самая заметная вещь после обоев, и единственная, что остаётся системной,
/// когда всё остальное уже перекрашено. Тут он делается из точек: точка вместо
/// стрелки, точка-кольцо для ссылок, три точки для текста.
///
/// Механика Windows: пути к .cur лежат в HKCU\Control Panel\Cursors, а применяет их
/// SystemParametersInfo(SPI_SETCURSORS). Поэтому сначала пишем файлы, потом ключи,
/// потом просим систему перечитать. Всё в профиле пользователя, откат — вернуть
/// прежние пути.
/// </summary>
public static class CustomCursors
{
    private const string Key = @"Control Panel\Cursors";
    private const int SpiSetCursors = 0x0057;
    private const uint SpifUpdateIniFile = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint flags);

    /// <summary>Какие роли курсора мы вообще подменяем. Остальные оставляем системе.</summary>
    /// <summary>
    /// Все роли, которые Windows умеет подменять. Раньше список был коротким
    /// (стрелка, рука, текст, прицел, ожидание) — размеры окна и «занято»
    /// оставались системными и выбивались из общего вида.
    /// </summary>
    public static readonly string[] Roles =
    {
        "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen", "No",
        "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow",
        "Hand", "Pin", "Person",
    };

    /// <summary>Снимок путей до подмены — для отката.</summary>
    public sealed class Snapshot
    {
        public Dictionary<string, string> Paths { get; set; } = new();
        public string DefaultValue { get; set; } = string.Empty;
    }

    /// <summary>
    /// Снимок текущих курсоров. Наши собственные пути в снимок не попадают: иначе
    /// «выключить» вернёт наши же курсоры, и человек останется без отката.
    /// Если же до нас уже стояли наши (например, после перезапуска), берём
    /// системный набор Windows.
    /// </summary>
    public static Snapshot? Capture(string? ourFolder = null)
    {
        try
        {
            var snapshot = new Snapshot();

            using var key = Registry.CurrentUser.OpenSubKey(Key);

            if (key is not null)
            {
                snapshot.DefaultValue = key.GetValue(string.Empty) as string ?? string.Empty;

                foreach (var role in Roles)
                {
                    if (key.GetValue(role) is not string path || path.Length == 0) continue;
                    if (ourFolder is not null && path.StartsWith(ourFolder, StringComparison.OrdinalIgnoreCase)) continue;

                    snapshot.Paths[role] = path;
                }
            }

            // Ничего не запомнили — значит система уже была перекрашена нами.
            // Тогда откат идёт на стандартную схему Windows.
            if (snapshot.Paths.Count == 0)
                snapshot.Paths = SystemDefaults();

            return snapshot;
        }
        catch { return null; }
    }

    /// <summary>Системный набор курсоров Windows (схема по умолчанию).</summary>
    private static Dictionary<string, string> SystemDefaults() => new()
    {
        ["Arrow"] = @"%SystemRoot%\cursors\aero_arrow.cur",
        ["Help"] = @"%SystemRoot%\cursors\aero_helpsel.cur",
        ["AppStarting"] = @"%SystemRoot%\cursors\aero_working.ani",
        ["Wait"] = @"%SystemRoot%\cursors\aero_busy.ani",
        ["Crosshair"] = @"%SystemRoot%\cursors\Cross_r.cur",
        ["IBeam"] = @"%SystemRoot%\cursors\beam_r.cur",
        ["NWPen"] = @"%SystemRoot%\cursors\pen_r.cur",
        ["No"] = @"%SystemRoot%\cursors\aero_unavail.cur",
        ["SizeNS"] = @"%SystemRoot%\cursors\aero_ns.cur",
        ["SizeWE"] = @"%SystemRoot%\cursors\aero_ew.cur",
        ["SizeNWSE"] = @"%SystemRoot%\cursors\aero_nwse.cur",
        ["SizeNESW"] = @"%SystemRoot%\cursors\aero_nesw.cur",
        ["SizeAll"] = @"%SystemRoot%\cursors\aero_move.cur",
        ["UpArrow"] = @"%SystemRoot%\cursors\aero_up.cur",
        ["Hand"] = @"%SystemRoot%\cursors\aero_link.cur",
        ["Pin"] = @"%SystemRoot%\cursors\aero_pin.cur",
        ["Person"] = @"%SystemRoot%\cursors\aero_person.cur",
    };

    /// <summary>Поставить свои курсоры: имя роли → путь к .cur.</summary>
    public static bool Apply(IReadOnlyDictionary<string, string> files)
    {
        try
        {
            bool wrote = false;

            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                foreach (var (role, path) in files)
                {
                    if (!File.Exists(path)) continue;
                    key.SetValue(role, path, RegistryValueKind.ExpandString);
                    wrote = true;
                }

                // Своя схема — чтобы Windows не «починила» отдельные курсоры обратно.
                if (wrote) key.SetValue(string.Empty, "DotShell", RegistryValueKind.String);
            }

            if (!wrote) return false;

            SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile);
            return true;
        }
        catch { return false; }
    }

    public static void Restore(Snapshot snapshot)
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
            {
                foreach (var (role, path) in snapshot.Paths)
                    key.SetValue(role, path, RegistryValueKind.ExpandString);

                if (snapshot.DefaultValue.Length > 0)
                    key.SetValue(string.Empty, snapshot.DefaultValue, RegistryValueKind.String);
            }

            SystemParametersInfo(SpiSetCursors, 0, IntPtr.Zero, SpifUpdateIniFile);
        }
        catch { }
    }

    /// <summary>
    /// Собрать .cur из PNG. Формат простой: заголовок, одна запись, картинка.
    /// Windows понимает PNG внутри курсора начиная с Vista, поэтому возиться с
    /// битмапом и маской прозрачности не нужно.
    /// </summary>
    public static void WriteCursor(string path, byte[] png, int hotX, int hotY, int size)
    {
        using var stream = File.Create(path);

        using var writer = new BinaryWriter(stream);

        // ICONDIR
        writer.Write((ushort)0);   // reserved
        writer.Write((ushort)2);   // 2 = курсор
        writer.Write((ushort)1);   // одна картинка

        // ICONDIRENTRY
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);     // цветов нет (32bpp)
        writer.Write((byte)0);     // reserved
        writer.Write((ushort)hotX);
        writer.Write((ushort)hotY);
        writer.Write(png.Length);
        writer.Write(22);          // смещение картинки: 6 + 16

        writer.Write(png);
    }
}
