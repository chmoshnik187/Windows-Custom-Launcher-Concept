namespace DotShell.Core.Theming;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Формат темы. Один JSON-файл = вся тема.
///
/// Это ответ на "кастомизация как в линукс-дистрибутиве, но чтобы не запарно":
/// пользователю не надо крутить 40 слайдеров — он кидает файл темы в папку
/// и выбирает его в списке. А кто хочет крутить слайдеры — редактирует тот же файл.
///
/// Позже тема станет .zip (сюда добавятся глифы, иконки и сцена обоев),
/// но структура полей уже заложена, чтобы ничего не переделывать.
/// </summary>
public sealed class ThemeSpec
{
    public string Name { get; set; } = "Untitled";
    public string Author { get; set; } = string.Empty;
    public int Version { get; set; } = 1;

    public ThemeColors Colors { get; set; } = new();
    public ThemeDot Dot { get; set; } = new();
    public ThemeMotion Motion { get; set; } = new();
    public ThemeWidgets Widgets { get; set; } = new();

    public static ThemeSpec Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<ThemeSpec>(json, Options) ?? new ThemeSpec();
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public sealed class ThemeColors
{
    /// <summary>Основной акцент. Если null — берётся из обоев (авто-цвет).</summary>
    public string? Accent { get; set; }

    public string Background { get; set; } = "#000000";
    public string Surface { get; set; } = "#121214";
    public string SurfaceAlt { get; set; } = "#17171A";
    public string Border { get; set; } = "#26262B";
    public string Text { get; set; } = "#ECECEE";
    public string TextMuted { get; set; } = "#8A8A93";

    /// <summary>Цвет "погасших" точек сетки. null = не рисовать.</summary>
    public string? GhostDot { get; set; } = "#FFFFFF14";
}

public sealed class ThemeDot
{
    /// <summary>Размер ячейки точки, px.</summary>
    public int Cell { get; set; } = 4;

    /// <summary>Зазор между точками, px.</summary>
    public int Gap { get; set; } = 2;

    /// <summary>Насколько точка "полная": 0.5 = кружки, 0.95 = почти квадраты.</summary>
    public double Fill { get; set; } = 0.86;

    /// <summary>Минимальный радиус для тусклых пикселей (0..1 от ячейки).</summary>
    public double MinRadius { get; set; } = 0.16;

    /// <summary>Яркость, ниже которой ячейка считается пустой.</summary>
    public double Threshold { get; set; } = 0.07;
}

public sealed class ThemeMotion
{
    /// <summary>Длительность раскрытия острова, мс.</summary>
    public int IslandExpandMs { get; set; } = 420;

    /// <summary>Кривая анимации в CSS-нотации (потом уедет в Composition-эйзинг).</summary>
    public string Easing { get; set; } = "cubic-bezier(0.2, 0.9, 0.2, 1)";

    /// <summary>Длительность появления панелей, мс.</summary>
    public int PanelFadeMs { get; set; } = 220;
}

public sealed class ThemeWidgets
{
    /// <summary>Что показывать в свёрнутом острове, слева направо.</summary>
    public string[] Collapsed { get; set; } = { "clock", "media", "cpu", "battery" };

    /// <summary>Вкладки раскрытого острова.</summary>
    public string[] Tabs { get; set; } = { "notes", "clipboard", "eq", "system", "theme" };

    /// <summary>Горячая клавиша лаунчера.</summary>
    public string LauncherHotkey { get; set; } = "Ctrl+Space";

    /// <summary>Горячая клавиша буфера обмена.</summary>
    public string ClipboardHotkey { get; set; } = "Ctrl+Shift+V";
}
