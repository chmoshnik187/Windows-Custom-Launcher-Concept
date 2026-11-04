import io
p = r"C:\Users\maxim\Desktop\рабочая зона\dotshell\src\DotShell.Island.Wpf\Widgets\WidgetAdaptive.cs"
t = io.open(p, encoding="utf-8").read()

old = '''        foreach (var child in host.Children)
        {
            if (ReferenceEquals(child, next) || child is not UIElement element) continue;
            if (element.Visibility != Visibility.Visible) continue;

            var fading = element;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            animation.Completed += (_, _) =>
            {
                fading.Visibility = Visibility.Collapsed;
                fading.Opacity = 1;
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        next.Visibility = Visibility.Visible;
        next.Opacity = 0;
        next.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));'''

new = '''        // До загрузки окна анимацию прозрачности отыграть некому: элемент остаётся
        // с Opacity = 0 и выглядит пустым. Поэтому до Loaded — переключаем в лоб.
        bool animate = next.IsLoaded;

        foreach (var child in host.Children)
        {
            if (ReferenceEquals(child, next) || child is not UIElement element) continue;
            if (element.Visibility != Visibility.Visible) continue;

            if (!animate)
            {
                element.Visibility = Visibility.Collapsed;
                element.Opacity = 1;
                continue;
            }

            var fading = element;
            var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
            animation.Completed += (_, _) =>
            {
                fading.Visibility = Visibility.Collapsed;
                fading.Opacity = 1;
            };
            element.BeginAnimation(UIElement.OpacityProperty, animation);
        }

        next.Visibility = Visibility.Visible;

        if (!animate)
        {
            next.BeginAnimation(UIElement.OpacityProperty, null);
            next.Opacity = 1;
        }
        else
        {
            next.Opacity = 0;
            next.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        }'''

assert old in t
t = t.replace(old, new, 1)
io.open(p, "w", encoding="utf-8").write(t)
print("Show: без анимации до загрузки")
