namespace DotShell.Island;

using System.Runtime.InteropServices;
using DotShell.Core.Native;
using Microsoft.UI.Xaml;

/// <summary>
/// Всё, что связано с "настоящим" окном Win32: стили, позиция, прозрачность для кликов.
///
/// Почему это отдельный класс: WinUI 3 прячет от нас HWND и не даёт менять стили окна,
/// а нам нужно окно без рамки, всегда сверху, не ворующее фокус и кликабельное только
/// там, где нарисована "таблетка". Всё это делается через Win32 — значит, выносим сюда.
/// </summary>
internal static class IslandChrome
{
    // Размеры в ЛОГИЧЕСКИХ пикселях (WinUI умножит их на масштаб экрана).
    public const int WindowLogicalWidth = 700;
    public const int WindowLogicalHeight = 380;

    public const int PillCollapsedWidth = 372;
    public const int PillCollapsedHeight = 46;
    public const int PillExpandedWidth = 620;
    public const int PillExpandedHeight = 296;

    public const int PillTopMargin = 10;   // отступ таблетки от верхнего края окна
    public const int PillCornerRadiusCollapsed = 23;
    public const int PillCornerRadiusExpanded = 26;

    private static IntPtr _hwnd;
    private static double _scale = 1.0;    // масштаб экрана (1.0 / 1.25 / 1.5 …)
    private static int _topOffsetPx = 10;  // отступ от верхнего края монитора, физ. px

    public static IntPtr Hwnd => _hwnd;
    public static double Scale => _scale;

    /// <summary>Перевести логические пиксели WinUI в физические пиксели экрана.</summary>
    public static int ToPhysical(double logical) => (int)Math.Round(logical * _scale);

    public static void Apply(Window window, int topOffsetLogical)
    {
        _hwnd = WindowHelper.GetHwnd(window);
        if (_hwnd == IntPtr.Zero) return;

        _scale = window.Content is FrameworkElement root
            ? root.XamlRoot?.RasterizationScale ?? 1.0
            : 1.0;
        _topOffsetPx = ToPhysical(topOffsetLogical);

        // ---- стили окна ----
        // WS_POPUP          — окно без рамки и заголовка;
        // WS_EX_LAYERED     — попиксельная прозрачность (видно только таблетку);
        // WS_EX_TOPMOST     — всегда поверх остальных окон;
        // WS_EX_NOACTIVATE  — клик по острову НЕ отбирает фокус у текущего приложения;
        // WS_EX_TOOLWINDOW  — не показываем окно в Alt+Tab;
        // WS_EX_TRANSPARENT — клики проходят сквозь окно (см. UpdateHitTest).
        long style = Win32.WS_POPUP | Win32.WS_VISIBLE;
        long exStyle = Win32.WS_EX_LAYERED
                     | Win32.WS_EX_TOPMOST
                     | Win32.WS_EX_NOACTIVATE
                     | Win32.WS_EX_TOOLWINDOW
                     | Win32.WS_EX_TRANSPARENT;

        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_STYLE, new IntPtr(style));
        Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(exStyle));

        Reposition();

        // Сообщаем системе, что стили изменились: без SWP_FRAMECHANGED
        // часть из них применится только после перерисовки.
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_FRAMECHANGED | Win32.SWP_NOACTIVATE);
    }

    /// <summary>Поставить окно по центру верхнего края того монитора, на котором оно сейчас.</summary>
    public static void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;

        var mi = Win32.MONITORINFOEX.Create();
        IntPtr monitor = Win32.MonitorFromWindow(_hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero || !Win32.GetMonitorInfo(monitor, ref mi)) return;

        int w = ToPhysical(WindowLogicalWidth);
        int h = ToPhysical(WindowLogicalHeight);
        int x = mi.rcMonitor.Left + (mi.rcMonitor.Width - w) / 2;
        int y = mi.rcMonitor.Top + _topOffsetPx;

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, w, h,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
    }

    /// <summary>
    /// Включить/выключить "проходимость" окна для мыши.
    ///
    /// Суть приёма: окно у нас большое (700×380), а видна только таблетка.
    /// Если оставить его кликабельным целиком, оно будет перехватывать клики
    /// на огромном куске экрана. Поэтому по умолчанию стоит WS_EX_TRANSPARENT
    /// (клики проходят насквозь), а когда курсор оказывается НАД таблеткой —
    /// флаг снимается, и клик попадает уже нам. Опрос курсора стоит примерно ноль ресурсов.
    ///
    /// Альтернатива — SetWindowRgn с областью в форме таблетки. Она точнее,
    /// но плохо дружит со скруглениями и анимацией размера, поэтому здесь опрос.
    /// </summary>
    public static void UpdateHitTest(double pillLogicalWidth, double pillLogicalHeight)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!Win32.GetCursorPos(out var cursor)) return;
        if (!Win32.GetWindowRect(_hwnd, out var win)) return;

        int pillW = ToPhysical(pillLogicalWidth);
        int pillH = ToPhysical(pillLogicalHeight);
        int left = win.Left + (win.Width - pillW) / 2;
        int top = win.Top + ToPhysical(PillTopMargin);

        bool inside = cursor.X >= left && cursor.X <= left + pillW &&
                      cursor.Y >= top && cursor.Y <= top + pillH;

        long ex = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();
        long wanted = inside
            ? ex & ~Win32.WS_EX_TRANSPARENT
            : ex | Win32.WS_EX_TRANSPARENT;

        if (wanted != ex)
            Win32.SetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE, new IntPtr(wanted));
    }

    /// <summary>Спрятать/показать окно (используется, когда что-то открыто на весь экран).</summary>
    public static void SetVisible(bool visible)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.ShowWindow(_hwnd, visible ? 5 /* SW_SHOW */ : 0 /* SW_HIDE */);
    }

    /// <summary>Снять/вернуть "поверх всех окон" — нужно, когда остров спрятан в полноэкранном режиме.</summary>
    public static void SetTopmost(bool topmost)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd,
            topmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }
}

/// <summary>Мелкие вспомогательные вещи для работы с Window.</summary>
internal static class WindowHelper
{
    /// <summary>
    /// Достать HWND из объекта Window (WinUI 3).
    ///
    /// Свойства HWND у Window нет. Но сам объект Window реализует COM-интерфейс
    /// IInitializeWithWindow, а его нативная реализация возвращает дескриптор окна,
    /// если передать нулевой указатель. Приём выглядит как магия, но он стабилен
    /// и используется практически во всех библиотеках для WinUI 3
    /// (Community Toolkit, WinUIEx, H.NotifyIcon).
    /// </summary>
    public static IntPtr GetHwnd(Window window)
    {
        try
        {
            var interop = window.As<IInitializeWithWindow>();
            return interop.Initialize(IntPtr.Zero);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [ComImport]
    [Guid("3E68D4BD-7135-4D10-8018-9FB6D9F33FA1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IInitializeWithWindow
    {
        // Да, возвращаемое значение здесь — именно HWND. В официальной документации
        // метод объявлен как void, но WinUI-реализация отдаёт дескриптор окна
        // при hWnd == IntPtr.Zero.
        IntPtr Initialize(IntPtr hWnd);
    }
}
