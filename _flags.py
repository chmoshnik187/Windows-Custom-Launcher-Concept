import io

# 1) Program: флаги --widget=<тип> и --widget-size=WxH
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Program.cs"
t = io.open(p, encoding="utf-8").read()

if "OpenWidgetType" not in t:
    anchor = "    /// <summary>--dashboard: открыть панель-обзор при старте (ярлык, проверка вёрстки).</summary>\n    public static bool OpenDashboard { get; private set; }"
    add = anchor + '''

    /// <summary>--widget=media: открыть виджет при старте (снимки, проверка раскладок).</summary>
    public static string? OpenWidgetType { get; private set; }

    /// <summary>--widget-size=520x112: задать размер открываемого виджета.</summary>
    public static (int W, int H)? WidgetSize { get; private set; }'''
    t = t.replace(anchor, add, 1)

    arg_anchor = '            if (arg.Equals("--dashboard", StringComparison.OrdinalIgnoreCase)) { OpenDashboard = true; continue; }'
    arg_add = arg_anchor + '''

            if (arg.StartsWith("--widget=", StringComparison.OrdinalIgnoreCase))
            {
                OpenWidgetType = arg["--widget=".Length..].Trim().ToLowerInvariant();
                continue;
            }

            if (arg.StartsWith("--widget-size=", StringComparison.OrdinalIgnoreCase))
            {
                var parts = arg["--widget-size=".Length..].ToLowerInvariant().Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int h))
                {
                    WidgetSize = (w, h);
                }
                continue;
            }'''
    t = t.replace(arg_anchor, arg_add, 1)
    io.open(p, "w", encoding="utf-8").write(t)
    print("Program: флаги добавлены")

# 2) WidgetsManager: доступ к открытому окну
m = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetsManager.cs"
mt = io.open(m, encoding="utf-8").read()
if "public static WidgetWindow? Get(" not in mt:
    anchor = "    public static bool Has(string type) => Open.ContainsKey(type);"
    mt = mt.replace(anchor, anchor + '''

    /// <summary>Открытое окно виджета (нужно для проверки раскладок и снимков).</summary>
    public static WidgetWindow? Get(string type) => Open.TryGetValue(type, out var window) ? window : null;''', 1)
    io.open(m, "w", encoding="utf-8").write(mt)
    print("WidgetsManager: Get добавлен")

# 3) IslandWindow: открыть виджет из флагов
w = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\IslandWindow.xaml.cs"
wt = io.open(w, encoding="utf-8").read()
if "Program.OpenWidgetType" not in wt:
    anchor = '''        if (Program.OpenDashboard)
        {
            Dispatcher.BeginInvoke(() => DashboardWindow.Toggle());
        }'''
    add = anchor + '''

        if (Program.OpenWidgetType is { } widgetType)
        {
            Dispatcher.BeginInvoke(() =>
            {
                Widgets.WidgetsManager.OpenWidget(widgetType);

                if (Program.WidgetSize is { } size && Widgets.WidgetsManager.Get(widgetType) is { } window)
                {
                    window.Width = size.W;
                    window.Height = size.H;
                }
            });
        }'''
    wt = wt.replace(anchor, add, 1)
    io.open(w, "w", encoding="utf-8").write(wt)
    print("IslandWindow: открытие виджета из флага")
