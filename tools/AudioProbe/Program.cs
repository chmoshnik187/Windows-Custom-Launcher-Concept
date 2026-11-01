using DotShell.Core.Audio;

// Разведчик аудио-подсистемы. Запускается руками, чтобы проверить interop
// до того, как он попадёт в интерфейс острова.
//
//   dotnet run --project tools/AudioProbe            — что видит система
//   dotnet run --project tools/AudioProbe -- route <pid> <deviceIndex|reset>
//
Console.OutputEncoding = System.Text.Encoding.UTF8;

using var router = new AudioRouter();

var devices = router.Devices();
Console.WriteLine($"== Устройства ({devices.Count}) ==");
for (int i = 0; i < devices.Count; i++)
    Console.WriteLine($"  [{i}] {(devices[i].IsDefault ? "*" : " ")} {devices[i].Name}   {devices[i].Id}");

Console.WriteLine();
Console.WriteLine($"== Приложения со звуком ==");
var apps = router.Apps();
if (apps.Count == 0) Console.WriteLine("  (тишина — ничего не играет)");
foreach (var a in apps)
{
    var dev = devices.FirstOrDefault(d => d.Id == a.DeviceId)?.Name ?? "(системное)";
    Console.WriteLine($"  pid={a.ProcessId,-6} {a.Name,-34} громк={a.Volume:P0} mute={a.Muted} играет={a.IsPlaying} → {dev}");
}

Console.WriteLine();
Console.WriteLine($"RoutingAvailable={router.RoutingAvailable}  Error={router.Error ?? "—"}");

if (args.Length >= 3 && args[0] == "vol")
{
    var pid = int.Parse(args[1]);
    var pct = float.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture);
    var ok = router.SetVolume(pid, pct / 100f);
    Console.WriteLine($"vol pid={pid} → {pct}%  ok={ok}");
}
if (args.Length >= 3 && args[0] == "route")
{
    var pid = int.Parse(args[1]);
    string? target = args[2] switch
    {
        "reset" => null,
        var s when int.TryParse(s, out var idx) && idx >= 0 && idx < devices.Count => devices[idx].Id,
        _ => null,
    };

    var before = router.GetAppDevice(pid);
    var ok = router.Route(pid, target);
    var after = router.GetAppDevice(pid);
    Console.WriteLine();
    Console.WriteLine($"route pid={pid} → {(target is null ? "системное" : target)}");
    Console.WriteLine($"  ok={ok}  before={before}  after={after}  error={router.Error ?? "—"}");
}

