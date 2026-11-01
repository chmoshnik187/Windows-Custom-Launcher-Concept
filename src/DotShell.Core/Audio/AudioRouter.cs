namespace DotShell.Core.Audio;

using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

/// <summary>Устройство вывода звука (наушники, колонки, HDMI монитора).</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefault);

/// <summary>Приложение, которое сейчас звучит (или звучало в этой сессии).</summary>
public sealed record AudioApp(
    int ProcessId,
    string Name,
    string? ExePath,
    string DeviceId,
    float Volume,
    bool Muted,
    bool IsPlaying);

/// <summary>
/// Аудио-роутер: видит все устройства вывода, все приложения со звуком
/// и умеет перекинуть конкретное приложение на конкретное устройство.
///
/// Это «виджет №18» из списка идей: Discord — в наушники, игра — в колонки,
/// браузер — в HDMI, не создавая виртуальных микшеров вроде VoiceMeeter.
///
/// Как это работает. Windows с 10 1803 хранит для каждого процесса
/// «привязанное» устройство вывода (persisted default endpoint) — ровно то,
/// что настраивается в Параметрах → Система → Звук → Громкость приложений.
/// Настройка живёт в недокументированном, но стабильном интерфейсе
/// IAudioPolicyConfigFactory (класс Windows.Media.Internal.AudioPolicyConfig).
/// Тот же приём использует EarTrumpet — здесь он повторён на чистом interop,
/// без WinRT-проекции, чтобы не тащить в ядро лишних зависимостей.
///
/// Честные границы: приложение, которое уже играет, переезжает на другое
/// устройство не мгновенно, а при следующем запуске потока (пауза/плей,
/// новый трек). Так работает сама Windows — это не наша недоработка.
/// </summary>
public sealed class AudioRouter : IDisposable
{
    // Идентификатор «внутреннего» устройства в формате MMDeviceAPI для policy-фабрики.
    // Windows хранит его как SWD-путь, а не как короткий {0.0.0...} из MMDevice.ID.
    private const string MmdevapiToken = @"\\?\SWD#MMDEVAPI#";
    private const string DeviceInterfaceRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private MMDeviceEnumerator? _enumerator;
    private IAudioPolicyConfigFactory? _policy;
    private bool _policyFailed;

    /// <summary>Можно ли переключать устройства (на старых сборках Windows — нет).</summary>
    public bool RoutingAvailable => _policy is not null || TryGetPolicy(out _);

    /// <summary>Причина, по которой роутинг недоступен (для подсказки в интерфейсе).</summary>
    public string? Error { get; private set; }

    public AudioRouter()
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    // ------------------------------------------------------------------
    //  Устройства
    // ------------------------------------------------------------------

    /// <summary>Все активные устройства вывода и признак «выбранное в системе».</summary>
    public IReadOnlyList<AudioDevice> Devices()
    {
        var list = new List<AudioDevice>();
        if (_enumerator is null) return list;

        string defaultId;
        try { defaultId = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID; }
        catch { defaultId = string.Empty; }

        try
        {
            foreach (var dev in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                string id, name;
                try { id = dev.ID; name = dev.FriendlyName; }
                catch { continue; }

                list.Add(new AudioDevice(id, CleanName(name), id == defaultId));
            }
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }

        return list;
    }

    /// <summary>«Динамики (Realtek High Definition Audio)» → «Динамики · Realtek».</summary>
    private static string CleanName(string friendly)
    {
        var text = friendly.Trim();
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        if (open <= 0 || close <= open) return text;

        var head = text[..open].Trim();
        var tail = text[(open + 1)..close].Trim();

        // Длинные драйверные хвосты вроде "(Realtek High Definition Audio)" режем по первому пробелу
        var shortTail = tail.Split(' ')[0];
        return string.IsNullOrWhiteSpace(shortTail) ? head : $"{head} · {shortTail}";
    }

    // ------------------------------------------------------------------
    //  Приложения со звуком
    // ------------------------------------------------------------------

    /// <summary>
    /// Приложения, у которых есть аудиосессии. Одно приложение может держать
    /// сессии на нескольких устройствах сразу (после переезда) — поэтому
    /// склеиваем по PID и запоминаем то устройство, где сессия активна.
    /// </summary>
    public IReadOnlyList<AudioApp> Apps()
    {
        var byPid = new Dictionary<int, AudioApp>();
        if (_enumerator is null) return Array.Empty<AudioApp>();

        MMDeviceCollection? devices = null;
        try { devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active); }
        catch (Exception ex) { Error = ex.Message; return Array.Empty<AudioApp>(); }

        foreach (var dev in devices)
        {
            SessionCollection? sessions = null;
            try { sessions = dev.AudioSessionManager.Sessions; }
            catch { continue; }

            string devId;
            try { devId = dev.ID; } catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                AudioSessionControl session;
                try { session = sessions[i]; }
                catch { continue; }

                uint pid;
                try { pid = session.GetProcessID; }
                catch { continue; }

                // PID 0 — системные звуки Windows; их переносить некуда.
                if (pid == 0) continue;

                var state = AudioSessionState.AudioSessionStateInactive;
                try { state = session.State; } catch { }

                if (state == AudioSessionState.AudioSessionStateExpired) continue;

                float volume = 1f;
                bool muted = false;
                try
                {
                    var vol = session.SimpleAudioVolume;
                    volume = vol.Volume;
                    muted = vol.Mute;
                }
                catch { }

                var (name, exe) = DescribeProcess((int)pid);
                var playing = state == AudioSessionState.AudioSessionStateActive;

                if (byPid.TryGetValue((int)pid, out var existing))
                {
                    // Активная сессия важнее «висящей» неактивной.
                    if (playing && !existing.IsPlaying)
                        byPid[(int)pid] = existing with { IsPlaying = true, DeviceId = devId };
                    continue;
                }

                // Показываем «привязанное» устройство приложения, а не то,
                // где сессия живёт сейчас: именно оно и есть его настройка.
                var persisted = GetAppDevice((int)pid);
                byPid[(int)pid] = new AudioApp(
                    (int)pid, name, exe,
                    string.IsNullOrEmpty(persisted) ? devId : persisted,
                    volume, muted, playing);
            }

        }

        return byPid.Values
            .OrderByDescending(a => a.IsPlaying)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static (string Name, string? Exe) DescribeProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            string? exe = null;
            try { exe = p.MainModule?.FileName; } catch { }
            var name = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle;
            // Заголовок окна может быть длинным («... - YouTube — Mozilla Firefox») — режем.
            if (name.Length > 42) name = name[..42] + "…";
            return (name, exe);
        }
        catch
        {
            return ($"процесс {pid}", null);
        }
    }

    // ------------------------------------------------------------------
    //  Громкость и mute конкретного приложения
    // ------------------------------------------------------------------

    public bool SetVolume(int pid, float volume)
    {
        var ok = false;
        foreach (var (session, _) in SessionsOf(pid))
        {
            try
            {
                session.SimpleAudioVolume.Volume = Math.Clamp(volume, 0f, 1f);
                ok = true;
            }
            catch { }
        }
        return ok;
    }

    public bool SetMute(int pid, bool muted)
    {
        var ok = false;
        foreach (var (session, _) in SessionsOf(pid))
        {
            try
            {
                session.SimpleAudioVolume.Mute = muted;
                ok = true;
            }
            catch { }
        }
        return ok;
    }

    private List<(AudioSessionControl Session, MMDevice Device)> SessionsOf(int pid)
    {
        var found = new List<(AudioSessionControl, MMDevice)>();
        if (_enumerator is null) return found;

        MMDeviceCollection? devices = null;
        try { devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active); }
        catch { return found; }

        foreach (var dev in devices)
        {
            SessionCollection sessions;
            try { sessions = dev.AudioSessionManager.Sessions; }
            catch { continue; }

            for (int i = 0; i < sessions.Count; i++)
            {
                try
                {
                    var session = sessions[i];
                    uint sid = session.GetProcessID;
                    if (sid == (uint)pid) found.Add((session, dev));
                }
                catch { }
            }
        }
        return found;
    }

    // ------------------------------------------------------------------
    //  Переключение устройства для приложения
    // ------------------------------------------------------------------

    /// <summary>
    /// Перевести приложение на устройство <paramref name="deviceId"/>.
    /// Пустая строка или null — вернуть «как в системе» (сбросить привязку).
    /// </summary>
    public bool Route(int pid, string? deviceId)
    {
        if (!TryGetPolicy(out var policy)) return false;

        IntPtr hstring = IntPtr.Zero;
        try
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                var full = MmdevapiToken + deviceId + DeviceInterfaceRender;
                hstring = Combase.CreateHString(full);
            }

            // Обе роли — ровно как это делает панель Windows и EarTrumpet:
            // иначе часть приложений останется на старом устройстве.
            var a = policy.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.Render, ERole.Multimedia, hstring);
            var b = policy.SetPersistedDefaultAudioEndpoint((uint)pid, EDataFlow.Render, ERole.Console, hstring);

            var ok = a >= 0 || b >= 0;
            if (!ok) Error = $"Windows отказала в переключении (0x{a:X8})";
            return ok;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            Combase.DeleteHString(hstring);
        }
    }

    /// <summary>Устройство, привязанное к приложению ("" — системное по умолчанию).</summary>
    public string GetAppDevice(int pid)
    {
        if (!TryGetPolicy(out var policy)) return string.Empty;

        IntPtr raw = IntPtr.Zero;
        try
        {
            var hr = policy.GetPersistedDefaultAudioEndpoint(
                (uint)pid, EDataFlow.Render, ERole.Multimedia | ERole.Console, out raw);
            if (hr < 0 || raw == IntPtr.Zero) return string.Empty;
            return UnpackDeviceId(Combase.ReadHString(raw));
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            // HSTRING выделяет вызываемая сторона — освобождаем мы.
            Combase.DeleteHString(raw);
        }
    }

    private static string UnpackDeviceId(string value)
    {
        var id = value;
        if (id.StartsWith(MmdevapiToken, StringComparison.OrdinalIgnoreCase))
            id = id[MmdevapiToken.Length..];
        if (id.EndsWith(DeviceInterfaceRender, StringComparison.OrdinalIgnoreCase))
            id = id[..^DeviceInterfaceRender.Length];
        return id;
    }

    // ------------------------------------------------------------------
    //  Ленивая инициализация policy-фабрики
    // ------------------------------------------------------------------

    private bool TryGetPolicy(out IAudioPolicyConfigFactory policy)
    {
        policy = _policy!;
        if (_policy is not null) return true;
        if (_policyFailed) return false;

        try
        {
            policy = AudioPolicyConfigFactory.Create();
            _policy = policy;
            return true;
        }
        catch (Exception ex)
        {
            _policyFailed = true;
            Error = "Windows этой сборки не отдаёт управление устройствами приложений: " + ex.Message;
            return false;
        }
    }

    public void Dispose()
    {
        try
        {
            if (_policy is not null && Marshal.IsComObject(_policy))
                Marshal.ReleaseComObject(_policy);
        }
        catch { }
        _policy = null;

        _enumerator?.Dispose();
        _enumerator = null;
    }
}

