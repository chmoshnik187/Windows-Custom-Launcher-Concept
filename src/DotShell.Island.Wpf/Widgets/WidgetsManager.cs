namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
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
        { "clock", "eq", "media", "system", "units", "settings", "clean", "dupes", "update", "windows", "audio", "currency", "tray", "weather" };

    public static string Label(string type) => type switch
    {
        "clock" => "Часы из точек",
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
        _ => type,
    };

    public static event Action? Changed;

    private static readonly Dictionary<string, WidgetWindow> Open = new();

    private static WidgetStore Store => ShellServices.Instance.Widgets;

    public static bool Has(string type) => Open.ContainsKey(type);

    /// <summary>Открытое окно виджета (нужно для проверки раскладок и снимков).</summary>
    public static WidgetWindow? Get(string type) => Open.TryGetValue(type, out var window) ? window : null;

    public static void Toggle(string type)
    {
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
        _ => (0, 0),
    };
    public static void OpenWidget(string type, int x = -32000, int y = -32000)
    {
        if (Has(type) || !Types.Contains(type)) return;

        var spec = Store.Find(type);
        if (x < -20000 && spec is not null && spec.X > -20000) { x = spec.X; y = spec.Y; }

        if (x < -20000)
        {
            // Позиция по умолчанию: лесенка от левого верхнего угла, чтобы
            // первые пять виджетов не легли друг на друга.
            var wa = SystemParameters.WorkArea;
            var n = Open.Count;
            x = (int)wa.Left + 48 + n * 42;
            y = (int)wa.Top + 72 + n * 42;
        }

        // Размер: сохранённый или дефолтный для этого типа.
        var (defW, defH) = DefaultSize(type);
        int width = spec is { W: > 0 } ? spec.W : defW;
        int height = spec is { H: > 0 } ? spec.H : defH;

        // И держим виджет целиком на экране: иначе ручка ресайза оказывается
        // за нижним краем, и уменьшить виджет физически нечем.
        var work = SystemParameters.WorkArea;
        x = (int)Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - Math.Max(width, 160)));
        y = (int)Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - Math.Max(height, 90)));

        Store.Add(type, x, y);
        var window = new WidgetWindow(type, x, y, width, height);
        Open[type] = window;
        window.Closed += (_, _) => Open.Remove(type);
        window.Show();
        Changed?.Invoke();
    }

    /// <summary>
    /// Перерисовать открытые виджеты: события вроде смены города приходят снаружи,
    /// и ждать таймера виджета (15 минут) для них слишком долго.
    /// </summary>
    public static void RefreshAll()
    {
        foreach (var window in Open.Values.ToList())
        {
            try { window.Reload(); } catch { }
        }
    }
    public static void Close(string type)
    {
        if (!Open.TryGetValue(type, out var window)) return;
        Open.Remove(type);
        Store.Remove(type);
        window.Close();
        Changed?.Invoke();
    }

    /// <summary>Виджет оттащили: запоминаем положение.</summary>
    public static void OnMoved(WidgetWindow window)
    {
        window.ClampToScreen();
        Store.UpdatePosition(window.WidgetType, (int)window.Left, (int)window.Top);
    }

    /// <summary>Виджет растянули за угол: запоминаем размер.</summary>
    public static void OnResized(WidgetWindow window)
    {
        Store.UpdateSize(window.WidgetType, (int)window.ActualWidth, (int)window.ActualHeight);
    }

    /// <summary>Восстановить виджеты прошлого запуска (вызывается при старте шелла).</summary>
    public static void Restore()
    {
        foreach (var spec in Store.Items.ToList())
            OpenWidget(spec.Type, spec.X, spec.Y);
    }
}


