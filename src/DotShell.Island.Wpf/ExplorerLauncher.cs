namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DotShell.Core;

/// <summary>
/// Открыть папку в проводнике — вкладкой в уже открытом окне, если это возможно.
///
/// Почему с трюком: `explorer.exe &lt;путь&gt;` всегда создаёт новое окно (проверено),
/// а официального API «открыть вкладку в существующем окне» нет. Зато в Windows 11
/// 22H2+ у проводника есть вкладки, и Ctrl+T открывает новую. Дальше адресная
/// строка (Ctrl+L), вставка пути и Enter.
///
/// Границы, которые честно проверяются:
///   • Windows 10 — вкладок нет вообще, там всегда новое окно;
///   • Windows 11 ниже 22621 — то же самое;
///   • окна проводника не видно (закрыт, свёрнут в трей) — открываем новое.
/// </summary>
public static class ExplorerLauncher
{
    private const int VkControl = 0x11;
    private const int VkT = 0x54;
    private const int VkL = 0x4C;
    private const int VkV = 0x56;
    private const int VkReturn = 0x0D;
    private const uint KeyUp = 0x0002;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    /// <summary>Есть ли в системе поддержка вкладок проводника.</summary>
    public static bool TabsSupported =>
        Environment.OSVersion.Version.Build >= 22621;

    /// <summary>Открыть папку: вкладкой, если получится, иначе новым окном.</summary>
    public static void Open(string path, bool preferTab = true)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!preferTab || !TabsSupported || FindExplorerWindow() == IntPtr.Zero)
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
                return;
            }

            OpenInNewTab(path);
        }
        catch (Exception ex)
        {
            Log.Info($"explorer open failed: {ex.Message}");
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
            catch { }
        }
    }

    private static void OpenInNewTab(string path)
    {
        var window = FindExplorerWindow();
        if (window == IntPtr.Zero) return;

        // Свёрнутое окно сначала показываем: Ctrl+T в него иначе не дойдёт.
        ShowWindow(window, 9);           // SW_RESTORE
        SetForegroundWindow(window);
        Thread.Sleep(180);

        // Запоминаем буфер, чтобы вернуть его после вставки пути.
        string? saved = null;
        try { if (Clipboard.ContainsText()) saved = Clipboard.GetText(); } catch { }

        try
        {
            Chord(VkControl, VkT);       // новая вкладка
            Thread.Sleep(260);
            Chord(VkControl, VkL);       // адресная строка
            Thread.Sleep(140);

            try { Clipboard.SetText(path); } catch { return; }
            Thread.Sleep(80);

            Chord(VkControl, VkV);       // вставить путь
            Thread.Sleep(120);
            Tap(VkReturn);               // перейти
            Thread.Sleep(120);
        }
        finally
        {
            try
            {
                if (saved is not null) Clipboard.SetText(saved);
                else Clipboard.Clear();
            }
            catch { }
        }
    }

    private static IntPtr FindExplorerWindow()
    {
        IntPtr found = IntPtr.Zero;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                var cls = new StringBuilder(64);
                GetClassName(hwnd, cls, cls.Capacity);
                if (cls.ToString() != "CabinetWClass") return true;   // окно проводника
                if (!IsWindowVisible(hwnd)) return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);
        }
        catch { }

        return found;
    }

    private static void Chord(int modifier, int key)
    {
        keybd_event((byte)modifier, 0, 0, UIntPtr.Zero);
        keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        keybd_event((byte)key, 0, KeyUp, UIntPtr.Zero);
        keybd_event((byte)modifier, 0, KeyUp, UIntPtr.Zero);
    }

    private static void Tap(int key)
    {
        keybd_event((byte)key, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        keybd_event((byte)key, 0, KeyUp, UIntPtr.Zero);
    }
}
