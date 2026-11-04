import io
# флаг --slider-demo
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Program.cs"
t = io.open(p, encoding="utf-8").read()
if "OpenSliderDemo" not in t:
    anchor = "    /// <summary>--dashboard: открыть панель-обзор при старте (ярлык, проверка вёрстки).</summary>"
    t = t.replace(anchor, '''    /// <summary>--slider-demo: витрина стилей ползунков и таймлайна.</summary>
    public static bool OpenSliderDemo { get; private set; }

''' + anchor, 1)

    arg = '''            if (arg.Equals("--dashboard", StringComparison.OrdinalIgnoreCase)) { OpenDashboard = true; continue; }'''
    t = t.replace(arg, arg + '''

            if (arg.Equals("--slider-demo", StringComparison.OrdinalIgnoreCase)) { OpenSliderDemo = true; continue; }''', 1)
    io.open(p, "w", encoding="utf-8").write(t)
    print("Program: --slider-demo")

# открытие витрины
p2 = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\IslandWindow.xaml.cs"
t2 = io.open(p2, encoding="utf-8").read()
if "SliderDemoWindow" not in t2:
    anchor2 = '''        if (Program.OpenDashboard)
        {
            Dispatcher.BeginInvoke(() => DashboardWindow.Toggle());
        }'''
    t2 = t2.replace(anchor2, anchor2 + '''

        if (Program.OpenSliderDemo)
        {
            Dispatcher.BeginInvoke(() => new SliderDemoWindow().Show());
        }''', 1)
    io.open(p2, "w", encoding="utf-8").write(t2)
    print("IslandWindow: витрина")
