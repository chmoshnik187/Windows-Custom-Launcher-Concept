namespace DotShell.Core.Native;

using System.Runtime.InteropServices;
using System.Text.Json;

/// <summary>
/// Ночной свет: тёплая гамма монитора без системного API (его у Windows нет публично).
///
/// Почему гамма, а не ночной свет Windows: системный переключатель живёт в CloudStore
/// и управляется только панелью параметров; документированного API нет. Зато
/// `SetDeviceGammaRamp` — обычный Win32, работает без прав и без упаковки, а эффект
/// тот же по смыслу: убираем синий канал, экран становится теплее.
///
/// Безопасность важнее эффекта:
///  • перед первым включением снимаем ИСХОДНУЮ гамму каждого монитора и сохраняем её
///    в файл — если приложение упадёт, следующий старт вернёт как было;
///  • на выходе приложения гамма возвращается;
///  • если драйвер не отдаёт гамму (HDR, некоторые ноутбуки) — честно возвращаем false
///    и ничего не меняем, а не делаем вид, что «включилось».
/// </summary>
public static class NightLight
{
    private const int RampSize = 256;
    private const int RampLength = RampSize * 3;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, ushort[]> Original = new(StringComparer.OrdinalIgnoreCase);
    private static bool _applied;
    private static int _warmth = 40;

    /// <summary>Драйвер вообще даёт трогать гамму (проверяется при первом включении).</summary>
    public static bool Supported { get; private set; } = true;

    /// <summary>Последняя причина отказа — для статусной строки.</summary>
    public static string? LastError { get; private set; }

    public static bool Enabled => _applied;

    /// <summary>
    /// Включить/выключить тёплый режим. false — драйвер отказал, экран не менялся.
    /// </summary>
    public static bool Set(bool enabled, int warmth)
    {
        lock (Gate)
        {
            _warmth = Math.Clamp(warmth, 0, 100);

            if (!enabled)
            {
                Restore();
                return true;
            }

            if (_applied)
            {
                // Уже включён: меняем только температуру, без повторного снятия исходной.
                return ApplyRamp();
            }

            if (!CaptureOriginals())
            {
                LastError = "драйвер не отдаёт исходную гамму";
                Supported = false;
                return false;
            }

            // Сначала сохраняем исходную гамму на диск: при падении процесса следующий
            // старт сможет вернуть цвета, даже если мы ничего не успели.
            SaveOriginals();

            if (!ApplyRamp())
            {
                Restore();
                return false;
            }

            return true;
        }
    }

    /// <summary>После смены разрешения/монитора гамма обычно сбрасывается — вернуть.</summary>
    public static void ReapplyIfEnabled()
    {
        lock (Gate)
        {
            if (_applied) ApplyRamp();
        }
    }

    /// <summary>Вернуть исходную гамму. Вызывается на выходе приложения и при выключении.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (Original.Count == 0)
            {
                _applied = false;
                DeleteOriginalsFile();
                return;
            }

            try
            {
                foreach (var name in MonitorNames())
                {
                    if (!Original.TryGetValue(name, out var ramp)) continue;

                    var dc = CreateDC("DISPLAY", name, null, IntPtr.Zero);
                    try { if (dc != IntPtr.Zero) SetDeviceGammaRamp(dc, ramp); }
                    finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }

            Original.Clear();
            _applied = false;
            DeleteOriginalsFile();
        }
    }

    /// <summary>
    /// После аварийного завершения на диске остаётся снимок исходной гаммы: вернуть её,
    /// чтобы тёплый экран не остался «навсегда». Вызывается при старте приложения.
    /// </summary>
    public static bool RecoverIfNeeded()
    {
        lock (Gate)
        {
            if (!File.Exists(OriginalsPath)) return false;

            try
            {
                var text = File.ReadAllText(OriginalsPath);
                var saved = JsonSerializer.Deserialize<Dictionary<string, ushort[]>>(text);
                if (saved is null) return false;

                foreach (var pair in saved) Original[pair.Key] = pair.Value;

                foreach (var name in MonitorNames())
                {
                    if (!Original.TryGetValue(name, out var ramp)) continue;

                    var dc = CreateDC("DISPLAY", name, null, IntPtr.Zero);
                    try { if (dc != IntPtr.Zero) SetDeviceGammaRamp(dc, ramp); }
                    finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
                }

                Original.Clear();
                DeleteOriginalsFile();
                return true;
            }
            catch
            {
                // Битый файл — не повод падать: просто забываем про него.
                DeleteOriginalsFile();
                return false;
            }
        }
    }

    private static bool CaptureOriginals()
    {
        Original.Clear();
        bool ok = false;

        try
        {
            foreach (var name in MonitorNames())
            {
                var ramp = new ushort[RampLength];
                var dc = CreateDC("DISPLAY", name, null, IntPtr.Zero);
                try
                {
                    if (dc != IntPtr.Zero && GetDeviceGammaRamp(dc, ramp))
                    {
                        Original[name] = ramp;
                        ok = true;
                    }
                }
                finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        return ok;
    }

    private static bool ApplyRamp()
    {
        var ramp = BuildRamp(_warmth);
        bool any = false;
        bool failed = false;

        try
        {
            foreach (var name in MonitorNames())
            {
                var dc = CreateDC("DISPLAY", name, null, IntPtr.Zero);
                try
                {
                    if (dc != IntPtr.Zero && SetDeviceGammaRamp(dc, ramp)) any = true;
                    else failed = true;
                }
                finally { if (dc != IntPtr.Zero) DeleteDC(dc); }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }

        if (any)
        {
            _applied = true;
            Supported = true;
            LastError = failed ? "часть мониторов не приняла гамму" : null;
            return true;
        }

        LastError = "SetDeviceGammaRamp отказал (HDR или драйвер)";
        Supported = false;
        return false;
    }

    /// <summary>
    /// Тёплая таблица: красный не трогаем, зелёный гасим умеренно, синий — сильнее.
    /// Так экран теплеет, но белый не превращается в оранжевый кирпич.
    /// </summary>
    private static ushort[] BuildRamp(int warmth)
    {
        double w = Math.Clamp(warmth, 0, 100) / 100.0;
        double red = 1.0;
        double green = 1.0 - 0.30 * w;
        double blue = 1.0 - 0.65 * w;

        var ramp = new ushort[RampLength];
        for (int i = 0; i < RampSize; i++)
        {
            double value = i * 257.0;
            ramp[i] = Clamp(value * red);
            ramp[RampSize + i] = Clamp(value * green);
            ramp[RampSize * 2 + i] = Clamp(value * blue);
        }

        return ramp;
    }

    private static ushort Clamp(double value) => (ushort)Math.Clamp((int)Math.Round(value), 0, 65535);

    private static string MonitorName(IntPtr monitor)
    {
        try
        {
            var info = new MonitorInfoEx { cbSize = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(monitor, ref info) && !string.IsNullOrEmpty(info.szDevice))
                return info.szDevice;
        }
        catch { }

        return "display";
    }

    /// <summary>Имена устройств всех мониторов (\\.\DISPLAY1, \\.\DISPLAY2...).</summary>
    private static List<string> MonitorNames()
    {
        var names = new List<string>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) =>
            {
                var name = MonitorName(monitor);
                if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return names;
    }

    private static string OriginalsPath =>
        Path.Combine(Settings.AppSettings.SettingsDirectory, "nightlight-original.json");

    private static void SaveOriginals()
    {
        try
        {
            Directory.CreateDirectory(Settings.AppSettings.SettingsDirectory);
            File.WriteAllText(OriginalsPath, JsonSerializer.Serialize(Original));
        }
        catch { /* без файла тоже работает, просто нет страховки от падения */ }
    }

    private static void DeleteOriginalsFile()
    {
        try { if (File.Exists(OriginalsPath)) File.Delete(OriginalsPath); } catch { }
    }

    // ---------------------------------------------------------------- Win32

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDC(string driver, string? device, string? port, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, [Out] ushort[] ramp);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
