namespace DotShell.Core.Input;

using System.Text.Json;
using DotShell.Core.Settings;

/// <summary>Одна подсказка хоткея: «Ctrl+Shift+T — вернуть закрытую вкладку».</summary>
public sealed record HotkeyTip(string Keys, string What);

/// <summary>
/// База хоткеев для HUD: общий набор Windows + кураторские наборы под частые
/// программы. Плюс слияние с %LOCALAPPDATA%\\DotShell\\hotkeys.json — владелец
/// может дописать свои подсказки под любую программу без пересборки шелла.
///
/// Формат json: { "имя_процесса": [ { "keys": "Ctrl+K", "what": "командная строка" } ] }
/// </summary>
public static class HotkeyCatalog
{
    private static readonly List<HotkeyTip> Common = new()
    {
        new("Win+D", "свернуть всё и показать рабочий стол"),
        new("Win+E", "проводник"),
        new("Win+V", "журнал буфера обмена"),
        new("Win+Shift+S", "ножницы: снимок области экрана"),
        new("Win+L", "заблокировать"),
        new("Win+. ", "эмодзи и символы"),
        new("Alt+Tab", "переключить окно"),
        new("Win+Tab", "все окна и столы"),
        new("Ctrl+Shift+Space", "раскрыть остров DotShell"),
        new("Ctrl+Shift+L", "лаунчер DotShell"),
    };

    private static readonly Dictionary<string, List<HotkeyTip>> Apps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["explorer"] = new()
        {
            new("Alt+←", "назад"), new("Alt+→", "вперёд"),
            new("Alt+↑", "на уровень вверх"),
            new("Ctrl+Shift+N", "новая папка"),
            new("F2", "переименовать"),
            new("Shift+Delete", "удалить мимо корзины"),
            new("Alt+Enter", "свойства файла"),
            new("Ctrl+L", "курсор в адресную строку"),
        },
        ["chrome"] = Browser(),
        ["msedge"] = Browser(),
        ["firefox"] = Browser(),
        ["notepad"] = new()
        {
            new("Ctrl+S", "сохранить"), new("Ctrl+G", "перейти к строке"),
            new("Ctrl+H", "заменить"), new("F5", "время и дата"),
        },
        ["winword"] = new()
        {
            new("Ctrl+B / I / U", "жирный / курсив / подчёркнутый"),
            new("Ctrl+K", "вставить ссылку"),
            new("Ctrl+Enter", "разрыв страницы"),
            new("F12", "сохранить как"),
        },
        ["excel"] = new()
        {
            new("Ctrl+1", "формат ячеек"),
            new("Ctrl+Shift+L", "фильтры"),
            new("Alt+=", "автосумма"),
            new("Ctrl+;", "сегодняшняя дата"),
        },
        ["Code"] = new()
        {
            new("Ctrl+P", "быстрый файл"),
            new("Ctrl+Shift+P", "палитра команд"),
            new("Ctrl+/", "комментарий строки"),
            new("Alt+↑ / ↓", "перенести строку"),
        },
        ["Spotify"] = new()
        {
            new("Space", "пауза / играть"),
            new("Ctrl+→ / ←", "следующий / предыдущий трек"),
        },
    };

    private static List<HotkeyTip> Browser() => new()
    {
        new("Ctrl+T", "новая вкладка"),
        new("Ctrl+Shift+T", "вернуть закрытую вкладку"),
        new("Ctrl+W", "закрыть вкладку"),
        new("Ctrl+L", "в адресную строку"),
        new("Ctrl+D", "закладка"),
        new("Ctrl+Shift+Delete", "очистить историю"),
        new("F12", "инструменты разработчика"),
        new("Ctrl+Tab", "следующая вкладка"),
    };

    /// <summary>Подсказки для процесса: свои из json + кураторские + общие.</summary>
    public static List<HotkeyTip> For(string processName)
    {
        var tips = new List<HotkeyTip>();

        foreach (var tip in Custom().Where(kv => kv.Key.Equals(processName, StringComparison.OrdinalIgnoreCase))
                     .SelectMany(kv => kv.Value))
            tips.Add(tip);

        if (Apps.TryGetValue(processName ?? string.Empty, out var app))
            tips.AddRange(app.Where(t => t.What.Length > 0));

        tips.AddRange(Common);
        return tips.Take(14).ToList();
    }

    // ------------------------------------------------------------------

    private static List<KeyValuePair<string, List<HotkeyTip>>>? _customCache;
    private static DateTime _customRead;

    private static List<KeyValuePair<string, List<HotkeyTip>>> Custom()
    {
        if (_customCache is not null && (DateTime.Now - _customRead).TotalSeconds < 10) return _customCache;
        _customRead = DateTime.Now;
        try
        {
            var path = Path.Combine(AppSettings.SettingsDirectory, "hotkeys.json");
            if (!File.Exists(path)) return _customCache = new();
            var parsed = JsonSerializer.Deserialize<Dictionary<string, List<HotkeyTip>>>(File.ReadAllText(path));
            return _customCache = parsed?.ToList() ?? new();
        }
        catch { return _customCache = new(); }
    }
}
