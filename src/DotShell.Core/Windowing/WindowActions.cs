namespace DotShell.Core.Windowing;

using System.Diagnostics;
using DotShell.Core.Launcher;
using DotShell.Core.Native;

/// <summary>
/// Действия с чужими окнами — механика в духе Familiar Desktop: док должен
/// уметь не только запускать, но и переключать/сворачивать/закрывать программу.
///
/// Обычный процесс не может «влезть» в чужое окно, поэтому используем только
/// документированные Win32-вызовы без инъекций:
///   • ShowWindowAsync — не блокирует наш UI, даже если приложение зависло;
///   • SetForegroundWindow — работает, когда клик пришёл из нашего окна
///     (процесс недавно получил ввод), иначе остаётся честный FlashWindowEx;
///   • PostMessage(WM_CLOSE) — «крестик», а не убийство процесса.
///
/// Окна приложений с повышенными правами (UIPI) не трогаем: вызовы молча
/// ничего не сделают, поэтому такие окна просто не попадают в список действий.
/// </summary>
public static class WindowActions
{
    /// <summary>Все видимые окна программы — для меню «Окна» в доке.</summary>
    public static List<WindowInfo> GetWindows(string target)
    {
        try
        {
            var processName = ProcessProbe.ResolveProcessName(target);
            if (string.IsNullOrWhiteSpace(processName)) return new List<WindowInfo>();

            IntPtr foreground = Win32.GetForegroundWindow();
            return WindowList.Get(Array.Empty<IntPtr>())
                .Where(window => string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(window => window.Hwnd == foreground)
                .ToList();
        }
        catch
        {
            return new List<WindowInfo>();
        }
    }

    /// <summary>
    /// Прокрутка колесом по иконке дока: переключение между окнами программы
    /// (как в Familiar Desktop). Возвращает false, если окон меньше двух.
    /// </summary>
    public static bool CycleWindows(string target, int direction)
    {
        var windows = GetWindows(target);
        if (windows.Count < 2) return false;

        IntPtr foreground = Win32.GetForegroundWindow();
        int index = windows.FindIndex(window => window.Hwnd == foreground);
        int next = index < 0 ? 0 : ((index + direction) % windows.Count + windows.Count) % windows.Count;
        Activate(windows[next].Hwnd);
        return true;
    }

    /// <summary>Найти видимое окно, принадлежащее программе из цели плитки.</summary>
    public static IntPtr FindWindow(string target)
    {
        try
        {
            var processName = ProcessProbe.ResolveProcessName(target);
            if (string.IsNullOrWhiteSpace(processName)) return IntPtr.Zero;

            IntPtr best = IntPtr.Zero;
            IntPtr foreground = Win32.GetForegroundWindow();

            foreach (var window in WindowList.Get(Array.Empty<IntPtr>()))
            {
                if (!string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) continue;
                if (window.Hwnd == foreground) return window.Hwnd;
                if (best == IntPtr.Zero) best = window.Hwnd;
            }

            return best;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// Поведение кнопки дока: свёрнутое — развернуть и показать, активное —
    /// свернуть, остальное — показать. Возвращает false, если окна нет
    /// (тогда вызывающий просто запускает программу).
    /// </summary>
    public static bool Toggle(string target)
    {
        var window = FindWindow(target);
        if (window == IntPtr.Zero) return false;
        Toggle(window);
        return true;
    }

    public static void Toggle(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window)) return;

        if (Win32.IsIconic(window))
        {
            Activate(window);
            return;
        }

        if (Win32.GetForegroundWindow() == window)
        {
            Minimize(window);
            return;
        }

        Activate(window);
    }

    /// <summary>Показать окно: развернуть, если свёрнуто, и вывести на передний план.</summary>
    public static void Activate(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window)) return;

        if (Win32.IsIconic(window))
            ShowWindowSafe(window, Win32.SW_RESTORE);

        // Показываем без ожидания: занятое приложение не должно вешать док.
        if (!Win32.SetForegroundWindow(window))
            Win32.ShowWindowAsync(window, Win32.SW_SHOWNOACTIVATE);
    }

    public static void Minimize(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window)) return;
        ShowWindowSafe(window, Win32.SW_MINIMIZE);
    }

    /// <summary>
    /// ShowWindowAsync в чужом процессе иногда возвращает true, но состояние
    /// не меняется (наблюдали на Notepad: async=true, iconic=false). Поэтому:
    /// для живого окна делаем синхронный ShowWindow (он мгновенный), а для
    /// зависшего — только асинхронный, чтобы не повесить интерфейс дока.
    /// </summary>
    private static void ShowWindowSafe(IntPtr window, int cmd)
    {
        try
        {
            if (Win32.IsHungAppWindow(window))
            {
                Win32.ShowWindowAsync(window, cmd);
                return;
            }

            Win32.ShowWindow(window, cmd);
        }
        catch
        {
            try { Win32.ShowWindowAsync(window, cmd); } catch { }
        }
    }

    public static void Restore(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window)) return;
        ShowWindowSafe(window, Win32.SW_RESTORE);
    }

    /// <summary>Вежливое закрытие: то же, что крестик окна (с вопросом о сохранении).</summary>
    public static void Close(IntPtr window)
    {
        if (window == IntPtr.Zero || !Win32.IsWindow(window)) return;
        Win32.PostMessage(window, Win32.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool IsMinimized(IntPtr window) =>
        window != IntPtr.Zero && Win32.IsWindow(window) && Win32.IsIconic(window);
}
