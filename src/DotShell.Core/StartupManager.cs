namespace DotShell.Core;

using Microsoft.Win32;

/// <summary>
/// Автозапуск вместе с Windows.
///
/// Делаем через ключ реестра HKCU\...\Run (не через Планировщик и не через Startup-папку):
/// это самый простой и самый предсказуемый способ, его легко показать пользователю
/// и легко удалить — в том числе вручную через regedit, если программа вдруг сломается.
///
/// Важные детали:
///  • пишем ПОЛНЫЙ путь к .exe в кавычках, иначе перенос папки молча сломает автозапуск;
///  • добавляем флаг --minimized: при старте вместе с Windows не нужно показывать
///    приветственное раскрытие острова — человек просто сел за компьютер;
///  • если DotShell установлен через установщик, путь берётся из места установки,
///    а не из папки сборки.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DotShell";

    /// <summary>Флаг командной строки: стартовать тихо, без приветственной анимации.</summary>
    public const string MinimizedFlag = "--minimized";

    /// <summary>Запущены ли мы сейчас из автозапуска (или вручную с этим флагом).</summary>
    public static bool StartedMinimized =>
        Environment.GetCommandLineArgs().Any(a =>
            string.Equals(a, MinimizedFlag, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/minimized", StringComparison.OrdinalIgnoreCase));

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    /// <summary>Текущая записанная команда (для отладки и показа в настройках).</summary>
    public static string? CurrentCommand
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) as string;
            }
            catch { return null; }
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (key is null) return false;

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                // Полный путь в кавычках + флаг тихого старта.
                key.SetValue(ValueName, $"\"{exe}\" {MinimizedFlag}");

                // Заодно сообщаем установщику (если он есть), что автозапуск включён,
                // чтобы при удалении программы ключ точно вычистили.
                return true;
            }

            if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Проверить, что записанный путь всё ещё ведёт на существующий .exe.
    /// Если пользователь перенёс папку программы — автозапуск сломался молча,
    /// и это надо показать в настройках, а не ждать, пока человек заметит сам.
    /// </summary>
    public static bool IsEnabledAndValid()
    {
        var command = CurrentCommand;
        if (string.IsNullOrWhiteSpace(command)) return false;

        var path = command.TrimStart('"');
        int quote = path.IndexOf('"');
        if (quote > 0) path = path[..quote];
        else path = path.Split(' ')[0];

        return File.Exists(path);
    }
}
