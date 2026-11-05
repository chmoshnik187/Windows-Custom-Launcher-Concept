namespace DotShell.Core.Diagnostics;

using System.Runtime.InteropServices;

/// <summary>
/// История загрузки: CPU, RAM, GPU — последние 48 замеров.
///
/// Зачем: «CPU 24%» отвечает на вопрос «сколько сейчас», но не «что было минуту назад».
/// История превращает строку в график и показывает всплески, которые одиночный замер
/// проглатывает.
///
/// GPU считается через PDH (`pdh.dll`) напрямую: пакет `System.Diagnostics.PerformanceCounter`
/// тянет зависимость, а PDH есть в самой Windows. Перечисление инстансов GPU — дорогая
/// операция (~2 с), поэтому значение обновляется в фоне не чаще раза в 10 секунд,
/// а UI читает кэш.
/// </summary>
public static class PerfHistory
{
    public const int Capacity = 48;

    private const uint PdhFmtDouble = 0x00000200;

    private static readonly object _lock = new();
    private static readonly double[] _cpu = new double[Capacity];
    private static readonly double[] _ram = new double[Capacity];
    private static readonly double[] _gpu = new double[Capacity];

    private static int _count;
    private static DateTime _gpuUpdated = DateTime.MinValue;
    private static double _gpuCached;
    private static bool _gpuAvailable;
    private static bool _gpuBusy;

    public static double GpuPercent { get { lock (_lock) return _gpuCached; } }

    public static bool GpuAvailable { get { lock (_lock) return _gpuAvailable; } }

    /// <summary>Добавить замер (проценты 0..100). Вызывается раз в секунду снаружи.</summary>
    public static void Push(double cpuPercent, double ramPercent)
    {
        lock (_lock)
        {
            for (int i = 0; i < Capacity - 1; i++)
            {
                _cpu[i] = _cpu[i + 1];
                _ram[i] = _ram[i + 1];
                _gpu[i] = _gpu[i + 1];
            }

            _cpu[^1] = Math.Clamp(cpuPercent, 0, 100);
            _ram[^1] = Math.Clamp(ramPercent, 0, 100);
            _gpu[^1] = Math.Clamp(_gpuCached, 0, 100);

            if (_count < Capacity) _count++;
        }

        StartGpuRefresh();
    }

    public static double[] Cpu() => Snapshot(_cpu);
    public static double[] Ram() => Snapshot(_ram);
    public static double[] Gpu() => Snapshot(_gpu);

    private static double[] Snapshot(double[] source)
    {
        lock (_lock)
        {
            // Незаполненную часть оставляем нулями: график начинается слева, а не
            // «притворяется», что история была всегда.
            var result = new double[Capacity];
            int offset = Capacity - _count;
            Array.Copy(source, Capacity - _count, result, offset, _count);
            return result;
        }
    }

    private static void StartGpuRefresh()
    {
        lock (_lock)
        {
            if (_gpuBusy) return;
            if ((DateTime.UtcNow - _gpuUpdated) < TimeSpan.FromSeconds(10)) return;
            _gpuBusy = true;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var (value, available) = ReadGpuPercent();

                lock (_lock)
                {
                    _gpuAvailable = available;
                    _gpuCached = Math.Clamp(value, 0, 100);
                    _gpu[^1] = _gpuCached;
                    _gpuUpdated = DateTime.UtcNow;
                }
            }
            catch
            {
                lock (_lock)
                {
                    _gpuAvailable = false;
                    _gpuUpdated = DateTime.UtcNow;
                }
            }
            finally
            {
                lock (_lock) _gpuBusy = false;
            }
        });
    }

    /// <summary>Прочитать загрузку 3D-движка GPU через PDH. Возвращает (процент, доступно ли).</summary>
    private static (double Value, bool Available) ReadGpuPercent()
    {
        IntPtr query = IntPtr.Zero;
        IntPtr counter = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) return (0, false);

            // Английское имя счётчика — не зависит от локали Windows.
            if (PdhAddEnglishCounterW(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out counter) != 0)
                return (0, false);

            // Путь GPU-счётчиков требует двух замеров подряд.
            if (PdhCollectQueryData(query) != 0) return (0, false);
            Thread.Sleep(350);
            if (PdhCollectQueryData(query) != 0) return (0, false);

            uint size = 0;
            uint count = 0;
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, IntPtr.Zero) != 0
                && size == 0)
                return (0, false);

            buffer = Marshal.AllocHGlobal((int)size);
            if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, out count, buffer) != 0)
                return (0, false);

            double total = 0;
            bool any = false;
            int itemSize = Marshal.SizeOf<Item>();

            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<Item>(IntPtr.Add(buffer, (int)i * itemSize));
                if (item.Name == IntPtr.Zero) continue;

                string name = Marshal.PtrToStringUni(item.Name) ?? string.Empty;

                // Сумма по всем движкам завышает загрузку: берём только 3D.
                if (!name.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)) continue;

                total += item.Value;
                any = true;
            }

            return (total, any);
        }
        catch
        {
            return (0, false);
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (query != IntPtr.Zero) PdhCloseQuery(query);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Item
    {
        public IntPtr Name;
        public uint Status;
        public uint Padding;
        public double Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint PdhGetFormattedCounterArrayW(
        IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    private static extern uint PdhCloseQuery(IntPtr query);
}
