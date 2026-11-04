import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetWindow.cs"
t = io.open(p, encoding="utf-8").read()

# --- система: у большого варианта свои дорожки ---
old_bars = '''        // Большой: те же дорожки плюс абсолютные значения и батарея.'''
new_bars = '''        // Большой: свои дорожки (элемент не может жить в двух панелях сразу —
        // иначе «уже логический дочерний для другого элемента»).
        var cpuBarLarge = new DotBar { Height = 16 };
        var ramBarLarge = new DotBar { Height = 16, Margin = new Thickness(0, 0, 0, 0) };'''
assert old_bars in t
t = t.replace(old_bars, new_bars, 1)

t = t.replace('''        largePanel.Children.Add(Label("CPU"));
        largePanel.Children.Add(cpuBar);''', '''        largePanel.Children.Add(Label("CPU"));
        largePanel.Children.Add(cpuBarLarge);''', 1)
t = t.replace('''        largePanel.Children.Add(ramBar);''', '''        largePanel.Children.Add(ramBarLarge);''', 1)

t = t.replace('''            cpuBar.Value = cpu;
            ramBar.Value = ram;''', '''            cpuBar.Value = cpu;
            ramBar.Value = ram;
            cpuBarLarge.Value = cpu;
            ramBarLarge.Value = ram;''', 1)

t = t.replace('''            cpuBar.Height = ramBar.Height = size == WidgetSizeClass.Large ? 16 : 12;''',
'''            cpuBar.Height = ramBar.Height = 12;''', 1)

# --- юниты: у большого варианта своя пара ---
old_units = '''        var largePanel = new Grid();
        largePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        largePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        largePanel.Children.Add(two);'''
new_units = '''        var twoLarge = new Controls.Units { Margin = new Thickness(6), Interactive = true };

        var largePanel = new Grid();
        largePanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        largePanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        largePanel.Children.Add(twoLarge);'''
assert old_units in t
t = t.replace(old_units, new_units, 1)

io.open(p, "w", encoding="utf-8").write(t)
print("дорожки и юниты разведены по панелям")
