namespace DotShell.Core.Theming;

/// <summary>
/// Мета-тема для списка: идентификатор (без расширения), человекочитаемое имя,
/// автор, версия и путь к файлу. Один JSON-файл = одна тема (формат — <see cref="ThemeSpec"/>).
/// </summary>
public sealed record ThemeMeta(string Id, string Name, string Author, int Version, string Path);

public static class ThemeStore
{
    /// <summary>
    /// Каталог тем человека. Там он кладёт свои JSON-файлы; шелл их сканирует
    /// и применяет. Пустой каталог = только встроенные скины.
    /// </summary>
    public static string ThemesDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotShell",
            "themes");

    /// <summary>Все темы из каталога, отсортированы по Id. Пустой список — нет файлов.</summary>
    public static IReadOnlyList<ThemeMeta> List()
    {
        var result = new List<ThemeMeta>();
        if (!Directory.Exists(ThemesDirectory)) return result;

        foreach (var file in Directory.EnumerateFiles(ThemesDirectory, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var spec = ThemeSpec.Load(file);
                result.Add(new ThemeMeta(
                    Path.GetFileNameWithoutExtension(file),
                    spec.Name,
                    spec.Author,
                    spec.Version,
                    file));
            }
            catch
            {
                // Битый файл темы не должен ломать список: пропускаем и продолжаем скан.
            }
        }

        return result;
    }

    public static ThemeSpec Load(string path) => ThemeSpec.Load(path);

    public static bool Save(ThemeSpec spec, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(spec, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower,
                ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
            return true;
        }
        catch
        {
            return false;
        }
    }
}
