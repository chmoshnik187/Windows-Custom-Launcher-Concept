import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWeather.cs"
t = io.open(p, encoding="utf-8").read()
anchor = '''            WidgetAdaptive.Show(host, next, ref current);'''
add = '''            Log.Info($"weather: relayout {w:0}x{h:0} → {next.GetType().Name}, " +
                     $"children={host.Children.Count}, largeVis={largePanel.Visibility}");
            WidgetAdaptive.Show(host, next, ref current);'''
assert anchor in t
t = t.replace(anchor, add, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("лог в погоде")
