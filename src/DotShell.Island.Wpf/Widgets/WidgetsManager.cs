namespace DotShell.Island.Wpf.Widgets;

using System.Linq;
using System.Windows;
using DotShell.Core;
using DotShell.Core.Widgets;

/// <summary>
/// Реестр живых виджетов рабочего стола.
///
/// Владелец просил «заставить рабочий стол виджетами»: здесь пять стартовых
/// (часы, эквалайзер, плеер, система, юниты), каждый — окно WidgetWindow.
/// Состав и раскладка переживают перезапуск (widgets.json), добавить/убрать
/// можно из трея и с главной страницы острова.
/// </summary>
public static class WidgetsManager
{
    public static readonly string[] Types =
        { "clock", "eq", "media", "system", "units", "settings", "clean", "dupes", "update", "windows", "audio", "currency", "tray", "weather", "calendar", "folder" };

    public static string Label(string type) => type switch
    {
        "clock" => "Часы из точек",
        "calendar" => "Календарь месяца",
        "eq" => "Эквалайзер",
        "media" => "Плеер",
        "system" => "Система (CPU/RAM)",
        "units" => "Юниты",
        "settings" => "Поиск настроек",
        "clean" => "Зашлакованность диска",
        "dupes" => "Радар дубликатов",
        "update" => "Светофор обновлений",
        "windows" => "Карта окон",
        "audio" => "Аудио-роутер: приложение → устройство",
        "currency" => "Курс доллара и юаня",
        "tray" => "Трей: значки из области уведомлений",
        "weather" => "Погода в городе",
        "folder" => "Умная папка",
        _ => type,
    };

    public static event Action? Changed;

    private static readonly Dictionary<string, WidgetWindow> Open = new();

    private static WidgetStore Store => ShellServices.Instance.Widgets;

    private static string Key(string type, string path = "") =>
        string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
            ? $"folder|{(path ?? string.Empty).ToLowerInvariant()}"
            : type;

    public static bool Has(string type, string path = "") => Open.ContainsKey(Key(type, path));

    /// <summary>Открытое окно виджета (нужно для проверки раскладок и снимков).</summary>
    public static WidgetWindow? Get(string type, string path = "") =>
        Open.TryGetValue(Key(type, path), out var window) ? window : null;

    /// <summary>Сколько экземпляров типа сейчас открыто (для папок — несколько).</summary>
    public static int Count(string type) => Open.Values.Count(w => string.Equals(w.WidgetType, type, StringComparison.OrdinalIgnoreCase));

    public static void Toggle(string type)
    {
        // Для папок «переключение» не имеет смысла — создаётся новый экземпляр с выбором пути.
        if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
        {
            OpenWidget(type);
            return;
        }

        if (Has(type)) Close(type);
        else OpenWidget(type);
    }

    /// <summary>
    /// Размер по умолчанию для каждого виджета. Раньше «размер по содержимому»
    /// давал эквалайзер во весь экран и окна, у которых ручка ресайза уезжала
    /// за нижний край — уменьшить было нечем.
    /// </summary>
    private static (int W, int H) DefaultSize(string type) => type switch
    {
        "clock" => (200, 118),
        "eq" => (210, 46),
        "media" => (320, 132),
        "system" => (210, 96),
        "units" => (150, 66),
        "settings" => (360, 280),
        "clean" => (330, 210),
        "dupes" => (380, 280),
        "update" => (330, 210),
        "windows" => (440, 320),
        "audio" => (610, 330),
        "currency" => (230, 132),
        "tray" => (320, 112),
        "weather" => (210, 130),
        "calendar" => (280, 240),
        "folder" => (320, 260),
        _ => (0, 0),
    };

    /// <summary>
    /// Открыть виджет. Для "folder" path обязателен (или будет запрошен);
    /// можно создать несколько папок-виджетов с разными путями.
    /// </summary>
    public static void OpenWidget(string type, int x = -32000, int y = -32000, string path = "")
    {
        if (!Types.Contains(type)) return;

        if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(path))
                path = PromptFolderPath() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return;
            path = System.IO.Path.GetFullPath(path);
        }

        if (Has(type, path)) return;

        var spec = string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
            ? Store.Items.FirstOrDefault(i => i.Type == type && string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))
            : Store.Find(type);

        if (x < -20000 && spec is not null && spec.X > -20000) { x = spec.X; y = spec.Y; }

        if (x < -20000)
        {
            // Позиция по умолчанию: лесенка, чтобы виджеты не легли друг на друга.
            var wa = SystemParameters.WorkArea;
            var n = Open.Count;
            x = (int)wa.Left + 48 + n * 42;
            y = (int)wa.Top + 72 + n * 42;
        }

        // Размер: сохранённый или дефолтный для этого типа.
        var (defW, defH) = DefaultSize(type);
        int width = spec is { W: > 0 } ? spec.W : defW;
        int height = spec is { H: > 0 } ? spec.H : defH;

        // Держим виджет целиком на экране.
        var work = SystemParameters.WorkArea;
        x = (int)Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - Math.Max(width, 160)));
        y = (int)Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - Math.Max(height, 90)));

        var saved = Store.Add(type, x, y, path);
        var window = new WidgetWindow(type, x, y, width, height, path,
            saved.PinnedPaths,
            pins => Store.UpdatePinnedPaths(type, path, pins));
        var key = Key(type, path);
        Open[key] = window;
        window.Closed += (_, _) => Open.Remove(key);
        window.Show();
        Changed?.Invoke();
    }

    private static string? PromptFolderPath()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку для виджета",
                Multiselect = false,
            };
            return dialog.ShowDialog() == true ? dialog.FolderName : null;
        }
        catch (Exception ex)
        {
            Log.Info($"widgets: folder picker failed — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Перерисовать открытые виджеты: события вроде смены города приходят снаружи,
    /// и ждать таймера виджета (15 минут) для них слишком долго.
    /// </summary>
    public static void RefreshAll()
    {
        foreach (var window in Open.Values.ToList())
        {
            try { window.Reload(); }
            catch (Exception ex) { Log.Info($"widgets: reload failed ({window.WidgetType}) — {ex.Message}"); }
        }
    }
    public static void Close(string type, string path = "")
    {
        var key = Key(type, path);
        if (!Open.TryGetValue(key, out var window)) return;
        Open.Remove(key);
        Store.Remove(type, path);
        window.Close();
        Changed?.Invoke();
    }

    /// <summary>Закрыть конкретное окно виджета (удобно для папок с путём).</summary>
    public static void CloseWindow(WidgetWindow window) => Close(window.WidgetType, window.WidgetPath);

    /// <summary>Виджет оттащили: запоминаем положение.</summary>
    public static void OnMoved(WidgetWindow window)
    {
        window.ClampToScreen();
        Store.UpdatePosition(window.WidgetType, (int)window.Left, (int)window.Top, window.WidgetPath);
    }

    /// <summary>Виджет растянули за угол: запоминаем размер.</summary>
    public static void OnResized(WidgetWindow window)
    {
        Store.UpdateSize(window.WidgetType, (int)window.ActualWidth, (int)window.ActualHeight, window.WidgetPath);
    }

    /// <summary>
    /// Убрать все виджеты сразу: закрыть окна и почистить сохранённый состав.
    /// Без этого виджеты копились — каждый запуск добавлял свои, и на столе
    /// оказывался десяток окон, которые надо было закрывать по одному.
    /// </summary>
    public static void CloseAll()
    {
        foreach (var window in Open.Values.ToList())
        {
            try { window.Close(); }
            catch (Exception ex) { Log.Info($"widgets: close failed ({window.WidgetType}) — {ex.Message}"); }
        }

        Open.Clear();
        Store.Clear();
        Changed?.Invoke();
    }

    /// <summary>Восстановить виджеты прошлого запуска (вызывается при старте шелла).</summary>
    public static void Restore()
    {
        foreach (var spec in Store.Items.ToList())
        {
            // Папка могла быть удалена/переименована — пропускаем без падения.
            if (string.Equals(spec.Type, "folder", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(spec.Path) || !System.IO.Directory.Exists(spec.Path))
                    continue;
                OpenWidget(spec.Type, spec.X, spec.Y, spec.Path);
            }
            else
            {
                OpenWidget(spec.Type, spec.X, spec.Y);
            }
        }
    }
}


