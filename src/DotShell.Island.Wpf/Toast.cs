namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Windows.UI.Notifications;

/// <summary>
/// Настоящие уведомления Windows (тосты) вместо пузыря над треем.
///
/// Пузырь живёт у иконки и легко теряется: если трей свёрнут в стрелку — его вообще
/// не видно. Тосты показываются справа на экране, попадают в центр уведомлений и
/// остаются там, пока их не уберут.
///
/// Условие Windows: у приложения должен быть **AppUserModelID**, зарегистрированный
/// в системе, иначе `CreateToastNotifier` бросает исключение. Для программы без
/// установщика это делается двумя шагами: ключ в реестре и ярлык в «Пуске» с тем же
/// идентификатором. Оба шага обратимы и живут в профиле пользователя.
/// </summary>
public static class Toast
{
    /// <summary>Идентификатор приложения в системе. Один на всё приложение.</summary>
    public const string AppId = "DotShell.Island";

    private static bool _registered;
    private static bool _broken;

    /// <summary>Галочка в настройках: выключено — напоминания идут только пузырём трея.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Есть ли смысл пробовать: если тосты отказали, молча откатываемся на пузырь.</summary>
    public static bool Available => !_broken;

    public static void EnsureRegistered()
    {
        if (_registered) return;

        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;

            using (var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
            {
                key.SetValue("DisplayName", "DotShell");
                if (exe.Length > 0) key.SetValue("IconUri", exe);
                key.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
            }

            // Ярлык в «Пуске» с AUMID — без него Windows 10/11 может не показать тост.
            EnsureShortcut(exe);
            _registered = true;
        }
        catch (Exception ex)
        {
            Log.Info($"toast register failed: {ex.Message}");
        }
    }

    /// <summary>Показать тост. Возвращает false, если Windows отказала (тогда — пузырь).</summary>
    public static bool Show(string title, string message, string? tag = null)
    {
        if (!Enabled) return false;

        // Сначала своё окно: оно в стиле острова и не зависит от разрешений на
        // уведомления Windows. Системный тост — запасной путь, если хаб выключен.
        if (NotificationHub.Enabled && NotificationHub.Show(title, message)) return true;

        if (_broken) return false;
        EnsureRegistered();

        try
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(
                "<toast duration=\"short\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(title)}</text>" +
                $"<text>{Escape(message)}</text>" +
                "</binding></visual>" +
                "</toast>");

            var notification = new ToastNotification(xml);
            if (!string.IsNullOrEmpty(tag)) notification.Tag = tag[..Math.Min(16, tag.Length)];

            // Клик по тосту возвращает остров на экран.
            notification.Activated += (_, _) => ActivateIsland();

            ToastNotificationManager.CreateToastNotifier(AppId).Show(notification);
            Log.Info($"toast: {title} — {message}");
            return true;
        }
        catch (Exception ex)
        {
            // Один отказ — и больше не пытаемся: у человека всё равно останется пузырь.
            _broken = true;
            Log.Info($"toast failed ({ex.GetType().Name}): {ex.Message}");
            return false;
        }
    }

    private static void ActivateIsland()
    {
        try
        {
            var process = Process.GetCurrentProcess();
            var main = process.MainWindowHandle;
            if (main != IntPtr.Zero) ShowWindow(main, 9);
        }
        catch { }
    }

    private static void EnsureShortcut(string exe)
    {
        try
        {
            if (exe.Length == 0) return;

            var startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs\DotShell.lnk");

            if (File.Exists(startMenu)) return;

            // Ярлык создаём через COM-объект WScript.Shell: он умеет ставить
            // System.AppUserModel.ID, чего обычный File.WriteAllBytes не может.
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;

            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(startMenu);
            link.TargetPath = exe;
            link.IconLocation = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty;
            link.Save();
        }
        catch (Exception ex) { Log.Info($"toast shortcut failed: {ex.Message}"); }
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
