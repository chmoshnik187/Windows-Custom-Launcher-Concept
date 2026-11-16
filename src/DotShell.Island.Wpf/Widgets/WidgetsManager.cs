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

        if (x < -20000 && spec is not null)
        {
            var restored = TryResolveRestorePosition(spec);
            if (restored is { } pos) { x = pos.X; y = pos.Y; }
            else if (spec.X > -20000) { x = spec.X; y = spec.Y; }
        }

        if (x < -20000)
        {
            // Позиция по умолчанию: лесенка, чтобы виджеты не легли друг на друга.
            var wa = GetDefaultWorkAreaDip();
            var n = Open.Count;
            x = (int)wa.Left + 48 + n * 42;
            y = (int)wa.Top + 72 + n * 42;
        }

        // Размер: сохранённый или дефолтный для этого типа.
        var (defW, defH) = DefaultSize(type);
        int width = spec is { W: > 0 } ? spec.W : defW;
        int height = spec is { H: > 0 } ? spec.H : defH;

        // Держим виджет целиком на экране (virtual screen fallback).
        var vs = MonitorHelper.GetVirtualScreenDip();
        x = (int)Math.Clamp(x, vs.Left, Math.Max(vs.Left, vs.Right - Math.Max(width, 160)));
        y = (int)Math.Clamp(y, vs.Top, Math.Max(vs.Top, vs.Bottom - Math.Max(height, 90)));

        var saved = Store.Add(type, x, y, path);
        var window = new WidgetWindow(type, x, y, width, height, path,
            saved.PinnedPaths,
            pins => Store.UpdatePinnedPaths(type, path, pins),
            saved.Collection,
            col => Store.UpdateCollection(type, path, col));
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

    private static bool _displayHooked;

    private static void EnsureDisplayHook()
    {
        if (_displayHooked) return;
        _displayHooked = true;
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) =>
            {
                try { Application.Current?.Dispatcher.BeginInvoke(OnDisplayChanged); }
                catch { OnDisplayChanged(); }
            };
        }
        catch { }
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

    /// <summary>Применить текущий desktop-режим (DesktopWidgets/DesktopHostEnabled) ко всем открытым виджетам.</summary>
    public static void ApplyDesktopModeAll()
    {
        foreach (var window in Open.Values.ToList())
        {
            try { window.ApplyDesktopMode(); }
            catch { }
        }
    }

    /// <summary>Реакция на смену конфигурации дисплеев: кламп/сохранение раскладки открытых виджетов.</summary>
    public static void OnDisplayChanged()
    {
        foreach (var window in Open.Values.ToList())
        {
            try
            {
                window.ClampToScreen();
                SaveLayout(window);
            }
            catch { }
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
        SaveLayout(window);
    }

    /// <summary>Виджет растянули за угол: запоминаем размер.</summary>
    public static void OnResized(WidgetWindow window)
    {
        SaveLayout(window);
    }

    private static void SaveLayout(WidgetWindow window)
    {
        int x = (int)window.Left;
        int y = (int)window.Top;
        Store.UpdateSize(window.WidgetType, (int)window.ActualWidth, (int)window.ActualHeight, window.WidgetPath);

        var settings = ShellServices.Instance.Settings.Current;
        if (!settings.PerMonitorLayouts)
        {
            Store.UpdateLayout(window.WidgetType, x, y, string.Empty, int.MinValue, int.MinValue, window.WidgetPath);
            return;
        }

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            string monitor = DotShell.Core.Windowing.Monitors.DeviceNameForWindow(hwnd) ?? string.Empty;
            var physical = hwnd != IntPtr.Zero ? DotShell.Core.Windowing.Monitors.ForWindow(hwnd) : null;
            if (!string.IsNullOrEmpty(monitor) && physical is { } wa)
            {
                var topLeft = window.PointFromScreen(new System.Windows.Point(wa.Left, wa.Top));
                var originX = window.Left + topLeft.X;
                var originY = window.Top + topLeft.Y;
                int relX = (int)Math.Round(window.Left - originX);
                int relY = (int)Math.Round(window.Top - originY);
                Store.UpdateLayout(window.WidgetType, x, y, monitor, relX, relY, window.WidgetPath);
                return;
            }
        }
        catch { }

        Store.UpdateLayout(window.WidgetType, x, y, string.Empty, int.MinValue, int.MinValue, window.WidgetPath);
    }

    private static (int X, int Y)? TryResolveRestorePosition(WidgetSpec spec)
    {
        try
        {
            var settings = ShellServices.Instance.Settings.Current;
            if (!settings.PerMonitorLayouts) return null;
            if (string.IsNullOrWhiteSpace(spec.LayoutMonitor)) return null;
            if (spec.LayoutX == int.MinValue || spec.LayoutY == int.MinValue) return null;

            var physical = DotShell.Core.Windowing.Monitors.WorkAreaForDevice(spec.LayoutMonitor);
            if (physical is null) return null;

            // Восстанавливаем относительно рабочего area монитора (физические пиксели ~ DIPs на 100%).
            // При смешанном DPI возможна погрешность; итог клампится по виртуальному экрану.
            int x = physical.Value.Left + spec.LayoutX;
            int y = physical.Value.Top + spec.LayoutY;
            return (x, y);
        }
        catch { return null; }
    }

    private static Rect GetDefaultWorkAreaDip()
    {
        try
        {
            var island = IslandWindow.Current;
            if (island is not null && island.IsLoaded)
                return MonitorHelper.GetWorkAreaDip(island);
        }
        catch { }
        return SystemParameters.WorkArea;
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
        EnsureDisplayHook();
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

    /// <summary>D1.1: сохранить текущую раскладку виджетов как именованный профиль.</summary>
    public static void SaveLayoutProfile(string name)
    {
        // Сначала синхронизируем позиции/размеры открытых окон в store.
        foreach (var w in Open.Values.ToList())
        {
            try { SaveLayout(w); } catch { }
        }
        WidgetLayoutProfiles.SaveProfile(name, Store.Snapshot());
    }

    /// <summary>D1.1: применить именованный профиль раскладки (закрывает текущие виджеты и восстанавливает из профиля).</summary>
    public static bool ApplyLayoutProfile(string name)
    {
        var profile = WidgetLayoutProfiles.Get(name);
        if (profile is null) return false;

        // Закрыть открытые окна без очистки store (store заменим).
        foreach (var window in Open.Values.ToList())
        {
            try { window.Close(); } catch { }
        }
        Open.Clear();

        Store.ReplaceAll(profile.Items);
        Restore();
        Changed?.Invoke();
        return true;
    }

    public static IReadOnlyList<string> ListLayoutProfiles() =>
        WidgetLayoutProfiles.LoadAll().Select(p => p.Name).ToList();

    public static bool DeleteLayoutProfile(string name) => WidgetLayoutProfiles.Delete(name);
}


