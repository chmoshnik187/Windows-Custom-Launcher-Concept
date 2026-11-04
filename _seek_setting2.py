import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Controls\PlayerStrip.cs"
t = io.open(p, encoding="utf-8").read()
t = t.replace("DotShell.Core.ShellServices.Instance.Settings.Current.SeekStyle",
              "ShellServices.Instance.Settings.Current.SeekStyle")
io.open(p, "w", encoding="utf-8").write(t)
print("ShellServices поправлен")
