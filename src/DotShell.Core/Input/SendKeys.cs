namespace DotShell.Core.Input;

using System.Runtime.InteropServices;

/// <summary>
/// Эмуляция нажатий клавиш.
///
/// Зачем: когда пользователь выбрал запись из истории буфера, её надо не просто
/// положить в буфер, а СРАЗУ ВСТАВИТЬ в то приложение, где он работал.
/// Фокус мы у него не забирали (окно острова создано с WS_EX_NOACTIVATE),
/// поэтому достаточно отправить системе Ctrl+V — она доставит его в активное окно.
/// </summary>
public static class SendKeys
{
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_SHIFT = 0x10;
    private const byte VK_V = 0x56;
    private const byte VK_RETURN = 0x0D;

    public static void Paste()
    {
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, 0, UIntPtr.Zero);
        keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void Enter()
    {
        keybd_event(VK_RETURN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_RETURN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
