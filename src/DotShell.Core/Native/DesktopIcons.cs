namespace DotShell.Core.Native;

using static Win32;

/// <summary>
/// Спрятать/показать иконки рабочего стола.
///
/// Часть мечты владельца: «вместо скучного рабочего стола с прогами и файлами —
/// остров и виджеты». Файлы уже умеют жить на полке, программы — на главной
/// странице острова; остаётся убрать визуальный мусор с самого стола.
///
/// Иконки стола — это обычный ListView (SysListView32), вложенный в
/// SHELLDLL_DefView внутри Progman (на части сборок Windows — внутри WorkerW).
/// Прячем его честным ShowWindow: без правок реестра и перезапусков Explorer.
/// </summary>
public static class DesktopIcons
{
    /// <summary>true, если список иконок найден и команда выполнена.</summary>
    public static bool SetHidden(bool hidden)
    {
        var list = FindIconList();
        if (list == IntPtr.Zero) return false;
        return ShowWindow(list, hidden ? SW_HIDE : SW_SHOW);
    }

    public static bool IsHidden()
    {
        var list = FindIconList();
        return list != IntPtr.Zero && !IsWindowVisible(list);
    }

    private static IntPtr FindIconList()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        var defView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
        {
            // На части сборок Explorer переносит DefView в дочернее WorkerW-окно.
            IntPtr worker = IntPtr.Zero;
            while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                var candidate = FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (candidate != IntPtr.Zero) { defView = candidate; break; }
            }
        }
        if (defView == IntPtr.Zero) return IntPtr.Zero;

        return FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }
}
