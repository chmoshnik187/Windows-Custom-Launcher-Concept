namespace DotShell.Core.Native;

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

/// <summary>
/// Акцент Windows = акцент шелла.
///
/// Windows хранит «цвет акцента» в двух местах и в двух разных порядках байт:
///   • DWM\ColorizationColor   — 0xAARRGGBB (альфа, красный, зелёный, синий);
///   • DWM\AccentColor и Explorer\Accent\AccentColorMenu — 0xAABBGGRR (обратный).
/// Плюс бинарная палитра AccentPalette из восьми оттенков, из которой система
/// берёт цвета для «Пуска», подсветки и кнопок.
///
/// Всё пишется только в HKEY_CURRENT_USER: чужие профили не трогаем, права
/// администратора не нужны, откат — запись сохранённых значений назад.
/// </summary>
public static class WindowsAccent
{
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";
    private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

    private const int WmDwmColorizationColorChanged = 0x031E;
    private const int WmSettingChange = 0x001A;
    private const int HwndBroadcast = 0xFFFF;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>Снимок системных значений до того, как мы что-то поменяли.</summary>
    public sealed class Snapshot
    {
        public int ColorizationColor { get; set; }
        public int AccentColor { get; set; }
        public int AccentColorMenu { get; set; }
        public int StartColorMenu { get; set; }
        public string PaletteBase64 { get; set; } = string.Empty;
    }

    /// <summary>Текущий акцент Windows в виде #RRGGBB (или null, если прочитать не удалось).</summary>
    public static string? CurrentHex()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DwmKey);
            if (key?.GetValue("ColorizationColor") is not int value) return null;

            // 0xAARRGGBB → RGB
            return $"#{((value >> 16) & 0xFF):X2}{((value >> 8) & 0xFF):X2}{(value & 0xFF):X2}";
        }
        catch { return null; }
    }

    public static Snapshot? Capture()
    {
        try
        {
            var snapshot = new Snapshot();

            using (var dwm = Registry.CurrentUser.OpenSubKey(DwmKey))
            {
                if (dwm?.GetValue("ColorizationColor") is int c) snapshot.ColorizationColor = c;
                if (dwm?.GetValue("AccentColor") is int a) snapshot.AccentColor = a;
            }

            using (var accent = Registry.CurrentUser.OpenSubKey(AccentKey))
            {
                if (accent?.GetValue("AccentColorMenu") is int m) snapshot.AccentColorMenu = m;
                if (accent?.GetValue("StartColorMenu") is int s) snapshot.StartColorMenu = s;
                if (accent?.GetValue("AccentPalette") is byte[] palette)
                    snapshot.PaletteBase64 = Convert.ToBase64String(palette);
            }

            return snapshot;
        }
        catch { return null; }
    }

    public static bool Restore(Snapshot snapshot)
    {
        try
        {
            // Защита от «пустого» снимка: нулевой ARGB означает чёрный акцент.
            // Однажды это уже случилось — лучше не писать ничего, чем залить чёрным.
            int colorization = snapshot.ColorizationColor;
            if (colorization == 0 && snapshot.AccentColor != 0)
            {
                // Восстанавливаем из AccentColor (там формат AABBGGRR — каналы обратны).
                int abgr = snapshot.AccentColor;
                int r = abgr & 0xFF, g = (abgr >> 8) & 0xFF, b = (abgr >> 16) & 0xFF;
                colorization = unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
            }

            // Пустой снимок — не пишем ничего: лучше оставить как есть, чем залить чёрным.
            if (colorization == 0) return false;

            using (var dwm = Registry.CurrentUser.CreateSubKey(DwmKey))
            {
                dwm.SetValue("ColorizationColor", colorization, RegistryValueKind.DWord);
                dwm.SetValue("ColorizationAfterglow", colorization, RegistryValueKind.DWord);
                if (snapshot.AccentColor != 0)
                    dwm.SetValue("AccentColor", snapshot.AccentColor, RegistryValueKind.DWord);
            }

            using (var accent = Registry.CurrentUser.CreateSubKey(AccentKey))
            {
                accent.SetValue("AccentColorMenu", snapshot.AccentColorMenu, RegistryValueKind.DWord);
                accent.SetValue("StartColorMenu", snapshot.StartColorMenu, RegistryValueKind.DWord);

                if (!string.IsNullOrEmpty(snapshot.PaletteBase64))
                    accent.SetValue("AccentPalette", Convert.FromBase64String(snapshot.PaletteBase64), RegistryValueKind.Binary);
            }

            Broadcast();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Сделать акцент Windows равным цвету (r,g,b).</summary>
    public static void Apply(byte r, byte g, byte b)
    {
        try
        {
            // DWM: AARRGGBB, альфа 0xC4 — как ставит сама Windows для «прозрачных» тем.
            int argb = unchecked((int)(0xC4000000u | ((uint)r << 16) | ((uint)g << 8) | b));
            // Accent: AABBGGRR — обратный порядок, здесь легко ошибиться.
            int abgr = unchecked((int)(0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r));

            using (var dwm = Registry.CurrentUser.CreateSubKey(DwmKey))
            {
                dwm.SetValue("ColorizationColor", argb, RegistryValueKind.DWord);
                dwm.SetValue("ColorizationAfterglow", argb, RegistryValueKind.DWord);
                dwm.SetValue("ColorizationOpaqueBlend", 0, RegistryValueKind.DWord);
                dwm.SetValue("AccentColor", abgr, RegistryValueKind.DWord);
            }

            using (var accent = Registry.CurrentUser.CreateSubKey(AccentKey))
            {
                accent.SetValue("AccentColorMenu", abgr, RegistryValueKind.DWord);
                accent.SetValue("StartColorMenu", abgr, RegistryValueKind.DWord);
                accent.SetValue("AccentPalette", BuildPalette(r, g, b), RegistryValueKind.Binary);
            }

            Broadcast();
        }
        catch { }
    }

    /// <summary>
    /// Восемь оттенков акцента — от тёмного к светлому. Из этой палитры Windows
    /// берёт цвет подсветки «Пуска» и кнопок, поэтому без неё акцент меняется
    /// наполовину: заголовки новые, а подсветка прежняя.
    /// </summary>
    private static byte[] BuildPalette(byte r, byte g, byte b)
    {
        // (насколько смешать с белым): отрицательное — затемнить, положительное — высветлить.
        double[] mixes = { -0.45, -0.30, -0.15, 0.0, 0.18, 0.34, 0.50, 0.66 };
        var palette = new byte[mixes.Length * 4];

        for (int i = 0; i < mixes.Length; i++)
        {
            byte ch = Shade(r, mixes[i]);
            byte cg = Shade(g, mixes[i]);
            byte cb = Shade(b, mixes[i]);

            int at = i * 4;
            palette[at + 0] = cb;   // B
            palette[at + 1] = cg;   // G
            palette[at + 2] = ch;   // R
            palette[at + 3] = 0xFF; // A
        }

        return palette;
    }

    private static byte Shade(byte value, double mix)
    {
        double target = mix >= 0 ? 255 : 0;
        double k = Math.Abs(mix);
        double result = value + (target - value) * k;
        return (byte)Math.Clamp(Math.Round(result), 0, 255);
    }

    /// <summary>
    /// Разбудить систему: без этих сообщений новые значения лежат в реестре,
    /// но панель задач и подсветка остаются прежними до перезапуска проводника.
    /// </summary>
    private static void Broadcast()
    {
        try
        {
            SendMessageTimeout((IntPtr)HwndBroadcast, WmDwmColorizationColorChanged,
                IntPtr.Zero, IntPtr.Zero, 0x0002, 200, out _);

            // WM_SETTINGCHANGE с "ImmersiveColorSet" — сигнал «цвета изменились».
            IntPtr text = Marshal.StringToHGlobalUni("ImmersiveColorSet");
            try
            {
                SendMessageTimeout((IntPtr)HwndBroadcast, WmSettingChange, IntPtr.Zero, text, 0x0002, 200, out _);
            }
            finally { Marshal.FreeHGlobal(text); }
        }
        catch { }
    }
}
