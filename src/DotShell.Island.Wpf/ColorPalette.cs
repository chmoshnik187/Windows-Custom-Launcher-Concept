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

    /// <summary>Второй акцент (прогресс, активные элементы). null — не задан.</summary>
    public static Color? AccentSecond { get; private set; }

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
        var color = ApplyTint(FromArgb(argb), bus.Settings.AccentTemperature, bus.Settings.AccentSaturation);
        Accent = color;
        AccentFaded = Color.FromArgb(0x40, color.R, color.G, color.B);

        // Второй акцент: если задан цвет — прогресс и активные элементы берут его.
        string second = bus.Settings.AccentSecondHex;
        AccentSecond = string.IsNullOrWhiteSpace(second) ? null : FromArgb(ParseHex(second));

        // «Где живёт акцент»: soft/ghost — это второстепенные акцентные элементы
        // (чипы, рамки, фон). В узком режиме они приглушаются, основной акцент цел.
        byte soft = bus.Settings.AccentFocus switch
        {
            "clock" or "player" => 0x2A,
            "quiet" => 0x1A,
            _ => 0x44,
        };

        byte ghost = bus.Settings.AccentFocus switch
        {
            "clock" or "player" => 0x14,
            "quiet" => 0x10,
            _ => 0x22,
        };

        var res = Application.Current?.Resources;
        if (res is null) return;

        res[AccentKey] = new SolidColorBrush(color);
        res[AccentSoftKey] = new SolidColorBrush(Color.FromArgb(soft, color.R, color.G, color.B));
        res[AccentGhostKey] = new SolidColorBrush(Color.FromArgb(ghost, color.R, color.G, color.B));
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

    /// <summary>
    /// Сдвиг оттенка и насыщенности акцента: «температура» и «насыщенность» из
    /// настроек. Работаем в HSV, чтобы не портить яркость.
    /// </summary>
    private static Color ApplyTint(Color color, int temperature, int saturation)
    {
        double h = 0, s = 0, v = 0;

        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
        }
        else if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * ((b - r) / delta + 2);
        }
        else
        {
            h = 60 * ((r - g) / delta + 4);
        }

        if (h < 0) h += 360;

        h = (h + Math.Clamp(temperature, -30, 30) + 360) % 360;
        s = Math.Clamp(s * Math.Clamp(saturation, 0, 100) / 100.0, 0, 1);

        // Обратно в RGB.
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        double rr, gg, bb;
        if (h < 60) { rr = c; gg = x; bb = 0; }
        else if (h < 120) { rr = x; gg = c; bb = 0; }
        else if (h < 180) { rr = 0; gg = c; bb = x; }
        else if (h < 240) { rr = 0; gg = x; bb = c; }
        else if (h < 300) { rr = x; gg = 0; bb = c; }
        else { rr = c; gg = 0; bb = x; }

        return Color.FromRgb(
            (byte)Math.Clamp((rr + m) * 255, 0, 255),
            (byte)Math.Clamp((gg + m) * 255, 0, 255),
            (byte)Math.Clamp((bb + m) * 255, 0, 255));
    }

    /// <summary>#RRGGBB → uint ARGB с непрозрачностью.</summary>
    private static uint ParseHex(string hex)
    {
        string text = hex.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint rgb))
            return 0xFF000000u | rgb;

        return 0xFFD71920u;
    }
}
