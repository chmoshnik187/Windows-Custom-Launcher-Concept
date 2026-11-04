import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\Units.cs"
t = io.open(p, encoding="utf-8").read()

if "public bool TwoUnits" not in t:
    anchor = "    public bool Interactive { get; set; }"
    t = t.replace(anchor, anchor + '''

    /// <summary>
    /// Два существа или одно. На узком виджете два не помещаются — владелец
    /// сравнивал с плеером: размер меняет композицию, а не обрезает её.
    /// </summary>
    public bool TwoUnits { get; set; } = true;''', 1)

old_draw = '''        DrawUnit(dc, body, eye, new Point(ActualWidth * 0.27, ActualHeight / 2 + bounce), blinking);
        DrawUnit(dc, body, eye, new Point(ActualWidth * 0.73, ActualHeight / 2 + bounce), blinking);'''

new_draw = '''        if (TwoUnits)
        {
            DrawUnit(dc, body, eye, new Point(ActualWidth * 0.27, ActualHeight / 2 + bounce), blinking);
            DrawUnit(dc, body, eye, new Point(ActualWidth * 0.73, ActualHeight / 2 + bounce), blinking);
        }
        else
        {
            DrawUnit(dc, body, eye, new Point(ActualWidth * 0.5, ActualHeight / 2 + bounce), blinking);
        }'''

assert old_draw in t
t = t.replace(old_draw, new_draw, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("Units.TwoUnits добавлен")
