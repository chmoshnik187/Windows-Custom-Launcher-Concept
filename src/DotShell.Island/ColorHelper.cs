namespace DotShell.Island;

using DotShell.Core.Theming;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

/// <summary>
/// Мост между ThemeBus (ядро) и XAML.
///
/// Идея простая: в XAML все цвета, зависящие от акцента, ссылаются на ресурсы
/// {ThemeResource DsAccent} и {ThemeResource DsAccentSoft}. Когда ядро сообщает
/// о новом цвете, мы МЕНЯЕМ ЗНАЧЕНИЕ РЕСУРСА — и весь интерфейс перекрашивается сам,
/// без единого обращения к конкретным контролам.
///
/// Это и есть "одна переменная красит всё", ровно как в CSS в прототипе.
/// </summary>
public static class ColorHelper
{
    private const string AccentKey = "DsAccent";
    private const string AccentSoftKey = "DsAccentSoft";
    private const string GhostKey = "DsGhostDot";

    public static Color Accent { get; private set; } = Colors.White;
    public static Color AccentFaded { get; private set; } = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

    /// <summary>Подписаться на ядро. Вызывается один раз при старте.</summary>
    public static void Bind(ThemeBus bus)
    {
        bus.AccentChanged += (argb, _) =>
        {
            // Событие может прийти из таймера сэмплера — то есть не из UI-потока.
            // Ресурсы XAML трогать можно только из UI-потока, поэтому маршалимся.
            RunOnUiThread(() => Apply(argb, bus.ShowGhostDots));
        };

        bus.SettingsChanged += settings =>
            RunOnUiThread(() => Apply(bus.AccentArgb, settings.ShowGhostDots));

        Apply(bus.AccentArgb, bus.ShowGhostDots);
    }

    private static void Apply(uint argb, bool showGhostDots)
    {
        var color = FromArgb(argb);
        Accent = color;
        AccentFaded = Color.FromArgb(0x3A, color.R, color.G, color.B);

        var dict = Application.Current?.Resources;
        if (dict is null) return;

        dict[AccentKey] = new SolidColorBrush(color);
        dict[AccentSoftKey] = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
        dict[GhostKey] = new SolidColorBrush(showGhostDots
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Colors.Transparent);
    }

    public static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.DispatcherQueue;
        if (dispatcher is null) { action(); return; }
        if (dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }
}
