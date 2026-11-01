namespace DotShell.Core.Native;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Тёмные заголовки для всех окон — «оформление всей Windows», а не только острова.
///
/// Зачем отдельный механизм: реестровый `AppsUseLightTheme` переключает сами
/// приложения, но заголовки Win32-окон (проводник, панель управления, старые
/// программы) остаются светлыми, пока DWM не скажут отдельно. Атрибут
/// `DWMWA_USE_IMMERSIVE_DARK_MODE` ставится на каждое окно.
///
/// Работает как сторож: раз в секунду проверяет новые окна и красит их.
/// Отключается одним вызовом — все изменения обратимы, ничего не патчится в файлах.
/// </summary>
public static class DarkTitleBars
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;   // сборки Windows до 2004

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    private static System.Timers.Timer? _timer;
    private static readonly object Gate = new();

    public static bool Enabled { get; private set; }

    /// <summary>Сколько окон покрасили в последний проход (для диагностики).</summary>
    public static int LastApplied { get; private set; }

    public static void Start()
    {
        lock (Gate)
        {
            Enabled = true;
            ApplyToAll();

            _timer ??= new System.Timers.Timer(1500) { AutoReset = true };
            _timer.Elapsed -= OnTick;
            _timer.Elapsed += OnTick;
            _timer.Start();
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            Enabled = false;
            _timer?.Stop();
        }
    }

    /// <summary>Окрасить всё, что есть сейчас.</summary>
    public static void ApplyToAll() => Apply(onlyVisible: true);

    private static void OnTick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        if (!Enabled) return;
        Apply(onlyVisible: true);
    }

    private static void Apply(bool onlyVisible)
    {
        uint self = (uint)Environment.ProcessId;
        int applied = 0;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (onlyVisible && !IsWindowVisible(hwnd)) return true;

                // Окна без заголовка красить незачем, а свои — не трогаем.
                if (GetWindowTextLength(hwnd) == 0) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;

                if (SetDark(hwnd)) applied++;
                return true;
            }, IntPtr.Zero);
        }
        catch { /* оформление не должно ронять шелл */ }

        LastApplied = applied;
    }

    private static bool SetDark(IntPtr hwnd)
    {
        int on = 1;
        try
        {
            // Новые сборки понимают атрибут 20, старые — 19. Пробуем оба.
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int)) == 0)
                return true;

            return DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref on, sizeof(int)) == 0;
        }
        catch { return false; }
    }

    /// <summary>Вернуть окну системный (светлый) вид — для отключения оформления.</summary>
    public static void RestoreAll()
    {
        uint self = (uint)Environment.ProcessId;

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || GetWindowTextLength(hwnd) == 0) return true;
                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == self) return true;

                int off = 0;
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref off, sizeof(int));
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeOld, ref off, sizeof(int));
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }
}
