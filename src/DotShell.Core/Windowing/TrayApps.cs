namespace DotShell.Core.Windowing;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Приложения, которые держат значок в области уведомлений. Два источника, в порядке доверия.
///
/// 1. UI Automation по панели задач — то, что Windows действительно показывает человеку.
///    Видимые значки приложений лежат в дереве `Shell_TrayWnd`, скрытые — в отдельном окне
///    `TopLevelWindowForOverflowXamlIsland` (XAML-остров, Win11 24H2+).
///    Измерено на 26300: видимых значков приложений в дереве нет вовсе (у всех 12 значков
///    выключен promoted), а дерево переполнения пустое, пока флайаут закрыт: 0 кнопок
///    закрыто, 12 открыто. Поэтому скрытые значки запоминаются на сессию — иначе виджет
///    видел бы их только при открытом флайауте.
///
/// 2. Перебор невидимых окон-приёмников по классам — прежнее поведение и честная деградация,
///    когда UIA ничего не дал (например, до первого открытия переполнения). Классы этих
///    окон говорящие: `Electron_NotifyIconHostWindow`, `Chrome_StatusTrayWindow`,
///    `WPETrayWindow`, `SystemTray_Main`. По ним и находим владельца значка (PID).
///
/// Сам UIA читается в WPF-проекте (см. <see cref="UiReader"/>): UIAutomationClient входит
/// в WindowsDesktop, а ядро намеренно не ссылается на UI-фреймворк, чтобы собираться без него.
/// </summary>
public static class TrayApps
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    /// <summary>Класс главной панели задач и её копий на других мониторах.</summary>
    private const string TaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>
    /// Окно XAML-острова переполнения: существует всегда (может быть невидимым),
    /// а кнопки-значки появляются в его UIA-дереве только когда флайаут открыт.
    /// </summary>
    private const string OverflowIslandClass = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>Обход UIA стоит миллисекунды; панель задач за 15 секунд не меняется.</summary>
    private const int UiTtlMs = 15_000;

    /// <summary>Опознание значков дороже UIA (процессы и реестр) — раз в минуту достаточно.</summary>
    private const int IdentityTtlMs = 60_000;

    /// <summary>Список процессов тянуть тоже недёшево; за 5 минут он меняется незначительно.</summary>
    private const int ProcessTtlMs = 300_000;

    private static readonly object Sync = new();
    private static readonly object ProcessSync = new();

    private static long _uiStamp = -UiTtlMs;
    private static TrayUiSnapshot _uiLast = new();
    private static bool _uiReading;

    private static long _processStamp = -ProcessTtlMs;
    private static List<ProcessInfo> _processes = new();

    /// <summary>Скрытые значки, которые UIA показал раньше, а сейчас не видит (флайаут закрыт).</summary>
    private static readonly Dictionary<string, string> Remembered = new(StringComparer.OrdinalIgnoreCase);

    private static long _identityStamp = -IdentityTtlMs;
    private static readonly Dictionary<string, Resolved?> IdentityCache = new(StringComparer.OrdinalIgnoreCase);

    private static long _registryStamp = -IdentityTtlMs;
    private static Dictionary<string, string> _registryTooltips = new(StringComparer.OrdinalIgnoreCase);

    public sealed class TrayApp
    {
        public string ProcessName { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string? IconPath { get; init; }
        public int ProcessId { get; init; }
        public IntPtr MainWindow { get; init; }
        public string OwnerClass { get; init; } = string.Empty;

        /// <summary>Настоящая подсказка значка, если UIA её отдал — то, что человек видит при наведении.</summary>
        public string? Tooltip { get; set; }

        /// <summary>Имя кнопки в дереве UI Automation — по нему ищется элемент для Invoke.</summary>
        public string? UiName { get; set; }

        /// <summary>Значок лежит в переполнении: в видимой части панели задач его нет.</summary>
        public bool InOverflow { get; set; }
    }

    /// <summary>Значок, который UI Automation показывает на панели задач.</summary>
    public sealed class TrayUiIcon
    {
        /// <summary>Имя кнопки как его отдаёт Windows (обычно подсказка, может быть многострочным).</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Значок в переполнении, а не в видимой части панели.</summary>
        public bool InOverflow { get; init; }
    }

    /// <summary>Одно чтение панели задач через UIA.</summary>
    public sealed class TrayUiSnapshot
    {
        public IReadOnlyList<TrayUiIcon> Icons { get; init; } = Array.Empty<TrayUiIcon>();

        /// <summary>true — дерево переполнения было непустым, значит список значков полный на этот момент.</summary>
        public bool OverflowOpen { get; init; }
    }

    /// <summary>
    /// Чтение значков панели задач через UI Automation. Ставится WPF-проектом;
    /// null — UIA недоступен, работает только перебор окон.
    /// </summary>
    public static Func<TrayUiSnapshot>? UiReader { get; set; }

    /// <summary>
    /// Нажать значок по имени через InvokePattern — ровно то, что сделал бы человек.
    /// false — элемента сейчас нет (например, флайаут переполнения закрыт).
    /// </summary>
    public static Func<string, bool>? UiInvoker { get; set; }

    /// <summary>Что показывать и что прятать. Хранится в настройках.</summary>
    public sealed class Options
    {
        /// <summary>Прятать служебные процессы (прокси, webhelper и подобные).</summary>
        public bool HideHelpers { get; set; } = true;

        /// <summary>Имена процессов, которые человек скрыл вручную.</summary>
        public HashSet<string> Hidden { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Закреплённые процессы: видны, даже когда не запущены.</summary>
        public HashSet<string> Pinned { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Служебные процессы: прокси Telegram, webhelper Steam, разные «runtime».
    /// Они держат значок, но человеку бесполезны — прячем по умолчанию.
    /// </summary>
    public static bool LooksLikeHelper(string processName)
    {
        var name = processName.ToLowerInvariant();

        return name.Contains("proxy")
            || name.Contains("webhelper")
            || name.Contains("helper")
            || name.Contains("runtimebroker")
            || name.StartsWith("steamweb", StringComparison.Ordinal)
            || name.Contains("crashpad");
    }

    /// <summary>
    /// Служебные значки самой Windows: часы, громкость, сеть, переключатель раскладки.
    /// В виджете трея нужны приложения, а не сама оболочка — так было и до UIA,
    /// где системные окна отбрасывались. Список имён совпадает с тем, что отдаёт UIA.
    /// </summary>
    public static bool IsShellTrayName(string name)
    {
        var text = name.TrimStart();
        if (text.Length == 0) return true;

        return text.StartsWith("Показать скрытые значки", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Показать рабочий стол", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Индикатор ввода", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Сеть ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Том ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Часы ", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Батарея", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Дисплей", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Устройства Bluetooth", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Безопасность Windows", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Show hidden icons", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Show desktop", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Input indicator", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("Volume", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Хендлы панелей задач (включая вторые мониторы) — с них начинается обход UIA.</summary>
    public static IReadOnlyList<IntPtr> TaskbarHandles()
    {
        var result = new List<IntPtr>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                var cls = ClassNameOf(hwnd);
                if (cls == TaskbarClass || cls == SecondaryTaskbarClass) result.Add(hwnd);
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        return result;
    }

    /// <summary>Хендл окна XAML-острова переполнения; IntPtr.Zero — если окна нет.</summary>
    public static IntPtr OverflowIslandHandle()
    {
        var found = IntPtr.Zero;
        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (ClassNameOf(hwnd) != OverflowIslandClass) return true;
                found = hwnd;
                return false;
            }, IntPtr.Zero);
        }
        catch { }

        return found;
    }

    /// <summary>Собрать список приложений с трей-значком (по одному на приложение).</summary>
    public static List<TrayApp> Scan(Options? options = null)
    {
        var fallback = ScanWindows(options);
        var ui = ReadUi();

        // UIA ничего не дал — работаем ровно как раньше, перебором окон.
        var merged = ui.Icons.Count == 0 ? fallback : Merge(fallback, ui, options);
        return WithPinned(merged, options);
    }

    /// <summary>
    /// Слить значки из UIA с найденными перебором окон.
    ///
    /// Значок из UIA — главный: у него настоящее имя и работающий Invoke. Перебор нужен,
    /// чтобы у значка были иконка и процесс (для «иначе старого поведения») и чтобы
    /// показать то, чего UIA в фоне не видит до первого открытия переполнения.
    /// </summary>
    private static List<TrayApp> Merge(List<TrayApp> fallback, TrayUiSnapshot ui, Options? options)
    {
        var used = new HashSet<TrayApp>();
        var extra = new List<TrayApp>();

        foreach (var icon in ui.Icons)
        {
            var name = icon.Name;
            if (string.IsNullOrWhiteSpace(name) || IsShellTrayName(name)) continue;

            var match = fallback.FirstOrDefault(app => !used.Contains(app) && SameApp(name, app));
            if (match is not null)
            {
                used.Add(match);
                match.Tooltip ??= name;
                match.UiName = name;
                match.InOverflow = icon.InOverflow;
                continue;
            }

            if (extra.Any(app => SameApp(name, app))) continue;

            var resolved = ResolveUiIdentity(name);
            if (resolved is null) continue;

            // Свой значок виджету не нужен: он и есть это приложение.
            if (resolved.Value.ProcessId == Environment.ProcessId) continue;
            if (resolved.Value.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;
            if (FilteredOut(resolved.Value.ProcessName, options)) continue;

            extra.Add(new TrayApp
            {
                ProcessId = resolved.Value.ProcessId,
                ProcessName = resolved.Value.ProcessName,
                DisplayName = resolved.Value.DisplayName,
                IconPath = resolved.Value.IconPath,
                MainWindow = resolved.Value.MainWindow,
                OwnerClass = "uia",
                Tooltip = name,
                UiName = name,
                InOverflow = icon.InOverflow,
            });
        }

        var result = new List<TrayApp>(fallback);
        result.AddRange(extra);
        return result;
    }

    /// <summary>Закреплённые показываем всегда: программа закрыта — плитка остаётся приглушённой.</summary>
    private static List<TrayApp> WithPinned(List<TrayApp> result, Options? options)
    {
        if (options is not null)
        {
            foreach (var pin in options.Pinned)
            {
                if (result.Any(a => string.Equals(a.IconPath, pin, StringComparison.OrdinalIgnoreCase))) continue;

                var name = Path.GetFileNameWithoutExtension(pin);
                result.Add(new TrayApp
                {
                    ProcessId = 0,
                    ProcessName = name,
                    DisplayName = name,
                    IconPath = pin,
                    MainWindow = IntPtr.Zero,
                    OwnerClass = "pinned",
                });
            }
        }

        return result.OrderBy(app => app.ProcessName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>«Человек это скрыл» — правило одинаковое для обоих источников.</summary>
    private static bool FilteredOut(string processName, Options? options)
    {
        if (options is null || string.IsNullOrEmpty(processName)) return false;
        if (options.HideHelpers && LooksLikeHelper(processName)) return true;
        return options.Hidden.Contains(processName);
    }

    /// <summary>
    /// Перебор невидимых окон-приёмников. Прежний источник значков; теперь — деградация,
    /// когда UIA молчит. Поведение намеренно не менялось: тот же фильтр служебных,
    /// та же склейка по исполняемому файлу.
    /// </summary>
    private static List<TrayApp> ScanWindows(Options? options)
    {
        var found = new List<(uint pid, string cls)>();

        try
        {
            EnumWindows((hwnd, _) =>
            {
                if (IsWindowVisible(hwnd)) return true;

                var name = ClassNameOf(hwnd);
                if (!LooksLikeTray(name)) return true;

                GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == (uint)Environment.ProcessId) return true;

                found.Add((pid, name));
                return true;
            }, IntPtr.Zero);
        }
        catch { }

        var result = new List<TrayApp>();

        // Telegram, например, держит два окна-приёмника (своё и TgWsProxy),
        // поэтому склеиваем по исполняемому файлу, а не по PID.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (pid, cls) in found)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);

                // Служебные оболочки не показываем: часы, батарея — это сама Windows.
                if (process.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase)) continue;

                string? path = null;
                try { path = process.MainModule?.FileName; } catch { /* чужой разряд/права */ }

                var key = path ?? process.ProcessName;
                if (!seen.Add(key)) continue;

                if (options is not null)
                {
                    if (options.HideHelpers && LooksLikeHelper(process.ProcessName)) continue;
                    if (options.Hidden.Contains(process.ProcessName)) continue;
                }

                result.Add(new TrayApp
                {
                    ProcessId = (int)pid,
                    ProcessName = process.ProcessName,
                    DisplayName = process.MainWindowTitle.Length > 0
                        ? process.MainWindowTitle
                        : process.ProcessName,
                    IconPath = path,
                    MainWindow = process.MainWindowHandle,
                    OwnerClass = cls,
                });
            }
            catch { /* процесс уже закрылся между скан-проходами */ }
        }

        return result;
    }

    /// <summary>
    /// Одно чтение UIA с кешем и памятью о скрытых значках. Память нужна потому,
    /// что дерево переполнения пустое при закрытом флайауте: без неё скрытые значки
    /// исчезали бы из виджета сразу после закрытия переполнения.
    ///
    /// Чтение идёт в фоне: UIA по панели задач занимает ~100 мс, а зовут его с UI-потока
    /// каждые 4 секунды — на таком темпе это заметные рывки анимации. Поэтому первый
    /// заход отдаёт то, что уже есть (деградацию), а настоящий список появляется
    /// на следующем обновлении виджета.
    /// </summary>
    private static TrayUiSnapshot ReadUi()
    {
        var reader = UiReader;
        if (reader is null) return new TrayUiSnapshot();

        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (_uiReading || now - _uiStamp < UiTtlMs) return _uiLast;

            _uiReading = true;
            _uiStamp = now;
            _ = Task.Run(() => UiReadLoop(reader));
            return _uiLast;
        }
    }

    private static void UiReadLoop(Func<TrayUiSnapshot> reader)
    {
        TrayUiSnapshot fresh;
        try { fresh = reader(); }
        catch { fresh = new TrayUiSnapshot(); }

        lock (Sync)
        {
            // Полное чтение (переполнение было открыто) — единственный момент, когда виден
            // весь список; тогда память переписываем начисто, иначе только дополняем.
            if (fresh.OverflowOpen) Remembered.Clear();
            foreach (var icon in fresh.Icons)
            {
                if (!icon.InOverflow) continue;
                Remembered[Normalize(icon.Name)] = icon.Name;
            }

            var all = new List<TrayUiIcon>(fresh.Icons);
            var live = new HashSet<string>(fresh.Icons.Select(icon => Normalize(icon.Name)));
            foreach (var kv in Remembered)
            {
                if (live.Contains(kv.Key)) continue;
                all.Add(new TrayUiIcon { Name = kv.Value, InOverflow = true });
            }

            _uiLast = new TrayUiSnapshot { Icons = all, OverflowOpen = fresh.OverflowOpen };
            _uiReading = false;
        }

        // Процессы и реестр читаются дорого; греем их здесь, чтобы в UI-потоке
        // опознание значка было только поиском по готовому списку.
        try
        {
            ProcessSnapshot();
            RegistryTooltipMap();
        }
        catch { /* фоновое тепло не должно ронять виджет */ }
    }

    /// <summary>
    /// Кто владеет значком с такой подсказкой. Путь берём из реестра Windows — там же,
    /// где сама Windows хранит значки; иначе ищем процесс по имени или описанию файла.
    /// Подсказку приложение задаёт произвольно, поэтому сравниваем без регистра и знаков.
    /// </summary>
    private static Resolved? ResolveUiIdentity(string uiName)
    {
        var target = Normalize(uiName);
        if (target.Length == 0) return null;

        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (now - _identityStamp >= IdentityTtlMs)
            {
                IdentityCache.Clear();
                _identityStamp = now;
            }

            if (IdentityCache.TryGetValue(target, out var cached)) return cached;

            var value = ResolveUiIdentityCore(target);
            IdentityCache[target] = value;
            return value;
        }
    }

    private static Resolved? ResolveUiIdentityCore(string target)
    {
        var processes = ProcessSnapshot();

        // 1) Реестр: подсказка значка и путь к exe, как их хранит Windows.
        var exe = RegistryExeByTooltip(target);
        if (!string.IsNullOrEmpty(exe))
        {
            foreach (var process in processes)
            {
                if (string.Equals(process.Path, exe, StringComparison.OrdinalIgnoreCase))
                    return ToResolved(process);
            }
        }

        // 2) По имени процесса или описанию файла: подсказка обычно их повторяет.
        Resolved? best = null;
        var bestScore = 0;

        foreach (var process in processes)
        {
            var score = 0;

            var procKey = Normalize(process.Name);
            if (procKey.Length >= 3 && (target == procKey || target.Contains(procKey) || procKey.Contains(target)))
                score = 60 + Math.Min(procKey.Length, 30);

            var descKey = Normalize(process.Description);
            if (descKey.Length >= 3 && (target == descKey || target.Contains(descKey) || descKey.Contains(target)))
                score = Math.Max(score, 40 + Math.Min(descKey.Length, 30));

            if (score <= bestScore) continue;

            bestScore = score;
            best = ToResolved(process);
        }

        return best;
    }

    /// <summary>
    /// Снимок запущенных процессов. Держим его готовым: опознать значок по имени
    /// процесса без снимка значило бы обходить все процессы на каждое обновление
    /// виджета, а это уже заметная пауза интерфейса.
    /// </summary>
    private static List<ProcessInfo> ProcessSnapshot()
    {
        lock (ProcessSync)
        {
            var now = Environment.TickCount64;
            if (now - _processStamp < ProcessTtlMs) return _processes;

            _processStamp = now;
            var list = new List<ProcessInfo>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var module = process.MainModule;
                    var path = module?.FileName;
                    if (string.IsNullOrEmpty(path)) continue;

                    list.Add(new ProcessInfo
                    {
                        Pid = process.Id,
                        Name = process.ProcessName,
                        Path = path,
                        Description = module?.FileVersionInfo.FileDescription ?? string.Empty,
                        Title = process.MainWindowTitle,
                        MainWindow = process.MainWindowHandle,
                    });
                }
                catch { /* чужой разряд/права — процесс просто не попадёт в список */ }
            }

            _processes = list;
            return list;
        }
    }

    private static Resolved ToResolved(ProcessInfo process) => new()
    {
        ProcessId = process.Pid,
        ProcessName = process.Name,
        DisplayName = process.Title.Length > 0 ? process.Title : process.Name,
        IconPath = process.Path,
        MainWindow = process.MainWindow,
    };

    /// <summary>Подсказка значка -> путь к exe, из HKCU\Control Panel\NotifyIconSettings.</summary>
    private static string? RegistryExeByTooltip(string normalizedTarget)
    {
        var map = RegistryTooltipMap();
        if (map.TryGetValue(normalizedTarget, out var exact)) return exact;

        string? best = null;
        var bestLength = 0;
        foreach (var kv in map)
        {
            if (kv.Key.Length < 3) continue;
            if (!normalizedTarget.Contains(kv.Key, StringComparison.Ordinal)) continue;
            if (kv.Key.Length <= bestLength) continue;
            best = kv.Value;
            bestLength = kv.Key.Length;
        }

        return best;
    }

    private static Dictionary<string, string> RegistryTooltipMap()
    {
        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (now - _registryStamp < IdentityTtlMs) return _registryTooltips;

            _registryStamp = now;
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
                if (root is null) return _registryTooltips = map;

                foreach (var keyName in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(keyName);
                    if (key is null) continue;

                    if (key.GetValue("InitialTooltip") is not string tooltip) continue;
                    if (key.GetValue("ExecutablePath") is not string exe) continue;
                    if (string.IsNullOrWhiteSpace(tooltip) || string.IsNullOrWhiteSpace(exe)) continue;

                    var path = ExpandKnownFolder(exe);
                    if (string.IsNullOrEmpty(path)) continue;

                    var keyText = Normalize(tooltip);
                    if (keyText.Length == 0) continue;
                    map[keyText] = path;
                }
            }
            catch { /* реестр недоступен — останется перебор процессов */ }

            _registryTooltips = map;
            return map;
        }
    }

    /// <summary>
    /// Windows пишет путь как `{GUID известной папки}\относительный\путь.exe`.
    /// SHGetKnownFolderPath переводит GUID в настоящую папку, поэтому таблицу
    /// «Program Files / System32» держать в коде не надо.
    /// </summary>
    private static string? ExpandKnownFolder(string raw)
    {
        if (raw.Length == 0) return null;
        if (raw[0] != '{') return raw;

        var close = raw.IndexOf('}');
        if (close <= 1) return null;
        if (!Guid.TryParse(raw[1..close], out var guid)) return null;

        try
        {
            if (SHGetKnownFolderPath(ref guid, 0, IntPtr.Zero, out var pointer) != 0) return null;
            try
            {
                var folder = Marshal.PtrToStringUni(pointer);
                return string.IsNullOrEmpty(folder) ? null : folder + raw[(close + 1)..];
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
        catch { return null; }
    }

    /// <summary>
    /// Один ли это значок: подсказка из UIA против имени процесса, описания файла
    /// и пути из перебора. Сравниваем хвосты вроде «webhelper»/«64», иначе
    /// «Steam — синхронизация» и `steamwebhelper.exe` выглядят как разные программы.
    /// </summary>
    private static bool SameApp(string uiName, TrayApp app)
    {
        var target = Normalize(uiName);
        if (target.Length < 3) return false;

        foreach (var candidate in new[] { app.ProcessName, app.DisplayName, app.Tooltip, StemOf(app.IconPath) })
        {
            var key = Normalize(candidate);
            if (key.Length < 3) continue;
            if (target == key || target.Contains(key, StringComparison.Ordinal) || key.Contains(target, StringComparison.Ordinal))
                return true;
        }

        var family = FamilyOf(target);
        foreach (var candidate in new[] { app.ProcessName, StemOf(app.IconPath) })
        {
            var key = FamilyOf(Normalize(candidate));
            if (key.Length < 3) continue;
            if (family.Contains(key, StringComparison.Ordinal) || key.Contains(family, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static string FamilyOf(string normalized)
    {
        foreach (var suffix in new[] { "webhelper", "helper", "proxy", "system", "tray", "x64", "64", "32" })
        {
            if (normalized.Length <= suffix.Length + 2) continue;
            if (normalized.EndsWith(suffix, StringComparison.Ordinal))
                return normalized[..^suffix.Length];
        }

        return normalized;
    }

    private static string? StemOf(string? path)
        => string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);

    /// <summary>Буквы и цифры в нижнем регистре: подсказки приходят с пробелами и переводами строк.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);

        return builder.ToString();
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static bool LooksLikeTray(string className)
    {
        if (className.Length == 0) return false;

        return className.Contains("Tray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("StatusTray", StringComparison.OrdinalIgnoreCase)
            || className.Contains("NotifyIcon", StringComparison.OrdinalIgnoreCase)
            || className.Contains("SystemTray", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Показать окно приложения (если оно есть) или запустить его снова.</summary>
    public static void Activate(TrayApp app)
    {
        // Сначала настоящий клик по значку в панели задач: это ровно то,
        // что сделал бы человек мышью, и работает даже без окна у программы.
        if (app.UiName is { Length: > 0 } uiName)
        {
            try
            {
                if (UiInvoker?.Invoke(uiName) == true) return;
            }
            catch { /* элемент мог исчезнуть между чтением и кликом */ }
        }

        try
        {
            if (app.MainWindow != IntPtr.Zero)
            {
                ShowWindow(app.MainWindow, 9);       // SW_RESTORE
                SetForegroundWindow(app.MainWindow);
                return;
            }

            if (!string.IsNullOrEmpty(app.IconPath) && File.Exists(app.IconPath))
                Process.Start(new ProcessStartInfo(app.IconPath) { UseShellExecute = true });
        }
        catch { }
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    /// <summary>Результат опознания значка: процесс, его окно и путь к exe.</summary>
    private readonly struct Resolved
    {
        public int ProcessId { get; init; }
        public string ProcessName { get; init; }
        public string DisplayName { get; init; }
        public string? IconPath { get; init; }
        public IntPtr MainWindow { get; init; }
    }

    /// <summary>Один процесс в снимке: всё, что нужно, чтобы опознать значок и нажать его.</summary>
    private sealed class ProcessInfo
    {
        public int Pid { get; init; }
        public string Name { get; init; } = string.Empty;
        public string? Path { get; init; }
        public string Description { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public IntPtr MainWindow { get; init; }
    }
}
