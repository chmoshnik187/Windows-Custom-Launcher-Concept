import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\NotificationHub.cs"
t = io.open(p, encoding="utf-8").read()
t = t.replace("DotShell.Core.ShellServices.Instance", "ShellServices.Instance")
io.open(p, "w", encoding="utf-8").write(t)
print("namespace fixed")
