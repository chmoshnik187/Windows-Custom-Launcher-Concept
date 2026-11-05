namespace DotShell.Core.Network;

using Windows.Devices.Radios;

/// <summary>
/// Переключатели радио: Wi-Fi и Bluetooth.
///
/// Windows.Devices.Radios — единственный публичный способ включить/выключить адаптер без
/// прав администратора. Он есть в SDK-проекции нашего TFM (net8.0-windows10.0.22621.0),
/// поэтому пакетов не требуется.
///
/// Ограничение: метод может бросить UnauthorizedAccessException (политика устройства,
/// корпоративная машина). Тогда UI показывает кнопку перехода в системные настройки —
/// честнее, чем делать вид, что переключатель работает.
/// </summary>
public static class RadioControl
{
    public sealed record State(bool Available, bool WifiOn, bool BluetoothOn, string Note);

    public static async Task<State> GetAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
                return new State(false, false, false, "нет доступа к радио");

            var radios = await Radio.GetRadiosAsync();

            bool wifi = radios.Any(r => r.Kind == RadioKind.WiFi && r.State == RadioState.On);
            bool bluetooth = radios.Any(r => r.Kind == RadioKind.Bluetooth && r.State == RadioState.On);
            bool hasAny = radios.Any(r => r.Kind is RadioKind.WiFi or RadioKind.Bluetooth);

            return hasAny
                ? new State(true, wifi, bluetooth, string.Empty)
                : new State(false, false, false, "нет адаптеров");
        }
        catch (Exception ex)
        {
            return new State(false, false, false, ex.GetType().Name);
        }
    }

    /// <summary>Включить или выключить радио нужного типа. true — получилось.</summary>
    public static async Task<bool> SetAsync(RadioKind kind, bool on)
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) return false;

            var radios = await Radio.GetRadiosAsync();
            var target = radios.FirstOrDefault(r => r.Kind == kind);
            if (target is null) return false;

            var result = await target.SetStateAsync(on ? RadioState.On : RadioState.Off);
            return result == RadioAccessStatus.Allowed;
        }
        catch
        {
            return false;
        }
    }
}
