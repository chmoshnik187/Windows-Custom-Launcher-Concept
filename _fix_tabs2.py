import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\IslandWindow.xaml.cs"
t = io.open(p, encoding="utf-8").read()

old = '''        var all = new FrameworkElement[]'''
new = '''        var previous = _visibleTab;

        var all = new FrameworkElement[]'''
assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("previous объявлен")
