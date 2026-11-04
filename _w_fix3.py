import io
# 1) погода: host не был добавлен в root — виджет пустой
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWeather.cs"
t = io.open(p, encoding="utf-8").read()
old = '''        var host = new Grid();
        host.Children.Add(stripPanel);
        host.Children.Add(smallPanel);
        host.Children.Add(normalPanel);
        host.Children.Add(largePanel);'''
new = '''        var host = new Grid();
        host.Children.Add(stripPanel);
        host.Children.Add(smallPanel);
        host.Children.Add(normalPanel);
        host.Children.Add(largePanel);

        root.Children.Add(host);'''
assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("погода: host добавлен в root")

# 2) часы: широкий вариант не только в Large
p2 = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWindow.cs"
t2 = io.open(p2, encoding="utf-8").read()
old2 = '''            var next = size == WidgetSizeClass.Strip
                ? (UIElement)stripPanel
                : size == WidgetSizeClass.Large && w > h * 1.6
                    ? widePanel
                    : smallPanel;'''
new2 = '''            var next = size == WidgetSizeClass.Strip
                ? (UIElement)stripPanel
                : w > h * 1.55
                    ? widePanel
                    : smallPanel;'''
assert old2 in t2
t2 = t2.replace(old2, new2, 1)
io.open(p2, "w", encoding="utf-8").write(t2)
print("часы: широкий вариант при соотношении > 1.55")
