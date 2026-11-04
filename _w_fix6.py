import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetAdaptive.cs"
t = io.open(p, encoding="utf-8").read()
t = t.replace("        bool animate = next.IsLoaded;",
              "        bool animate = next is FrameworkElement fe && fe.IsLoaded;")
io.open(p, "w", encoding="utf-8").write(t)
print("IsLoaded через FrameworkElement")
