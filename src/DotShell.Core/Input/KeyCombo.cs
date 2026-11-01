namespace DotShell.Core.Input;

/// <summary>
/// Сочетания клавиш: разбор строк вида «Ctrl+Alt+1» в модификаторы и код клавиши.
///
/// Зачем строкой, а не структурой: так оно читаемо в pinned.json и settings.json —
/// человек может поправить руками, не зная наших типов.
///
/// Честное ограничение платформы: клавиши Fn здесь нет и быть не может.
/// Fn обрабатывает контроллер клавиатуры (или EC ноутбука) и в Windows приходит
/// только результат — медиа-клавиша, яркость, иногда вообще ничего. Ни
/// RegisterHotKey, ни низкоуровневый хук Fn не видят. Поэтому бинды — на
/// обычные модификаторы, а «что делает Fn+X» показывает проба клавиш.
/// </summary>
public static class KeyCombo
{
    public sealed record Parsed(uint Modifiers, uint VirtualKey, string Text);

    /// <summary>Разобрать «Ctrl+Alt+1». null — если строка непонятна или нет модификатора.</summary>
    public static Parsed? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        uint mods = 0;
        uint vk = 0;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= 0x0002; break;   // MOD_CONTROL
                case "alt": mods |= 0x0001; break;                  // MOD_ALT
                case "shift": mods |= 0x0004; break;                // MOD_SHIFT
                case "win": mods |= 0x0008; break;                  // MOD_WIN
                default: vk = VirtualKey(raw); break;
            }
        }

        if (vk == 0 || mods == 0) return null;
        return new Parsed(mods, vk, Format(mods, vk));
    }

    /// <summary>Человеческий вид: «Ctrl+Alt+1».</summary>
    public static string Format(uint mods, uint vk)
    {
        var parts = new List<string>(4);
        if ((mods & 0x0002) != 0) parts.Add("Ctrl");
        if ((mods & 0x0001) != 0) parts.Add("Alt");
        if ((mods & 0x0004) != 0) parts.Add("Shift");
        if ((mods & 0x0008) != 0) parts.Add("Win");
        parts.Add(KeyName(vk));
        return string.Join("+", parts);
    }

    /// <summary>Имя клавиши по коду.</summary>
    public static string KeyName(uint vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        0x20 => "Space",
        0x0D => "Enter",
        0x1B => "Esc",
        0x09 => "Tab",
        0x25 => "Left",
        0x26 => "Up",
        0x27 => "Right",
        0x28 => "Down",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PgUp",
        0x22 => "PgDn",
        0x2C => "PrintScreen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x14 => "CapsLock",
        0x08 => "Backspace",
        0xBB => "=",
        0xBD => "-",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        _ => $"0x{vk:X2}",
    };

    /// <summary>Код клавиши по имени (для разбора строки).</summary>
    private static uint VirtualKey(string name)
    {
        if (name.Length == 1)
        {
            var ch = char.ToUpperInvariant(name[0]);
            if (ch is >= '0' and <= '9') return ch;
            if (ch is >= 'A' and <= 'Z') return ch;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') &&
            int.TryParse(name[1..], out var fn) && fn is >= 1 and <= 12)
            return (uint)(0x6F + fn);

        return name.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" => 0x0D,
            "esc" or "escape" => 0x1B,
            "tab" => 0x09,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            "home" => 0x24,
            "end" => 0x23,
            "pgup" => 0x21,
            "pgdn" => 0x22,
            "insert" => 0x2D,
            "delete" => 0x2E,
            _ => 0,
        };
    }

    /// <summary>Понятное имя клавиши, которую нажали в пробе.</summary>
    public static string Describe(int vk, int scan, bool extended, bool modifierOnly)
    {
        var name = KeyName((uint)vk);
        var kind = modifierOnly ? "модификатор" :
            vk switch
            {
                0xAD or 0xAE or 0xAF => "медиа-клавиша (микрофон/громкость)",
                >= 0xB0 and <= 0xB3 => "медиа-клавиша (трек)",
                0x5B or 0x5C => "Win",
                0xA0 or 0xA1 or 0xA2 or 0xA3 => "Ctrl (левая/правая)",
                0xA4 or 0xA5 => "Alt (левый/правый)",
                >= 0x60 and <= 0x6F => "цифровая клавиатура",
                _ => "клавиша",
            };

        var ext = extended ? " · extended" : string.Empty;
        return $"{name} — {kind} · VK 0x{vk:X2} · scan {scan}{ext}";
    }
}
