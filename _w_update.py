import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetBodies.cs"
t = io.open(p, encoding="utf-8").read()

# компактный вариант: лампа и статус в одну строку, без кнопок и легенды
old = '''        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(details);

        _relayout = (_, h) => details.Visibility = h >= 190 ? Visibility.Visible : Visibility.Collapsed;

        Refresh();
        Timer(60, Refresh);
        return panel;'''

new = '''        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(details);

        // Полоска: только лампа и статус одной строкой. Кнопки и легенда на высоте
        // в 50 px — это очередь из обрезанных элементов, а не информация.
        var compactLamp = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var compactText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 11,
            Foreground = WidgetAdaptive.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var compact = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        compact.Children.Add(compactLamp);
        compact.Children.Add(compactText);

        var host = new Grid();
        host.Children.Add(compact);
        host.Children.Add(panel);

        object? current = null;

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);
            var next = size == WidgetSizeClass.Strip ? (UIElement)compact : panel;
            WidgetAdaptive.Show(host, next, ref current);
        };

        Refresh();
        Timer(60, Refresh);
        return host;'''

assert old in t
t = t.replace(old, new, 1)

# цвет и текст для компактной строки
old_color = '''                    lamp.Fill = new SolidColorBrush(color);'''
new_color = '''                    lamp.Fill = new SolidColorBrush(color);
                    compactLamp.Fill = new SolidColorBrush(color);'''
assert old_color in t
t = t.replace(old_color, new_color, 1)

old_headline = '''                    meaning.Text = light switch'''
new_headline = '''                    compactText.Text = light switch
                    {
                        UpdateStatus.Light.Red => "НУЖНА ПЕРЕЗАГРУЗКА",
                        UpdateStatus.Light.Yellow => "СТАВИТСЯ СЕЙЧАС",
                        _ => "ВСЁ АКТУАЛЬНО",
                    };

                    meaning.Text = light switch'''
assert old_headline in t
t = t.replace(old_headline, new_headline, 1)

io.open(p, "w", encoding="utf-8").write(t)
print("обновления: полоска + карточка")
