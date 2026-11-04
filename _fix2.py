import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWindow.cs"
t = io.open(p, encoding="utf-8").read()

old = '''        pillBar.Margin = new Thickness(70, 0, 96, 0);'''
new = '''        // Полоса прогресса идёт через все колонки: раньше она жила в нулевой,
        // и её отступы раздували колонку с обложкой — текст уезжал от картинки.
        pillBar.Margin = new Thickness(70, 0, 16, 0);'''
assert old in t
t = t.replace(old, new, 1)

old2 = '''        pill.Children.Add(pillBar);'''
new2 = '''        Grid.SetColumnSpan(pillBar, 3);
        pill.Children.Add(pillBar);'''
assert old2 in t
t = t.replace(old2, new2, 1)

old3 = '''                    pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 96, 0);'''
new3 = '''                    pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 16, 0);'''
assert old3 in t
t = t.replace(old3, new3, 1)

old4 = '''                pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 96, 0);'''
new4 = '''                pillBar.Margin = new Thickness(pillArtBox.Width + 12, 0, 16, 0);'''
t = t.replace(old4, new4, 1)

io.open(p, "w", encoding="utf-8").write(t)
print("полоса прогресса на все колонки")
