namespace DotShell.Core.Audio;

using NAudio.CoreAudioApi;

/// <summary>
/// Системная громкость: тот самый ползунок, что крутит человек в трее Windows,
/// но доступный из острова. Используем CoreAudio (IAudioEndpointVolume) через
/// NAudio — тот же пакет, что уже тащит нам WASAPI-захват для эквалайзера.
///
/// Подписка OnVolumeNotification даёт событие "громкость поменяли СНАРУЖИ"
/// (клавиши клавиатуры, микшер, другое приложение) — ползунок острова обязан
/// это увидеть, иначе он начнёт врать.
/// </summary>
public sealed class SystemVolume : IDisposable
{
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;

    /// <summary>false, если устройства нет (RDP, отключён звук) — UI прячет ползунок.</summary>
    public bool Available { get; private set; }

    /// <summary>Громкость 0..1.</summary>
    public float Level
    {
        get
        {
            try { return _device?.AudioEndpointVolume.MasterVolumeLevelScalar ?? 0f; }
            catch { return 0f; }
        }
    }

    public bool Muted
    {
        get
        {
            try { return _device?.AudioEndpointVolume.Mute ?? true; }
            catch { return true; }
        }
    }

    /// <summary>Громкость изменилась (нами или кем-то ещё).</summary>
    public event Action? Changed;

    public SystemVolume()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _device.AudioEndpointVolume.OnVolumeNotification += OnExternalVolumeChange;
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    private void OnExternalVolumeChange(AudioVolumeNotificationData data) => Changed?.Invoke();

    public void SetLevel(float value)
    {
        if (!Available) return;
        try
        {
            var vol = _device!.AudioEndpointVolume;
            vol.MasterVolumeLevelScalar = Math.Clamp(value, 0f, 1f);
            if (value > 0f && vol.Mute) vol.Mute = false; // покрутил ползунок — значит хочешь слышать
        }
        catch { }
        Changed?.Invoke();
    }

    public void SetMute(bool muted)
    {
        if (!Available) return;
        try { _device!.AudioEndpointVolume.Mute = muted; }
        catch { }
        Changed?.Invoke();
    }

    public void ToggleMute() => SetMute(!Muted);

    public void Dispose()
    {
        try
        {
            if (_device is not null) _device.AudioEndpointVolume.OnVolumeNotification -= OnExternalVolumeChange;
            _device?.Dispose();
            _enumerator?.Dispose();
        }
        catch { }
    }
}
