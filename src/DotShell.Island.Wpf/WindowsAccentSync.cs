namespace DotShell.Island.Wpf;

using System.Text.Json;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Native;
using DotShell.Core.Theming;
using DotShell.Core.Settings;

/// <summary>
/// Держит акцент Windows в согласии с акцентом шелла.
///
/// Почему по таймеру, а не по событию: акцент из обоев «догоняет» цель каждый тик
/// (так задумано, чтобы не было стробоскопа). Если писать в реестр на каждое
/// изменение, система будет получать десятки обновлений в секунду и мигать.
/// Поэтому раз в 10 секунд: изменился заметно — применили.
/// </summary>
public static class WindowsAccentSync
{
    private static DispatcherTimer? _timer;
    private static SettingsStore? _settings;
    private static ThemeBus? _bus;
    private static string _lastApplied = string.Empty;

    public static bool Enabled => _settings?.Current.WindowsAccentSync == true;

    /// <summary>Порог: мельче этого не трогаем — иначе мигание на плавном переходе.</summary>
    private const int Threshold = 6;

    public static void Attach(SettingsStore settings, ThemeBus bus)
    {
        _settings = settings;
        _bus = bus;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (settings.Current.WindowsAccentSync) ApplyNow(force: true);
    }

    public static void Enable()
    {
        if (_settings is null) return;

        // Первый раз запоминаем, что было у человека, чтобы откат вернул его цвет,
        // а не «синий по умолчанию».
        if (string.IsNullOrEmpty(_settings.Current.WindowsAccentOriginal))
        {
            var snapshot = WindowsAccent.Capture();
            if (snapshot is not null && snapshot.ColorizationColor != 0)
                _settings.Update(s => s.WindowsAccentOriginal = JsonSerializer.Serialize(snapshot));
            else
                Log.Info("accent: снимок не записан — читать было нечего");
        }

        _settings.Update(s => s.WindowsAccentSync = true);
        ApplyNow(force: true);
    }

    public static void Disable()
    {
        if (_settings is null) return;

        _settings.Update(s => s.WindowsAccentSync = false);
        _lastApplied = string.Empty;

        try
        {
            if (!string.IsNullOrEmpty(_settings.Current.WindowsAccentOriginal))
            {
                var snapshot = JsonSerializer.Deserialize<WindowsAccent.Snapshot>(_settings.Current.WindowsAccentOriginal);
                if (snapshot is not null && !WindowsAccent.Restore(snapshot))
                    Log.Info("accent: откат пропущен — снимок пустой");
            }
        }
        catch (Exception ex) { Log.Info($"accent restore failed: {ex.Message}"); }
    }

    private static void Tick()
    {
        if (!Enabled) return;
        ApplyNow(force: false);
    }

    private static void ApplyNow(bool force)
    {
        if (_bus is null) return;

        var hex = _bus.AccentHex;

        // Только строгий #RRGGBB. Раньше сюда мог попасть пустой или битый цвет,
        // Parse возвращал (0,0,0) — и акцент Windows становился чёрным.
        if (!System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9A-Fa-f]{6}$"))
        {
            Log.Info($"accent skip: недопустимый цвет «{hex}»");
            return;
        }

        if (!force && hex == _lastApplied) return;
        if (!force && !Different(_lastApplied, hex)) return;

        try
        {
            var (r, g, b) = Parse(hex);
            WindowsAccent.Apply(r, g, b);
            _lastApplied = hex;
            Log.Info($"accent: windows = {hex}");
        }
        catch (Exception ex) { Log.Info($"accent apply failed: {ex.Message}"); }
    }

    private static bool Different(string previous, string next)
    {
        if (previous.Length != 7 || next.Length != 7) return true;
        var (pr, pg, pb) = Parse(previous);
        var (nr, ng, nb) = Parse(next);
        return Math.Abs(pr - nr) > Threshold || Math.Abs(pg - ng) > Threshold || Math.Abs(pb - nb) > Threshold;
    }

    private static (byte, byte, byte) Parse(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length < 6) return (0, 0, 0);
        return (Convert.ToByte(hex[..2], 16), Convert.ToByte(hex.Substring(2, 2), 16), Convert.ToByte(hex.Substring(4, 2), 16));
    }
}
