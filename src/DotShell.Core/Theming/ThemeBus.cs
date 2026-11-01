namespace DotShell.Core.Theming;

using DotShell.Core.Settings;

/// <summary>
/// Theme Bus — "шина темы". Одна точка правды про цвет и про точечную сетку.
///
/// Зачем она нужна: остров, лаунчер, панели и обои — это разные процессы/окна,
/// но акцент у них обязан быть одинаковым и меняться одновременно.
/// Все они просто подписываются сюда и перерисовываются, когда пришло событие.
///
/// Позже этот же класс станет мостом между процессами (named pipes / gRPC),
/// поэтому уже сейчас никто не читает настройки напрямую — все ходят через ThemeBus.
/// </summary>
public sealed class ThemeBus
{
    private readonly SettingsStore _settings;

    /// <summary>Текущий акцент в виде 0xAARRGGBB (формат WinUI Color).</summary>
    public uint AccentArgb { get; private set; }

    /// <summary>Текущий акцент в виде #RRGGBB — для конфигов и theme.json.</summary>
    public string AccentHex { get; private set; } = "#D71920";

    public int CellSize => _settings.Current.CellSize;
    public int Gap => _settings.Current.Gap;
    public bool ShowGhostDots => _settings.Current.ShowGhostDots;
    public AppSettings Settings => _settings.Current;

    /// <summary>Сюда приходит новый акцент. Строка — имя источника ("auto", "preset", "manual").</summary>
    public event Action<uint, string>? AccentChanged;

    /// <summary>Сюда приходят изменения любых настроек (размер точки, поведение и т.д.).</summary>
    public event Action<AppSettings>? SettingsChanged;

    // Сглаживание: цвет "догоняет" цель постепенно, чтобы обои не вызывали стробоскоп.
    private double _r, _g, _b;
    private uint _targetArgb;

    public ThemeBus(SettingsStore settings)
    {
        _settings = settings;
        AccentArgb = ParseHex(settings.Current.AccentHex);
        AccentHex = settings.Current.AccentHex;
        (_r, _g, _b) = Unpack(AccentArgb);
        _targetArgb = AccentArgb;

        _settings.Changed += s =>
        {
            SettingsChanged?.Invoke(s);
            // Если пользователь выключил авто-цвет — сразу применяем ручной.
            if (!s.AutoAccent) SetAccent(ParseHex(s.AccentHex), "manual");
        };
    }

    /// <summary>Мгновенно задать акцент (клик по пресету, ручной выбор).</summary>
    public void SetAccent(uint argb, string source = "manual")
    {
        _targetArgb = argb;
        AccentArgb = argb;
        AccentHex = ToHex(argb);
        (_r, _g, _b) = Unpack(argb);
        AccentChanged?.Invoke(argb, source);
    }

    /// <summary>
    /// Плавно подвинуть акцент к целевому цвету. Вызывается подборщиком цвета из обоев.
    /// Возвращает true, если цвет реально изменился (тогда надо перерисовать UI).
    /// </summary>
    public bool EaseToward(uint targetArgb)
    {
        _targetArgb = targetArgb;
        var (tr, tg, tb) = Unpack(targetArgb);
        var k = Math.Clamp(_settings.Current.AccentSmoothing, 0.02, 1.0);
        _r += (tr - _r) * k;
        _g += (tg - _g) * k;
        _b += (tb - _b) * k;

        var next = Pack(_r, _g, _b);
        if (next == AccentArgb) return false;

        AccentArgb = next;
        AccentHex = ToHex(next);
        AccentChanged?.Invoke(next, "auto");
        return true;
    }

    /// <summary>Целевой цвет (куда мы стремимся) — handy для отладки.</summary>
    public uint TargetAccent => _targetArgb;

    // ---------- утилиты цвета ----------

    public static uint ParseHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return 0xFFD71920;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        return uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0xFFD71920;
    }

    public static string ToHex(uint argb) =>
        "#" + (argb & 0xFFFFFF).ToString("X6");

    /// <summary>Цвет с нужной прозрачностью (для "погасших" точек, рамок, свечения).</summary>
    public uint WithAlpha(byte alpha) => ((uint)alpha << 24) | (AccentArgb & 0xFFFFFF);

    /// <summary>Смешать акцент с другим цветом — для мягких рамок и подложек.</summary>
    public uint Mix(uint otherArgb, double amount)
    {
        var (ar, ag, ab) = Unpack(AccentArgb);
        var (br, bg, bb) = Unpack(otherArgb);
        var t = Math.Clamp(amount, 0, 1);
        return Pack(ar + (br - ar) * t, ag + (bg - ag) * t, ab + (bb - ab) * t);
    }

    private static (double r, double g, double b) Unpack(uint argb) =>
        ((argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF);

    private static uint Pack(double r, double g, double b)
    {
        byte cb(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return 0xFF000000u | ((uint)cb(r) << 16) | ((uint)cb(g) << 8) | (uint)cb(b);
    }
}
