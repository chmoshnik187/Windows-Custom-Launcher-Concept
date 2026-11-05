namespace DotShell.Core.Network;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Состояние Wi-Fi: сеть, сигнал, подключено или нет.
///
/// Читаем через <c>netsh wlan show interfaces</c>: публичного WinRT-API без пакетов нет,
/// а netsh есть на любой Windows 10/11. Запрос идёт ~50 мс, поэтому можно обновлять
/// раз в несколько секунд, не нагружая систему.
///
/// Ограничение честное: это <b>чтение</b> состояния. Включать/выключать радио мы не
/// пытаемся — netsh для этого требует прав администратора, а просить их ради тумблера
/// в панели неправильно. Вместо тумблера — кнопка «СЕТИ», открывающая системный список.
/// </summary>
public static class WifiInfo
{
    public sealed record Status(bool HasAdapter, bool Connected, string Ssid, int Signal, string State)
    {
        public static Status NoAdapter => new(false, false, string.Empty, 0, "нет адаптера");
    }

    public static Status Query()
    {
        try
        {
            var start = new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };

            using var process = Process.Start(start);
            if (process is null) return Status.NoAdapter;

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);

            if (string.IsNullOrWhiteSpace(output))
                return Status.NoAdapter;

            string ssid = string.Empty;
            int signal = 0;
            string state = string.Empty;

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();

                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase)
                    && !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    ssid = AfterColon(line);
                }
                else if (line.StartsWith("Signal", StringComparison.OrdinalIgnoreCase))
                {
                    var text = AfterColon(line).Replace("%", string.Empty).Trim();
                    int.TryParse(text, out signal);
                }
                else if (line.StartsWith("State", StringComparison.OrdinalIgnoreCase)
                         || line.StartsWith("Состояние", StringComparison.OrdinalIgnoreCase))
                {
                    state = AfterColon(line);
                }
            }

            bool connected = state.Contains("connected", StringComparison.OrdinalIgnoreCase)
                             || state.Contains("подключено", StringComparison.OrdinalIgnoreCase)
                             || (!string.IsNullOrEmpty(ssid) && signal > 0);

            return new Status(true, connected, ssid, signal, state);
        }
        catch
        {
            return Status.NoAdapter;
        }
    }

    private static string AfterColon(string line)
    {
        int index = line.IndexOf(':');
        if (index < 0) return string.Empty;

        return line[(index + 1)..].Trim();
    }
}
