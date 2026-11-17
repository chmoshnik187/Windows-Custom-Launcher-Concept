namespace DotShell.Island.Wpf.Taskbar;

using System.IO;
using System.Text.Json;
using DotShell.Core;

/// <summary>
/// Мост между WPF-приложением DotShell и нативным модулем панели задач
/// (Windhawk-мод): состояние острова в файле, команды — тоже файлом.
///
/// Зачем файлы, а не COM: мод живёт в explorer.exe на другом уровне привилегий
/// и не должен зависеть от времени жизни нашего процесса. Файл — самый
/// неубиваемый канал: переживает падение любой стороны, легко читается и
/// отлаживается блокнотом.
///
///   состояние   %LOCALAPPDATA%\DotShell\taskbar-island.json  (пишем мы, читает мод)
///   heartbeat   %LOCALAPPDATA%\DotShell\taskbar-island.heartbeat (пишет мод, читаем мы)
///   команда     %LOCALAPPDATA%\DotShell\taskbar-island-command.json (пишет мод, читаем мы)
/// </summary>
internal static class TaskbarIslandBridge
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotShell");

    public static string StatePath => Path.Combine(Dir, "taskbar-island.json");
    public static string HeartbeatPath => Path.Combine(Dir, "taskbar-island.heartbeat");
    public static string CommandPath => Path.Combine(Dir, "taskbar-island-command.json");

    private static FileSystemWatcher? _watcher;
    private static bool _started;

    /// <summary>Мод прислал команду (toggle-panel, open-settings, ...).</summary>
    public static event Action<string>? CommandReceived;

    /// <summary>Мод жив и рисует остров внутри панели задач.</summary>
    public static bool ModActive
    {
        get
        {
            try
            {
                var info = new FileInfo(HeartbeatPath);
                return info.Exists && DateTime.Now - info.LastWriteTime < TimeSpan.FromSeconds(3);
            }
            catch
            {
                return false;
            }
        }
    }

    public static void Start()
    {
        if (_started) return;
        _started = true;

        try
        {
            Directory.CreateDirectory(Dir);

            // Подчищаем старые файлы, чтобы после сбоя не сработала чужая команда.
            TryDelete(CommandPath);

            _watcher = new FileSystemWatcher(Dir, "taskbar-island-command.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };

            _watcher.Created += (_, _) => ReadCommand();
            _watcher.Changed += (_, _) => ReadCommand();
            _watcher.Renamed += (_, _) => ReadCommand();
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar bridge: watcher failed: {ex.Message}");
        }
    }

    private static void ReadCommand()
    {
        try
        {
            if (!File.Exists(CommandPath)) return;

            string text;
            try { text = File.ReadAllText(CommandPath); }
            catch (IOException) { return; }

            TryDelete(CommandPath);

            if (string.IsNullOrWhiteSpace(text)) return;

            string action = "toggle-panel";
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String)
                    action = a.GetString() ?? action;
            }
            catch { }

            Log.Info($"taskbar bridge: command '{action}'");
            CommandReceived?.Invoke(action);
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar bridge: command read failed: {ex.Message}");
        }
    }

    /// <summary>Записать состояние для нативного острова в панели.</summary>
    public static void WriteState(Action<Utf8JsonWriter> write)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string temp = StatePath + ".tmp";

            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }

            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Info($"taskbar bridge: state write failed: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }
}
