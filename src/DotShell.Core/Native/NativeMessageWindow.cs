namespace DotShell.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// "Невидимое" окно, которое умеет только одно: получать системные сообщения.
/// Оно нужно, потому что Windows шлёт уведомления о буфере обмена и о горячих
/// клавишах именно окнам. Окно создаётся как message-only (родитель = HWND_MESSAGE),
/// поэтому нигде не видно, не занимает места и не появляется в Alt+Tab.
///
/// Класс окна регистрируется ОДИН раз на процесс, а его wndProc — статический:
/// он находит нужный экземпляр по HWND в словаре. Так можно создать сколько
/// угодно таких окон (например, отдельное для будущего лаунчера), и каждое будет
/// получать свои сообщения.
/// </summary>
public sealed class NativeMessageWindow : IDisposable
{
    private const string ClassName = "DotShell.MessageWindow";

    // Делегат обязан жить вечно: если GC соберёт его, lpfnWndProc станет
    // битым указателем и первое же сообщение уронит процесс.
    private static readonly Win32.WndProcDelegate SharedWndProc = StaticWndProc;
    private static bool _classRegistered;

    private static readonly Dictionary<IntPtr, NativeMessageWindow> Instances = new();

    private IntPtr _hwnd;

    /// <summary>Сюда приходят все сообщения окна. msg/wParam/lParam — как в документации Win32.</summary>
    public event Action<uint, IntPtr, IntPtr>? MessageReceived;

    public IntPtr Handle => _hwnd;

    public NativeMessageWindow()
    {
        if (!_classRegistered)
        {
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedWndProc),
                hInstance = Win32.GetModuleHandle(null),
                lpszClassName = ClassName
            };
            if (Win32.RegisterClassEx(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            _classRegistered = true;
        }

        // HWND_MESSAGE = new IntPtr(-3) — признак "только сообщения, без визуальной части"
        _hwnd = Win32.CreateWindowEx(0, ClassName, "DotShell", 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        lock (Instances) Instances[_hwnd] = this;
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        NativeMessageWindow? self;
        lock (Instances) Instances.TryGetValue(hWnd, out self);

        try
        {
            self?.MessageReceived?.Invoke(msg, wParam, lParam);
        }
        catch
        {
            // Обработчик подписчика не должен ронять системное окно.
            // Ошибки наверх забирает глобальный логгер приложения.
        }
        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        lock (Instances) Instances.Remove(_hwnd);
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }
}
