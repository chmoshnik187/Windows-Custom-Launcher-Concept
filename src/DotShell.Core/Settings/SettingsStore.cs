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

    /// <summary>Актуальная версия схемы настроек. Увеличивать при добавлении миграций.</summary>
    public const int CurrentConfigVersion = 2;

    public static SettingsStore Load()
    {
        var store = new SettingsStore();
        bool loaded = false;
        try
        {
            if (File.Exists(AppSettings.SettingsPath))
            {
                var json = File.ReadAllText(AppSettings.SettingsPath);
                var parsed = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (parsed is not null)
                {
                    store.Current = parsed;
                    loaded = true;
                }
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

        if (loaded)
            store.ApplyMigrations();

        return store;
    }

    /// <summary>
    /// Миграции настроек по ConfigVersion. Перед изменением делается бэкап исходного файла.
    /// Миграции должны быть идемпотентными и безопасно откатываться вручную из .bak.
    /// </summary>
    private void ApplyMigrations()
    {
        try
        {
            int from = Current.ConfigVersion;
            if (from <= 0) from = 1; // старые конфиги без поля
            if (from >= CurrentConfigVersion)
            {
                // Нормализуем поле для старых конфигов, где оно отсутствовало (десериализовалось в 0).
                if (Current.ConfigVersion != CurrentConfigVersion)
                {
                    Current.ConfigVersion = CurrentConfigVersion;
                    Save();
                }
                return;
            }

            BackupForMigration(from);

            // Будущие миграции: if (from < 2) { ... Current.ConfigVersion = 2; }

            Current.ConfigVersion = CurrentConfigVersion;
            Save();
        }
        catch (Exception ex)
        {
            LastError = $"миграция настроек не завершена: {ex.Message}";
        }
    }

    private static void BackupForMigration(int fromVersion)
    {
        try
        {
            if (!File.Exists(AppSettings.SettingsPath)) return;
            var backup = AppSettings.SettingsPath + $".v{fromVersion}.bak";
            File.Copy(AppSettings.SettingsPath, backup, overwrite: true);
        }
        catch
        {
            // Бэкап — best-effort; основная ошибка миграции будет отражена в LastError при необходимости.
        }
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
