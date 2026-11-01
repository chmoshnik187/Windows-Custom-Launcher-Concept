namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Media;
using DotShell.Core.Theming;

/// <summary>
/// Палитра DotShell и мост между ThemeBus (ядро) и WPF.
///
/// Приём, который держит всю кастомизацию: цвета лежат в Application.Resources
/// под фиксированными ключами, а весь XAML ссылается на них через DynamicResource.
/// Когда ядро сообщает о новом акценте, мы МЕНЯЕМ ЗНАЧЕНИЕ РЕСУРСА — и весь
/// интерфейс перекрашивается сам, без единого обращения к конкретным контролам.
///
/// Именно поэтому в XAML везде DynamicResource, а не StaticResource:
/// StaticResource вычисляется один раз при загрузке и замену не подхватит.
/// </summary>
public static class ColorPalette
{
    public const string AccentKey = "DsAccent";
    public const string AccentSoftKey = "DsAccentSoft";
    public const string AccentGhostKey = "DsAccentGhost";
    public const string BackgroundKey = "DsBackground";
    public const string SurfaceKey = "DsSurface";
    public const string SurfaceAltKey = "DsSurfaceAlt";
    public const string BorderKey = "DsBorder";
    public const string TextKey = "DsText";
    public const string TextMutedKey = "DsTextMuted";
    public const string GhostDotKey = "DsGhostDot";
    public const string OnAccentKey = "DsOnAccent";

    public static Color Accent { get; private set; } = Color.FromRgb(0xD7, 0x19, 0x20);

    /// <summary>Акцент с пониженной прозрачностью — для мигающих элементов.</summary>
    public static Color AccentFaded { get; private set; } = Color.FromArgb(0x40, 0xD7, 0x19, 0x20);

    /// <summary>
    /// Срабатывает при смене акцента.
    /// Нужно тем контролам, у которых цвет — это свойство типа Color, а не Brush:
    /// к ним нельзя привязать SolidColorBrush из ресурсов, поэтому подписываемся напрямую.
    /// Вызывается уже в UI-потоке.
    /// </summary>
    public static event Action<Color>? AccentColorChanged;

    /// <summary>Текущий цвет "погасших" точек сетки (Transparent, если они выключены).</summary>
    public static Color GhostDotColor { get; private set; } = Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF);

    /// <summary>Срабатывает вместе с AccentColorChanged — чтобы контролы обновили и сетку.</summary>
    public static event Action? PaletteChanged;

    public static void Install(ResourceDictionary resources)
    {
        resources[AccentKey] = new SolidColorBrush(Accent);
        resources[AccentSoftKey] = new SolidColorBrush(Color.FromArgb(0x44, Accent.R, Accent.G, Accent.B));
        resources[AccentGhostKey] = new SolidColorBrush(Color.FromArgb(0x22, Accent.R, Accent.G, Accent.B));
        // Подложка острова: чёрный с прозрачностью по умолчанию 92 %.
        // Прозрачность влияет ТОЛЬКО на неё — акцент и текст остаются плотными.
        resources[BackgroundKey] = new SolidColorBrush(Color.FromArgb(0xEA, 0x00, 0x00, 0x00));
        resources[SurfaceKey] = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14));
        resources[SurfaceAltKey] = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1A));
        resources[BorderKey] = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2B));
        resources[TextKey] = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        resources[TextMutedKey] = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));
        resources[GhostDotKey] = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));

        // Текст поверх акцента: чёрный на ярком, белый на тёмном.
        resources[OnAccentKey] = new SolidColorBrush(PickContrast(Accent));
    }

    /// <summary>Подписаться на ядро. Вызывается один раз при старте.</summary>
    public static void Bind(ThemeBus bus)
    {
        bus.AccentChanged += (_, _) => RunOnUiThread(() => Apply(bus));
        bus.SettingsChanged += _ => RunOnUiThread(() => Apply(bus));
        Apply(bus);
    }

    private static void Apply(ThemeBus bus)
    {
        var argb = bus.AccentArgb;
        var showGhostDots = bus.ShowGhostDots;
        var opacity = Math.Clamp(bus.Settings.IslandOpacity, 30, 100);
        var color = FromArgb(argb);
        Accent = color;
        AccentFaded = Color.FromArgb(0x40, color.R, color.G, color.B);

        var res = Application.Current?.Resources;
        if (res is null) return;

        res[AccentKey] = new SolidColorBrush(color);
        res[AccentSoftKey] = new SolidColorBrush(Color.FromArgb(0x44, color.R, color.G, color.B));
        res[AccentGhostKey] = new SolidColorBrush(Color.FromArgb(0x22, color.R, color.G, color.B));
        res[BackgroundKey] = new SolidColorBrush(Color.FromArgb((byte)(255 * opacity / 100), 0x00, 0x00, 0x00));
        res[GhostDotKey] = new SolidColorBrush(showGhostDots
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Colors.Transparent);
        res[OnAccentKey] = new SolidColorBrush(PickContrast(color));

        GhostDotColor = showGhostDots
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Colors.Transparent;

        AccentColorChanged?.Invoke(color);
        PaletteChanged?.Invoke();
    }

    /// <summary>Чёрный или белый текст поверх заданного цвета — по воспринимаемой яркости.</summary>
    public static Color PickContrast(Color c)
    {
        double l = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        return l > 0.62 ? Colors.Black : Colors.White;
    }

    public static Color FromArgb(uint argb) => Color.FromArgb(
        (byte)((argb >> 24) & 0xFF),
        (byte)((argb >> 16) & 0xFF),
        (byte)((argb >> 8) & 0xFF),
        (byte)(argb & 0xFF));

    public static uint ToArgb(Color c) =>
        ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static void RunOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
