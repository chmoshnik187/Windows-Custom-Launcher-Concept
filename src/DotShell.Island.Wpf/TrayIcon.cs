namespace DotShell.Island.Wpf;

using System.Runtime.InteropServices;
using System.Windows.Interop;

/// <summary>
/// Иконка в трее на чистом Win32 (Shell_NotifyIcon), без WinForms.
///
/// Меню — не WPF-попап, а своё окно (<see cref="TrayMenu"/>): попап с Shell_NotifyIcon
/// не держится, потому что у скрытого окна нет нормальной активации. Проверял дважды
/// (message-only и настоящее скрытое окно) — оба раза меню закрывалось само.
/// Своё окно решает это без борьбы за фокус.
///
/// WinForms (System.Windows.Forms + .Primitives + .Design) — 21 МБ из 70 в публикации
/// и источник псевдонимов типов. После переезда на Win32 он не нужен вовсе.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WmApp = 0x8000;
    private const int NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const int NiifInfo = 0x1;
    private const int NimAdd = 0, NimModify = 1, NimDelete = 2;

    private const int WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonUp = 0x0205;
    private const int NinBalloonUserClick = 0x0405;

    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExToolWindow = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int Size;
        public IntPtr Window;
        public int Id;
        public int Flags;
        public int CallbackMessage;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public int State;
        public int StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public int TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public int InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string file, int index, int cx, int cy,
        IntPtr[] icons, uint[] ids, uint count, uint flags);

    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly uint _callbackMessage;
    private readonly uint _taskbarCreated;
    private readonly Action _onToggle;

    private TrayMenu? _menu;
    private bool _iconAdded;

    public TrayIcon(Action onToggle, Action onResetPosition, Action onOpenFolder, Action onExit,
        Action<TrayMenu>? extend = null)
    {
        _onToggle = onToggle;

        var parameters = new HwndSourceParameters("DotShell tray owner")
        {
            WindowStyle = WsPopup,
            ExtendedWindowStyle = WsExToolWindow,
            Width = 1,
            Height = 1,
            PositionX = -32000,
            PositionY = -32000,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        _callbackMessage = WmApp + 1;
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");

        _icon = ExtractIcon();
        BuildMenu(onResetPosition, onOpenFolder, onExit, extend);
        AddIcon();
    }

    private void BuildMenu(Action onResetPosition, Action onOpenFolder, Action onExit, Action<TrayMenu>? extend)
    {
        var menu = new TrayMenu();

        menu.Add("Раскрыть / свернуть остров", () => _onToggle());
        menu.Add("Показать остров (сброс позиции)", onResetPosition);
        menu.Add("Папка настроек", onOpenFolder);

        extend?.Invoke(menu);

        // «Выйти» всегда последним: пункты расширения добавляются выше.
        menu.AddSeparator();
        menu.Add("Выйти", onExit);

        _menu = menu;
    }

    private IntPtr ExtractIcon()
    {
        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path))
            {
                var icons = new IntPtr[1];
                var ids = new uint[1];
                if (PrivateExtractIcons(path, 0, 16, 16, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
                    return icons[0];
            }
        }
        catch { }

        return LoadIcon(IntPtr.Zero, new IntPtr(32512));
    }

    private NotifyIconData NewData() => new()
    {
        Size = Marshal.SizeOf<NotifyIconData>(),
        Window = _source.Handle,
        Id = 1,
        Tip = string.Empty,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void AddIcon()
    {
        var data = NewData();
        data.Flags = NifMessage | NifIcon | NifTip | NifShowTip;
        data.CallbackMessage = (int)_callbackMessage;
        data.Icon = _icon;
        data.Tip = "DotShell — остров";

        _iconAdded = Shell_NotifyIcon(NimAdd, ref data);
        Log.Info($"tray: иконка добавлена: {_iconAdded}, окно {_source.Handle}");
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated && message != 0)
        {
            // Проводник перезапустили — иконку надо добавить заново.
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (message == (int)_callbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case WmLButtonUp:
                    _onToggle();
                    handled = true;
                    break;

                case WmRButtonUp:
                case WmContextMenu:
                    ShowMenu();
                    handled = true;
                    break;

                case NinBalloonUserClick:
                    _onToggle();
                    handled = true;
                    break;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowMenu()
    {
        if (_menu is null) return;

        try
        {
            _menu.ShowAtCursor();
            Log.Info("tray: меню открыто");
        }
        catch (Exception ex)
        {
            Log.Info($"tray: меню не открылось: {ex.Message}");
        }
    }

    /// <summary>Пузырь над трее: напоминания и таймер доходят и из-под окон.</summary>
    public void Notify(string title, string text)
    {
        try
        {
            var data = NewData();
            data.Flags = NifInfo;
            data.InfoTitle = title.Length > 63 ? title[..63] : title;
            data.Info = text.Length > 255 ? text[..255] : text;
            data.InfoFlags = NiifInfo;
            Shell_NotifyIcon(NimModify, ref data);
        }
        catch { }
    }

    public void Dispose()
    {
        try
        {
            if (_iconAdded)
            {
                var data = NewData();
                Shell_NotifyIcon(NimDelete, ref data);
            }
        }
        catch { }

        try { _source.RemoveHook(WndProc); _source.Dispose(); } catch { }
        try { if (_icon != IntPtr.Zero) DestroyIcon(_icon); } catch { }
    }
}
