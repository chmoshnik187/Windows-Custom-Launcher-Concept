namespace DotShell.Core.Native;

using System.Runtime.InteropServices;

/// <summary>
/// Рабочие столы Windows (виртуальные рабочие столы): сколько их, какой текущий
/// и переключение.
///
/// Публичного API для переключения у Windows нет — это знаменитая дыра. Что есть:
///
///   • IVirtualDesktopManager (документированный) — только «перенести окно на
///     стол», этим остров догоняет текущий стол после смены;
///   • ImmersiveShell → IVirtualDesktopManagerInternal (недокументированный COM,
///     IID {53F5CA0B-…} актуален для 24H2/25H2/26H2) — GetCount/GetCurrentDesktop/
///     GetDesktops/SwitchDesktop. Переключение мгновенное, без анимации;
///   • реестр HKCU\...\Explorer\VirtualDesktops — ленивый кэш: до создания второго
///     стола значений нет, поэтому он только фолбэк;
///   • Win+Ctrl+Left/Right — последний фолбэк, если COM не поднялся.
///
/// Проверено на этой машине 26H2 (26300.9974): COM создаётся через
/// CLSCTX_LOCAL_SERVER, GetCount/GetDesktops согласуются с реестром,
/// SwitchDesktop реально переключает и после переключения индекс реестра меняется.
/// </summary>
public static class VirtualDesktops
{
    private const int ClsCtxLocalServer = 4;

    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid IidServiceProvider = new("6D5140C1-7436-11CE-8034-00AA006009FA");
    private static readonly Guid ClsidVdManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid IidVdManagerInternal26100 = new("53F5CA0B-158F-4124-900C-057158060B27");
    private static readonly Guid IidVirtualDesktop = new("3F07F4BE-B107-441A-AF0F-39D82529072C");

    private const int VkLeftWin = 0x5B;
    private const int VkControl = 0x11;
    private const int VkLeft = 0x25;
    private const int VkRight = 0x27;
    private const uint KeyEventKeyUp = 0x0002;

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, out IntPtr obj);
    }

    [ComImport]
    [Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManagerInternal
    {
        uint GetCount();
        void MoveViewToDesktop(IntPtr view, IntPtr desktop);
        bool CanViewMoveDesktops(IntPtr view);
        IntPtr GetCurrentDesktop();
        IntPtr GetDesktops();
        IntPtr GetAdjacentDesktop(IntPtr desktop, int direction);
        void SwitchDesktop(IntPtr desktop);
        void SwitchDesktopAndMoveForegroundView(IntPtr desktop);
        IntPtr CreateDesktopW();
        void MoveDesktop(IntPtr desktop, uint index);
        void RemoveDesktop(IntPtr desktop, IntPtr fallback);
        IntPtr FindDesktop(ref Guid id);
    }

    [ComImport]
    [Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IObjectArray
    {
        uint GetCount();
        IntPtr GetAt(uint index, ref Guid riid);
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    private class VirtualDesktopManagerCoClass
    {
    }

    private static readonly object Gate = new();
    private static IVirtualDesktopManager? _moveManager;
    private static bool _moveManagerTried;

    /// <summary>Сколько рабочих столов сейчас.</summary>
    public static int Count => ReadState().Count;

    /// <summary>Индекс текущего стола (0-based).</summary>
    public static int CurrentIndex => ReadState().Index;

    /// <summary>Одно чтение состояния: сначала недокументированный COM, потом реестр.</summary>
    public static (int Count, int Index) ReadState()
    {
        if (TryReadCom(out int count, out int index)) return (count, index);
        return (RegistryCount(), RegistryIndex());
    }

    /// <summary>Переключиться на соседний стол (по кругу).</summary>
    public static bool SwitchNext() => SwitchBy(1);

    public static bool SwitchPrev() => SwitchBy(-1);

    /// <summary>Переключиться на конкретный стол по индексу (0-based).</summary>
    public static bool SwitchTo(int index)
    {
        lock (Gate)
        {
            if (TrySwitchComTo(index)) return true;
            // Фолбэк: шагаем клавишами от текущего.
            var (count, current) = ReadState();
            if (count <= 1 || index < 0 || index >= count) return false;
            int delta = index - current;
            if (delta == 0) return true;
            int step = delta > 0 ? 1 : -1;
            for (int i = 0; i < Math.Abs(delta); i++)
                if (!TrySwitchKeys(step > 0)) return false;
            return true;
        }
    }

    private static bool TrySwitchComTo(int index)
    {
        lock (Gate)
        {
            if (!TryOpenManager(out var manager) || manager is null) return false;

            IntPtr current = IntPtr.Zero, arrayPtr = IntPtr.Zero, target = IntPtr.Zero;
            try
            {
                current = manager.GetCurrentDesktop();
                arrayPtr = manager.GetDesktops();
                if (arrayPtr == IntPtr.Zero) return false;

                var array = (IObjectArray)Marshal.GetObjectForIUnknown(arrayPtr);
                uint items = array.GetCount();
                if (index < 0 || index >= items) return false;

                var iid = IidVirtualDesktop;
                target = array.GetAt((uint)index, ref iid);
                if (target == IntPtr.Zero) return false;

                manager.SwitchDesktop(target);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (target != IntPtr.Zero) Marshal.Release(target);
                if (arrayPtr != IntPtr.Zero) Marshal.Release(arrayPtr);
                if (current != IntPtr.Zero) Marshal.Release(current);
            }
        }
    }

    private static bool SwitchBy(int direction)
    {
        lock (Gate)
        {
            if (TrySwitchCom(direction)) return true;
            return TrySwitchKeys(direction > 0);
        }
    }

    /// <summary>
    /// Перенести окно на текущий стол. Нужно потому, что overlay-окна живут на
    /// одном столе: без переноса после переключения остров исчезает.
    /// </summary>
    public static bool MoveWindowToCurrent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var manager = MoveManager;
            var current = ReadCurrentGuid();
            if (manager is null || current is null) return false;

            var guid = current.Value;
            return manager.MoveWindowToDesktop(hwnd, ref guid) == 0;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================
    //  Недокументированный COM: ImmersiveShell → ManagerInternal
    // ================================================================

    private static bool TryOpenManager(out IVirtualDesktopManagerInternal? manager)
    {
        manager = null;
        IntPtr shell = IntPtr.Zero, mgrPtr = IntPtr.Zero;
        try
        {
            var clsid = ClsidImmersiveShell;
            var iidSp = IidServiceProvider;
            if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxLocalServer, ref iidSp, out shell) != 0)
                return false;

            var sp = (IServiceProvider)Marshal.GetObjectForIUnknown(shell);
            var service = ClsidVdManagerInternal;
            var iid = IidVdManagerInternal26100;
            if (sp.QueryService(ref service, ref iid, out mgrPtr) != 0 || mgrPtr == IntPtr.Zero)
                return false;

            manager = (IVirtualDesktopManagerInternal)Marshal.GetObjectForIUnknown(mgrPtr);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (mgrPtr != IntPtr.Zero) Marshal.Release(mgrPtr);
            if (shell != IntPtr.Zero) Marshal.Release(shell);
        }
    }

    private static bool TryReadCom(out int count, out int index)
    {
        count = 1;
        index = 0;

        lock (Gate)
        {
            if (!TryOpenManager(out var manager) || manager is null) return false;

            IntPtr current = IntPtr.Zero, arrayPtr = IntPtr.Zero;
            try
            {
                uint c = manager.GetCount();
                if (c == 0 || c > 64) return false;
                count = (int)c;

                current = manager.GetCurrentDesktop();
                arrayPtr = manager.GetDesktops();
                if (arrayPtr == IntPtr.Zero) return true;

                var array = (IObjectArray)Marshal.GetObjectForIUnknown(arrayPtr);
                uint items = array.GetCount();
                var iid = IidVirtualDesktop;

                for (uint i = 0; i < items; i++)
                {
                    IntPtr desktop = array.GetAt(i, ref iid);
                    bool same = desktop == current;
                    Marshal.Release(desktop);
                    if (same) { index = (int)i; break; }
                }

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (arrayPtr != IntPtr.Zero) Marshal.Release(arrayPtr);
                if (current != IntPtr.Zero) Marshal.Release(current);
            }
        }
    }

    private static bool TrySwitchCom(int direction)
    {
        lock (Gate)
        {
            if (!TryOpenManager(out var manager) || manager is null) return false;

            IntPtr current = IntPtr.Zero, arrayPtr = IntPtr.Zero, target = IntPtr.Zero;
            try
            {
                current = manager.GetCurrentDesktop();
                arrayPtr = manager.GetDesktops();
                if (arrayPtr == IntPtr.Zero) return false;

                var array = (IObjectArray)Marshal.GetObjectForIUnknown(arrayPtr);
                uint items = array.GetCount();
                if (items <= 1) return false;

                var iid = IidVirtualDesktop;
                int index = -1;
                for (uint i = 0; i < items; i++)
                {
                    IntPtr desktop = array.GetAt(i, ref iid);
                    if (desktop == current) index = (int)i;
                    Marshal.Release(desktop);
                }
                if (index < 0) return false;

                int next = (index + direction + (int)items) % (int)items;
                target = array.GetAt((uint)next, ref iid);
                if (target == IntPtr.Zero) return false;

                manager.SwitchDesktop(target);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (target != IntPtr.Zero) Marshal.Release(target);
                if (arrayPtr != IntPtr.Zero) Marshal.Release(arrayPtr);
                if (current != IntPtr.Zero) Marshal.Release(current);
            }
        }
    }

    private static bool TrySwitchKeys(bool right)
    {
        try
        {
            byte direction = (byte)(right ? VkRight : VkLeft);
            keybd_event(VkLeftWin, 0, 0, IntPtr.Zero);
            keybd_event(VkControl, 0, 0, IntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(direction, 0, 0, IntPtr.Zero);
            Thread.Sleep(20);
            keybd_event(direction, 0, KeyEventKeyUp, IntPtr.Zero);
            keybd_event(VkControl, 0, KeyEventKeyUp, IntPtr.Zero);
            keybd_event(VkLeftWin, 0, KeyEventKeyUp, IntPtr.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================================================================
    //  Реестр: ленивый кэш, только фолбэк
    // ================================================================

    private static int RegistryCount()
    {
        var ids = ReadDesktopIds();
        return Math.Max(1, ids?.Count ?? 0);
    }

    private static int RegistryIndex()
    {
        var ids = ReadDesktopIds();
        if (ids is null || ids.Count <= 1) return 0;

        var current = ReadCurrentGuid();
        if (current is { } guid)
            for (int i = 0; i < ids.Count; i++)
                if (ids[i] == guid) return i;

        return 0;
    }

    private static List<Guid>? ReadDesktopIds()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] raw || raw.Length < 16) return null;

            var list = new List<Guid>();
            for (int offset = 0; offset + 16 <= raw.Length; offset += 16)
                list.Add(new Guid(raw.AsSpan(offset, 16).ToArray()));
            return list;
        }
        catch
        {
            return null;
        }
    }

    private static Guid? ReadCurrentGuid()
    {
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
            if (key?.GetValue("CurrentVirtualDesktop") is byte[] raw && raw.Length == 16)
                return new Guid(raw);

            using var fallback = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops");
            if (fallback?.GetValue("CurrentVirtualDesktop") is byte[] raw2 && raw2.Length == 16)
                return new Guid(raw2);
        }
        catch
        {
            // нет ключей — один стол
        }
        return null;
    }

    private static IVirtualDesktopManager? MoveManager
    {
        get
        {
            if (_moveManagerTried) return _moveManager;
            _moveManagerTried = true;
            try { _moveManager = (IVirtualDesktopManager)new VirtualDesktopManagerCoClass(); }
            catch { _moveManager = null; }
            return _moveManager;
        }
    }
}
