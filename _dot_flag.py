import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Program.cs"
t = io.open(p, encoding="utf-8").read()
if "OpenDotDemo" not in t:
    anchor = '''    /// <summary>--slider-demo: витрина стилей ползунков и таймлайна.</summary>'''
    t = t.replace(anchor, '''    /// <summary>--dot-demo: витрина «размер точки и зазор».</summary>
    public static bool OpenDotDemo { get; private set; }

''' + anchor, 1)

    arg = '''            if (arg.Equals("--slider-demo", StringComparison.OrdinalIgnoreCase)) { OpenSliderDemo = true; continue; }'''
    t = t.replace(arg, arg + '''

            if (arg.Equals("--dot-demo", StringComparison.OrdinalIgnoreCase)) { OpenDotDemo = true; continue; }''', 1)
    io.open(p, "w", encoding="utf-8").write(t)
    print("Program: --dot-demo")

p2 = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\IslandWindow.xaml.cs"
t2 = io.open(p2, encoding="utf-8").read()
if "DotDemoWindow" not in t2:
    anchor2 = '''        if (Program.OpenSliderDemo)
        {
            Dispatcher.BeginInvoke(() => new SliderDemoWindow().Show());
        }'''
    t2 = t2.replace(anchor2, anchor2 + '''

        if (Program.OpenDotDemo)
        {
            Dispatcher.BeginInvoke(() => new DotDemoWindow().Show());
        }''', 1)
    io.open(p2, "w", encoding="utf-8").write(t2)
    print("IslandWindow: витрина точек")
