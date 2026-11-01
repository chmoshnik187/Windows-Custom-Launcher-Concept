namespace DotShell.Core.Theming;

/// <summary>
/// Скин — это не только цвет. Это связка: акцент, размер точки, зазор,
/// рисунок подложки и её плотность. Один клик меняет всё сразу, чтобы человек
/// не подбирал пять ползунков, пытаясь получить «другой характер».
///
/// Почему так, а не PNG-тема: шелл рисует точки своим движком, поэтому скин
/// это числа, а не картинки. Зато он честно работает на любом разрешении
/// и мгновенно перекрашивается, когда меняется акцент.
/// </summary>
public sealed record Skin(
    string Id,
    string Name,
    string Motto,
    string AccentHex,
    bool AutoAccent,
    int CellSize,
    int Gap,
    string BackdropStyle,
    int BackdropOpacity,
    int IslandOpacity);

public static class Skins
{
    /// <summary>
    /// Порядок важен: первый — «родной», с него всё начиналось.
    /// </summary>
    public static readonly IReadOnlyList<Skin> All = new List<Skin>
    {
        new("nothing", "NOTHING", "красный штамп, точечная сетка",
            "#D71920", true, 4, 2, "dots", 45, 92),

        new("cmf", "CMF", "белый монохром, крупная точка",
            "#EDEDEF", false, 5, 3, "grid", 55, 94),

        new("phosphor", "ФОСФОР", "зелёный терминал, мелкая сетка",
            "#35FF7A", false, 3, 1, "grid", 70, 96),

        new("violet", "УЛЬТРАФИОЛЕТ", "фиолетовое свечение",
            "#7C4DFF", false, 3, 2, "aurora", 62, 90),

        new("amber", "ЯНТАРЬ", "тёплый закат, волны",
            "#FF9E2C", false, 4, 2, "waves", 52, 92),
    };

    public static Skin? ById(string? id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
