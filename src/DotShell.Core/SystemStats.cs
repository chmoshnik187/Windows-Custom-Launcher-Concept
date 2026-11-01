namespace DotShell.Core;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

/// <summary>
/// Минимальная телеметрия для свёрнутого острова: загрузка CPU, батарея, память.
///
/// Специально БЕЗ PerformanceCounter: тот тянет тяжёлый пакет, медленно инициализируется
/// и на части машин капризничает. Вместо него — три старых добрых Win32-вызова:
///   • GetSystemTimes       — считаем долю не-idle времени между двумя замерами;
///   • GetSystemPowerStatus — процент батареи и статус зарядки;
///   • GlobalMemoryStatusEx — сколько памяти занято.
/// Все три стоят буквально ноль ресурсов и работают на любой Windows 10/11.
/// </summary>
public sealed class SystemStats : INotifyPropertyChanged, IDisposable
{
    private readonly Timer _timer;
    private long _prevIdle, _prevTotal;

    private double _cpu;
    private int _battery = -1;
    private bool _charging;
    private long _usedMemoryMb, _totalMemoryMb;

    /// <summary>Загрузка процессора, 0..100.</summary>
    public double Cpu { get => _cpu; private set => Set(ref _cpu, value); }

    /// <summary>Заряд батареи, 0..100. -1 = батареи нет (стационарный ПК).</summary>
    public int Battery { get => _battery; private set => Set(ref _battery, value); }

    public bool Charging { get => _charging; private set => Set(ref _charging, value); }

    public long UsedMemoryMb { get => _usedMemoryMb; private set => Set(ref _usedMemoryMb, value); }
    public long TotalMemoryMb { get => _totalMemoryMb; private set => Set(ref _totalMemoryMb, value); }

    /// <summary>Есть ли в машине батарея (для стационарного ПК чип батареи не показываем).</summary>
    public bool HasBattery => Battery >= 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SystemStats(int pollMs = 3000)
    {
        // Первый замер через 300 мс: сразу после старта CPU ещё не с чем сравнивать.
        _timer = new Timer(_ => Refresh(), null, 300, pollMs);
    }

    public void Start() => Refresh();

    public void Refresh()
    {
        try { RefreshCpu(); } catch { }
        try { RefreshPower(); } catch { }
        try { RefreshMemory(); } catch { }
    }

    private void RefreshCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return;

        long idleTicks = idle.ToInt64();
        // kernel time УЖЕ включает в себя idle — это documented behavior,
        // поэтому "всего" = kernel + user, а не kernel + user + idle.
        long totalTicks = kernel.ToInt64() + user.ToInt64();

        long dTotal = totalTicks - _prevTotal;
        long dIdle = idleTicks - _prevIdle;

        _prevIdle = idleTicks;
        _prevTotal = totalTicks;

        if (dTotal > 0)
            Cpu = Math.Clamp(100.0 * (dTotal - dIdle) / dTotal, 0, 100);
    }

    private void RefreshPower()
    {
        var status = new SYSTEM_POWER_STATUS();
        if (!GetSystemPowerStatus(ref status)) return;

        int percent = status.BatteryLifePercent;
        Battery = percent > 100 ? -1 : percent;
        Charging = status.ACLineStatus == 1 || (status.BatteryFlag & 0x08) != 0;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasBattery)));
    }

    private void RefreshMemory()
    {
        var ms = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };
        if (!GlobalMemoryStatusEx(ref ms)) return;

        TotalMemoryMb = (long)(ms.ullTotalPhys / 1024 / 1024);
        UsedMemoryMb = (long)((ms.ullTotalPhys - ms.ullAvailPhys) / 1024 / 1024);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public void Dispose() => _timer.Dispose();

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint DateTimeLow;
        public uint DateTimeHigh;
        public readonly long ToInt64() => ((long)DateTimeHigh << 32) | DateTimeLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;      // 0 = батарея, 1 = от сети, 255 = неизвестно
        public byte BatteryFlag;       // бит 3 (0x08) = идёт зарядка
        public byte BatteryLifePercent;// 0..100, 255 = неизвестно
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS status);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
}
