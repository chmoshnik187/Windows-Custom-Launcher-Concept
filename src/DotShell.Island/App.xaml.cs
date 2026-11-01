namespace DotShell.Island;

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

/// <summary>
/// Класс приложения. Создаётся один раз на запуск процесса.
/// </summary>
public partial class App : Application
{
    private IslandWindow? _window;

    public App()
    {
        InitializeComponent();

        // Любое необработанное исключение пишем в файл, а не роняемся молча.
        // Для софта, который живёт 24/7, это критично: иначе ты никогда не узнаешь,
        // почему остров исчез через три дня после запуска.
        UnhandledException += (_, e) =>
        {
            Log.Fatal(e.Exception);
            e.Handled = true; // одна ошибка в UI не должна убивать весь шелл
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            // Поднимаем ядро: настройки, шину темы, буфер обмена, медиа-мост, наблюдателей.
            await ShellServices.InitializeAsync();
        }
        catch (Exception ex)
        {
            // Окна ещё нет, поэтому показываем обычный Win32 message box — он всегда работает.
            Log.Fatal(ex);
            MessageBox(IntPtr.Zero,
                "Ядро DotShell не запустилось. Подробности в %LOCALAPPDATA%\\DotShell\\crash.log\n\n" + ex.Message,
                "DotShell", 0x10 /* MB_ICONERROR */);
            Exit();
            return;
        }

        _window = new IslandWindow();
        _window.Activate();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

/// <summary>Простейший логгер в файл. Позже заменяется на Serilog — интерфейс тот же.</summary>
public static class Log
{
    private static readonly object Lock = new();
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotShell");
    public static string FilePath => Path.Combine(Directory, "crash.log");

    public static void Info(string message) => Write("INFO", message);
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
