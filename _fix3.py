import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWindow.cs"
t = io.open(p, encoding="utf-8").read()
old = '''                : ratio > 0.95 && h > 230'''
new = '''                : ratio > 0.85 && h > 240'''
assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("порог режима обложки: 0.85")
