// ======================================================================
//  Interop с недокументированным API управления устройствами приложений
// ======================================================================
//
//  Почему всё так «руками». WinRT-маршалинг (UnmanagedType.HString и
//  InterfaceIsIInspectable) в обычном WPF-процессе не зарегистрирован:
//  .NET отвечает «Cannot marshal parameter #1: Invalid managed/unmanaged
//  type combination». Поэтому HSTRING создаётся явно через combase,
//  а интерфейс забирается как обычный COM-указатель.

namespace DotShell.Core.Audio;

using System.Runtime.InteropServices;

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2,
}

/// <summary>
/// IAudioPolicyConfigFactory для Windows 11 21H2+.
///
/// Девятнадцать методов в начале — не наши: они уже есть в объекте, и нам
/// важно только их количество, чтобы SetPersistedDefaultAudioEndpoint попал
/// в правильный слот vtable. Это публично известное описание ABI, на нём
/// работает EarTrumpet; другого способа звать недокументированный метод нет.
/// </summary>
[ComImport]
[Guid("ab3d4648-e242-459f-b02f-541c70306324")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioPolicyConfigFactory
{
    // IInspectable: GetIids / GetRuntimeClassName / GetTrustLevel.
    // Объявлены пустыми ради смещения — у обычного COM-интерфейса этих
    // трёх слотов нет, а у WinRT-объекта они есть всегда.
    int __inspectable__GetIids();
    int __inspectable__GetRuntimeClassName();
    int __inspectable__GetTrustLevel();

    int __incomplete__add_CtxVolumeChange();
    int __incomplete__remove_CtxVolumeChanged();
    int __incomplete__add_RingerVibrateStateChanged();
    int __incomplete__remove_RingerVibrateStateChange();
    int __incomplete__SetVolumeGroupGainForId();
    int __incomplete__GetVolumeGroupGainForId();
    int __incomplete__GetActiveVolumeGroupForEndpointId();
    int __incomplete__GetVolumeGroupsForEndpoint();
    int __incomplete__GetCurrentVolumeContext();
    int __incomplete__SetVolumeGroupMuteForId();
    int __incomplete__GetVolumeGroupMuteForId();
    int __incomplete__SetRingerVibrateState();
    int __incomplete__GetRingerVibrateState();
    int __incomplete__SetPreferredChatApplication();
    int __incomplete__ResetPreferredChatApplication();
    int __incomplete__GetPreferredChatApplication();
    int __incomplete__GetCurrentChatApplications();
    int __incomplete__add_ChatContextChanged();
    int __incomplete__remove_ChatContextChanged();

    [PreserveSig]
    int SetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, IntPtr deviceId);

    /// <summary>Возвращает HSTRING в сыром виде — строку собираем сами.</summary>
    [PreserveSig]
    int GetPersistedDefaultAudioEndpoint(uint processId, EDataFlow flow, ERole role, out IntPtr deviceId);

    [PreserveSig]
    int ClearAllPersistedApplicationDefaultEndpoints();
}

internal static class AudioPolicyConfigFactory
{
    public static IAudioPolicyConfigFactory Create()
    {
        var hstr = Combase.CreateHString("Windows.Media.Internal.AudioPolicyConfig");
        try
        {
            var iid = typeof(IAudioPolicyConfigFactory).GUID;
            var hr = Combase.RoGetActivationFactory(hstr, ref iid, out var raw);
            Marshal.ThrowExceptionForHR(hr);
            if (raw == IntPtr.Zero) throw new InvalidOperationException("фабрика не создана");

            try
            {
                // GetTypedObjectForIUnknown оборачивает указатель в RCW нужного
                // интерфейса: тот же vtable, но без WinRT-маршалинга.
                return (IAudioPolicyConfigFactory)Marshal.GetTypedObjectForIUnknown(
                    raw, typeof(IAudioPolicyConfigFactory));
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        finally
        {
            Combase.DeleteHString(hstr);
        }
    }
}

internal static class Combase
{
    [DllImport("combase.dll")]
    public static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll")]
    public static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string src, uint length, out IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    public static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);

    public static IntPtr CreateHString(string value)
    {
        var hr = WindowsCreateString(value, (uint)value.Length, out var hstr);
        Marshal.ThrowExceptionForHR(hr);
        return hstr;
    }

    public static void DeleteHString(IntPtr hstr)
    {
        if (hstr != IntPtr.Zero) WindowsDeleteString(hstr);
    }

    public static string ReadHString(IntPtr hstr)
    {
        if (hstr == IntPtr.Zero) return string.Empty;
        var buf = WindowsGetStringRawBuffer(hstr, out var len);
        return buf == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(buf, (int)len) ?? string.Empty;
    }
}
