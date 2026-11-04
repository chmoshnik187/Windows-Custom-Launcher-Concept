import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetBodies.cs"
t = io.open(p, encoding="utf-8").read()

decl = '''        var compactLamp = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var compactText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 11,
            Foreground = WidgetAdaptive.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

'''

assert decl in t
t = t.replace(decl, "", 1)

anchor = '''        void Refresh()
        {
            Task.Run(() =>'''
new_anchor = decl + '''        void Refresh()
        {
            Task.Run(() =>'''
assert anchor in t
t = t.replace(anchor, new_anchor, 1)

io.open(p, "w", encoding="utf-8").write(t)
print("объявления перенесены выше Refresh")
