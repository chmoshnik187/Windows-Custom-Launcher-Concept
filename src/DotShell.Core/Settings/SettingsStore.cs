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
        catch
        {
            // Битый конфиг не должен мешать программе запуститься — работаем с дефолтами.
            // TODO: сохранить битый файл как settings.json.bak и показать уведомление.
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
            Changed?.Invoke(Current);
        }
        catch
        {
            // TODO: лог
        }
    }
}
