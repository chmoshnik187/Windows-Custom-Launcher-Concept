namespace DotShell.Island.Wpf;

using DotShell.Island.Wpf.Desktop;

using System.IO;
using System.Windows;
using System.Windows.Threading;
using DotShell.Core;

/// <summary>
/// Точка входа. Написана руками вместо auto-generated Main,
/// чтобы контролировать порядок инициализации и перехватывать падения.
/// </summary>
public static class Program
{
    /// <summary>
    /// Вкладка, которую открыть сразу при старте (`--tab=chat`).
    /// Полезно для ярлыка «открыть чат» и для проверки вёрстки без кликов.
    /// </summary>
    public static string? StartupTab { get; private set; }

    /// <summary>--gallery — открыть витрину виджетов при старте (ярлык/проверка).</summary>
    public static bool OpenGallery { get; private set; }

    /// <summary>--keys — открыть справочник клавиш при старте.</summary>
    public static bool OpenKeys { get; private set; }

    /// <summary>--settings: сразу открыть окно настроек.</summary>
    public static bool OpenSettingsOnStart { get; private set; }

    /// <summary>--dashboard: открыть панель-обзор при старте (ярлык, проверка вёрстки).</summary>
    public static bool OpenDashboard { get; private set; }

    /// <summary>--widget=media: открыть виджет при старте (снимки, проверка раскладок).</summary>
    public static string? OpenWidgetType { get; private set; }

    /// <summary>--widget-size=520x112: задать размер открываемого виджета.</summary>
    public static (int W, int H)? WidgetSize { get; private set; }

    /// <summary>--chat-demo: пара демонстрационных реплик в чате (снимки, проверка вёрстки).</summary>
    public static bool ChatDemo { get; private set; }

    /// <summary>--toast-test: отправить проверочный тост и завершиться.</summary>
    public static bool ToastTest { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        // Самопроверка: --selftest. Запускается в CI/перед релизом, ничего не меняет
        // в системе, печатает результат и возвращает код (0 — всё в порядке).
        if (args.Any(a => a.StartsWith("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // Консоль Windows по умолчанию не в UTF-8 — русский текст в отчёте
            // превращается в кашу, а отчёт читают глазами.
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

            var results = new List<(string Name, bool Ok, string Note)>();

            void Check(string name, Func<(bool ok, string note)> action)
            {
                try
                {
                    var (ok, note) = action();
                    results.Add((name, ok, note));
                }
                catch (Exception ex)
                {
                    results.Add((name, false, ex.GetType().Name + ": " + ex.Message));
                }
            }

            Check("версия", () =>
            {
                var text = DotShell.Core.Updates.UpdateChecker.ActualText;
                return (Version.TryParse(text, out _), text);
            });

            Check("настройки: чтение", () =>
            {
                var dir = DotShell.Core.Settings.AppSettings.SettingsDirectory;
                return (System.IO.Directory.Exists(dir), dir);
            });

            Check("акцент", () =>
            {
                var accent = DotShell.Island.Wpf.ColorPalette.Accent;
                return (accent.A > 0, $"#{accent.R:X2}{accent.G:X2}{accent.B:X2}");
            });

            Check("мониторы", () =>
            {
                int count = DotShell.Core.Windowing.Monitors.Count;
                return (count > 0, $"{count} шт.");
            });

            Check("иконки стола", () =>
            {
                var links = DotShell.Island.Wpf.Desktop.DesktopIcons.Shortcuts();
                return (true, $"{links.Count} ярлыков");
            });

            Check("погода: кэш", () =>
            {
                var dir = DotShell.Core.Settings.AppSettings.SettingsDirectory;
                return (System.IO.Directory.Exists(dir), dir);
            });

            Check("обновление: проверка версии", () =>
            {
                var current = DotShell.Core.Updates.UpdateChecker.Current;
                return (current.Major >= 0, current.ToString(3));
            });

            int failed = results.Count(r => !r.Ok);

            var lines = new List<string>();
            foreach (var (name, ok, note) in results)
                lines.Add($"{(ok ? "ok  " : "FAIL")} {name}: {note}");

            lines.Add(failed == 0 ? "SMOKE ok" : $"SMOKE fail ({failed})");

            // Консоль Windows отдаёт OEM-кодировку при перенаправлении, и русский
            // отчёт превращается в кашу. Поэтому пишем UTF-8 в файл, а в консоль —
            // то, что переживёт любую кодировку.
            // В консоль — только ASCII-итог: подробности лежат в UTF-8 файле,
            // а OEM-консоль Windows ломает русские буквы при перенаправлении.
            Console.WriteLine(failed == 0 ? "SMOKE ok" : $"SMOKE fail ({failed})");

            var selfTestPath = args
                .FirstOrDefault(a => a.StartsWith("--selftest=", StringComparison.OrdinalIgnoreCase))
                ?["--selftest=".Length..];

            if (!string.IsNullOrWhiteSpace(selfTestPath))
            {
                try { System.IO.File.WriteAllLines(selfTestPath!, lines, System.Text.Encoding.UTF8); }
                catch { }
            }

            return failed == 0 ? 0 : 1;
        }
        // --launcher: сразу открыть умный поиск (удобно для снимков и проверки).
        if (args.Any(a => a.Equals("--launcher", StringComparison.OrdinalIgnoreCase)))
        {
            LauncherHost.OpenOnce = true;
        }
        // Иконки рабочего стола: --icons-apply / --icons-restore
        // (необязательный --icons-dir=PATH — работать с папкой вместо рабочего стола).
        bool iconsApply = args.Any(a => a.Equals("--icons-apply", StringComparison.OrdinalIgnoreCase));
        bool iconsRestore = args.Any(a => a.Equals("--icons-restore", StringComparison.OrdinalIgnoreCase));

        if (iconsApply || iconsRestore)
        {
            string? dir = null;
            foreach (var arg in args)
            {
                if (!arg.StartsWith("--icons-dir=", StringComparison.OrdinalIgnoreCase)) continue;
                dir = arg["--icons-dir=".Length..].Trim('"');
            }

            var links = DesktopIcons.Shortcuts(dir);

            if (iconsRestore)
            {
                int restored = DesktopIcons.Restore(out var failedRestore);
                Log.Info($"icons: вернул {restored}, не удалось {failedRestore.Count}");
                Console.WriteLine($"restored={restored} failed={failedRestore.Count}");
            }
            else
            {
                int done = DesktopIcons.MakeMonochrome(links, DotShell.Island.Wpf.ColorPalette.Accent, out var failed);
                Log.Info($"icons: перекрасил {done} из {links.Count}, не удалось {failed.Count}");
                Console.WriteLine($"done={done} total={links.Count} failed={failed.Count}");
            }

            return 0;
        }
        // Проверка механизма подмены exe: копируем себя в папку обновления и
        // прогоняем полный цикл (переименование, копирование, перезапуск).
        if (args.Any(a => a.Equals("--self-update-test", StringComparison.OrdinalIgnoreCase)))
        {
            var dir = System.IO.Path.Combine(DotShell.Core.Settings.AppSettings.SettingsDirectory, "update");
            System.IO.Directory.CreateDirectory(dir);

            var me = Environment.ProcessPath!;
            var staged = System.IO.Path.Combine(dir, "DotShell-test.exe");
            System.IO.File.Copy(me, staged, true);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "pending.txt"), staged);

            bool applied = SelfUpdate.TryApplyPending();
            Log.Info($"self-update test: {applied}");
            return applied ? 0 : 3;
        }
        // Проба версии: её запускает самообновление у скачанного файла. Печатаем
        // версию в stdout и выходим — так проверяется, что файл живой и тот, что нужен.
        if (args.Any(a => a.Equals("--version-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(DotShell.Core.Updates.UpdateChecker.ActualText);
            return 0;
        }

        // Отложенное обновление: подменить себя и перезапуститься ДО всей инициализации.
        // `.old` удаляется только после успешного старта — это точка отката.
        if (SelfUpdate.TryApplyPending()) return 0;
        SelfUpdate.CleanupBackup();
        // Проверка тостов не требует острова и не должна упираться в единственный
        // экземпляр: иначе «уже запущено» и никакой проверки.
        if (args.Any(a => a.Equals("--toast-test", StringComparison.OrdinalIgnoreCase)))
        {
            Toast.EnsureRegistered();
            bool toastOk = Toast.Show("DotShell", "Проверка уведомлений: если видишь это справа — тосты работают.");
            Log.Info($"toast self-test: {toastOk}");
            return toastOk ? 0 : 2;
        }

        // Один экземпляр на систему. Вторая копия не может зарегистрировать те же
        // глобальные хоткеи (RegisterHotKey → 1409) и тихо ломает первую: остров
        // «есть», а Ctrl+Shift+Space не работает. Поэтому вторую просто не пускаем.
        using var single = new System.Threading.Mutex(true, "DotShell.Island.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show(
                "DotShell уже запущен — ищи остров сверху экрана или иконку в трее.",
                "DotShell", MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        foreach (var arg in args)
        {
            if (arg.Equals("--gallery", StringComparison.OrdinalIgnoreCase)) { OpenGallery = true; continue; }

            if (arg.Equals("--keys", StringComparison.OrdinalIgnoreCase)) { OpenKeys = true; continue; }

            if (arg.Equals("--settings", StringComparison.OrdinalIgnoreCase)) { OpenSettingsOnStart = true; continue; }

            if (arg.Equals("--dashboard", StringComparison.OrdinalIgnoreCase)) { OpenDashboard = true; continue; }

            if (arg.StartsWith("--widget=", StringComparison.OrdinalIgnoreCase))
            {
                OpenWidgetType = arg["--widget=".Length..].Trim().ToLowerInvariant();
                continue;
            }

            if (arg.StartsWith("--widget-size=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg["--widget-size=".Length..].ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    WidgetSize = (w, h);
                }
                continue;
            }

            if (arg.Equals("--chat-demo", StringComparison.OrdinalIgnoreCase)) { ChatDemo = true; continue; }

            // Проверка уведомлений: отправить тост сразу и выйти. Нужна, чтобы
            // убедиться, что AUMID зарегистрирован и Windows тосты не блокирует.
            if (arg.Equals("--toast-test", StringComparison.OrdinalIgnoreCase)) { ToastTest = true; continue; }

            // tryb оформления задаёт установщик: --style=full включает системный
            // стиль один раз, --style=island выключает. Дальше живёт в настройках.
            if (arg.StartsWith("--style=", StringComparison.OrdinalIgnoreCase))
            {
                var mode = arg["--style=".Length..];
                bool full = mode.Equals("full", StringComparison.OrdinalIgnoreCase);

                DotShell.Core.Settings.AppSettings.SetSystemStyle(full);
                continue;
            }

            // --fake-version=0.1.0: притвориться старой версией, чтобы увидеть,
            // как выглядит уведомление об обновлении, не откатывая установку.
            if (arg.StartsWith("--fake-version=", StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(arg["--fake-version=".Length..], out var fake))
            {
                DotShell.Core.Updates.UpdateChecker.SimulatedVersion = fake;
                continue;
            }


            if (arg.StartsWith("--tab=", StringComparison.OrdinalIgnoreCase))
                StartupTab = arg["--tab=".Length..].Trim().ToLowerInvariant();
        }

        // Любая необработанная ошибка пишется в лог, а не роняет программу молча.
        // Для софта, который висит 24/7, это критично: иначе никогда не узнаешь,
        // почему остров пропал через три дня.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception ?? new Exception("unknown"));

        var app = new DotShellApp();

        // Тёмное меню — на уровне приложения: иначе контекстные меню
        // виджетов, групп и лаунчера выпадали системными, светло-голубыми.
        try
        {
            app.Resources.MergedDictionaries.Add(
                (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                    new Uri("MenuStyles.xaml", UriKind.Relative)));
        }
        catch (Exception ex) { Log.Info($"menu styles failed: {ex.Message}"); }

        // Диагностика: неявный стиль меню должен находиться в ресурсах приложения,
        // иначе меню любого окна выпадет системным.
        Log.Info($"menu style available: {app.TryFindResource(typeof(System.Windows.Controls.ContextMenu)) is System.Windows.Style}");
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception);
            e.Handled = true; // одна ошибка в UI не должна убивать весь шелл
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Fatal(e.Exception);
            e.SetObserved();
        };


        // --toast-test: проверка уведомлений без запуска острова.
        if (ToastTest)
        {
            Toast.EnsureRegistered();
            bool ok = Toast.Show("DotShell", "Проверка уведомлений: если видишь это справа — тосты работают.");
            Log.Info($"toast self-test: {ok}");
            return ok ? 0 : 2;
        }

        try
        {
            return app.Run();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex);
            MessageBox.Show(
                "DotShell не смог запуститься.\n\n" + ex.Message +
                "\n\nПодробности: %LOCALAPPDATA%\\DotShell\\crash.log",
                "DotShell", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }
    }
}

/// <summary>
/// WPF-приложение. Ресурсы (цвета, стили) задаются кодом в ColorPalette.Install,
/// а не XAML-словарём: так их можно менять на лету из ThemeBus,
/// и это единственный способ, которым DynamicResource гарантированно подхватывает замену.
/// </summary>
public sealed class DotShellApp : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Окно острова у нас без рамки и не появляется в таскбаре, поэтому
        // "все окна закрыты" не должно означать "выходим".
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            await ShellServices.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex);
            MessageBox.Show(
                "Ядро DotShell не запустилось.\n\n" + ex.Message +
                "\n\nПодробности: %LOCALAPPDATA%\\DotShell\\crash.log",
                "DotShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ColorPalette.Install(Resources);
        ColorPalette.Bind(ShellServices.Instance.Theme);
        DotStyle.Bind(ShellServices.Instance.Theme);

        var window = new IslandWindow();
        MainWindow = window;
        window.Show();
        window.OpenStartupTab();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _ = ShellServices.Instance?.DisposeAsync();
        base.OnExit(e);
    }
}

/// <summary>Простейший логгер в файл. Позже заменяется на Serilog — интерфейс тот же.</summary>
public static class Log
{
    private static readonly object Lock = new();

    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotShell");

    public static string FilePath => Path.Combine(Directory, "crash.log");

    public static void Info(string message) => Write("INFO ", message);
    public static void Fatal(Exception ex) => Write("FATAL", ex.ToString());

    private static void Write(string level, string message)
    {
        try
        {
            lock (Lock)
            {
                System.IO.Directory.CreateDirectory(Directory);
                File.AppendAllText(FilePath, $"[{DateTimeOffset.Now:O}] {level} {message}\n");
            }
        }
        catch { }
    }
}

