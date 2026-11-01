namespace DotShell.Core.Input;

using DotShell.Core.Native;

/// <summary>
/// Глобальные горячие клавиши (работают, даже когда фокус в другой программе).
/// Через RegisterHotKey — официальный способ, без хуков и без антивирусных подозрений.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly NativeMessageWindow _window;
    private readonly Dictionary<int, Action> _actions = new();
    private readonly List<int> _ids = new();
    private int _nextId = 0xC001;

    public HotkeyManager(NativeMessageWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_HOTKEY) return;
        if (_actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            try { action(); } catch { /* действие не должно ронять сообщение */ }
        }
    }

    /// <summary>
    /// Зарегистрировать сочетание.
    /// </summary>
    /// <param name="modifiers">Комбинация Win32.MOD_CONTROL / MOD_SHIFT / MOD_ALT / MOD_WIN / MOD_NOREPEAT.</param>
    /// <param name="vk">Код клавиши. Для букв — это код символа в верхнем регистре: (uint)'V'.</param>
    /// <returns>false, если сочетание уже занято другой программой.</returns>
    public bool Register(string name, uint modifiers, uint vk, Action action)
    {
        int id = _nextId++;
        if (!Win32.RegisterHotKey(_window.Handle, id, modifiers | Win32.MOD_NOREPEAT, vk))
            return false;

        _ids.Add(id);
        _actions[id] = action;
        return true;
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        foreach (var id in _ids) Win32.UnregisterHotKey(_window.Handle, id);
        _ids.Clear();
        _actions.Clear();
    }
}
