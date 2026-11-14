namespace DotShell.Core.Settings;

using System.Text.Json;

/// <summary>
/// Загрузка и сохранение настроек.
/// Пишем "атомарно": сначала во временный файл, потом подменяем им целевой.
/// Иначе выключение питания в момент записи оставит тебя с битым JSON.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public AppSettings Current { get; private set; } = new();

    /// <summary>
    /// Последняя ошибка загрузки/сохранения. UI пишет её в crash.log: молча
    /// потерять настройки человек не должен.
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>Срабатывает после каждого сохранения — на него подписан ThemeBus.</summary>
    public event Action<AppSettings>? Changed;

    public static SettingsStore Load()
    {
        var store = new SettingsStore();
        try
        {
            if (File.Exists(AppSettings.SettingsPath))
            {
                var json = File.ReadAllText(AppSettings.SettingsPath);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (parsed is not null) store.Current = parsed;
            }
        }
        catch (Exception ex)
        {
            // Битый конфиг не должен мешать программе запуститься — работаем с дефолтами.
            // Но и молча стирать его нельзя: сохраняем копию, чтобы человек мог
            // вернуть свои настройки руками.
            string backup = AppSettings.SettingsPath + ".bak";
            try
            {
                if (File.Exists(AppSettings.SettingsPath))
                    File.Copy(AppSettings.SettingsPath, backup, overwrite: true);
                store.LastError = $"settings.json не прочитан ({ex.Message}); копия: {backup}";
            }
            catch (Exception copyEx)
            {
                store.LastError = $"settings.json не прочитан ({ex.Message}); копию сделать не удалось ({copyEx.Message})";
            }
        }
        return store;
    }

    /// <summary>Изменить настройки и сразу сохранить.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            var tmp = AppSettings.SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, AppSettings.SettingsPath, overwrite: true);
            LastError = null;
            Changed?.Invoke(Current);
        }
        catch (Exception ex)
        {
            LastError = $"настройки не сохранились: {ex.Message}";
        }
    }
}
