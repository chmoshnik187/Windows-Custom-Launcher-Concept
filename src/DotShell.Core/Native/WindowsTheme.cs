namespace DotShell.Core.Native;

using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Тёмная/светлая тема Windows из острова: один клик во вкладке СИСТЕМА.
///
/// Ключи лежат в HKCU (права администратора не нужны), а рассылка
/// WM_SETTINGCHANGE с "ImmersiveColorSet" говорит проводнику и приложениям
/// перечитать палитру — иначе тема поменяется только после перезахода.
/// </summary>
public static class WindowsTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public static bool IsDarkApps()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch { return true; }
    }

    public static void SetDark(bool dark)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
            // AppsUseLightTheme — приложения, SystemUseLightTheme — системные панели.
            key.SetValue("AppsUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.SetValue("SystemUseLightTheme", dark ? 0 : 1, RegistryValueKind.DWord);
            key.Flush();
            BroadcastThemeChange();
        }
        catch { } // нет доступа к реестру — молча пропускаем, это не смертельно
    }

    private const uint HWND_BROADCAST = 0xFFFF;
    private const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, UIntPtr wParam, string lParam,
        uint flags, uint timeout, out UIntPtr result);

    private static void BroadcastThemeChange()
    {
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE,
            UIntPtr.Zero, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 2000, out _);
    }
}
