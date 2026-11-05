import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\ColorPalette.cs"
t = io.open(p, encoding="utf-8").read()
t = t.replace("using DotShell.Core.Theming;",
              "using DotShell.Core.Theming;\nusing DotShell.Island.Wpf.Desktop;")
io.open(p, "w", encoding="utf-8").write(t)
print("using Desktop добавлен")
