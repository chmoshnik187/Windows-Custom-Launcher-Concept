namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core;
using DotShell.Core.Weather;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Виджет погоды: температура крупно, состояние словами, «ощущается» и ветер.
///
/// Данные — Open-Meteo: без ключа, без регистрации, без рекламы. Город берётся из
/// настроек, меняется правым кликом по виджету. Пока данных нет, показываем «—»,
/// а не выдуманные градусы: врать в виджете хуже, чем молчать.
/// </summary>
public sealed partial class WidgetWindow
{
    private UIElement BuildWeather()
    {
        // Четыре размера — четыре композиции. Полоска показывает только знак и
        // температуру, маленький квадрат добавляет город, обычный — состояние,
        // большой разводит температуру и детали по двум колонкам.
        var root = new Grid { Margin = new Thickness(4, 2, 4, 2) };

        var iconStrip = WeatherIcon(20);
        var tempStrip = WidgetAdaptive.BigValue(26);
        tempStrip.VerticalAlignment = VerticalAlignment.Center;
        tempStrip.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        tempStrip.Margin = new Thickness(8, 0, 0, 0);

        var stripPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        stripPanel.Children.Add(iconStrip);
        stripPanel.Children.Add(tempStrip);

        var iconSmall = WeatherIcon(18);
        var citySmall = CityCaption();
        var tempSmall = WidgetAdaptive.BigValue(32);
        tempSmall.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");

        var smallPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        smallPanel.Children.Add(iconSmall);
        smallPanel.Children.Add(citySmall);
        tempSmall.Margin = new Thickness(0, 2, 0, 0);
        smallPanel.Children.Add(tempSmall);

        var iconNormal = WeatherIcon(18);
        var cityNormal = CityCaption();
        var tempNormal = WidgetAdaptive.BigValue(34);
        tempNormal.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        var condition = WidgetAdaptive.Detail(10);

        var normalPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        normalPanel.Children.Add(iconNormal);
        normalPanel.Children.Add(cityNormal);
        tempNormal.Margin = new Thickness(0, 2, 0, 2);
        normalPanel.Children.Add(tempNormal);
        normalPanel.Children.Add(condition);

        // Большой: слева температура, справа мелкие данные — иначе в высоком
        // виджете всё липнет к верху, а снизу пустая простыня.
        var iconLarge = WeatherIcon(22);
        var cityLarge = CityCaption();
        var tempLarge = WidgetAdaptive.BigValue(52);
        tempLarge.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(iconLarge);
        left.Children.Add(cityLarge);
        left.Children.Add(tempLarge);

        var conditionLarge = WidgetAdaptive.Detail(12);
        var detailsLarge = WidgetAdaptive.Detail(10);
        detailsLarge.Margin = new Thickness(0, 8, 0, 0);

        var right = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
        };
        right.Children.Add(conditionLarge);
        right.Children.Add(detailsLarge);

        var largePanel = new Grid();
        largePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        largePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        largePanel.Children.Add(left);
        Grid.SetColumn(right, 1);
        largePanel.Children.Add(right);

        var host = new Grid();
        host.Children.Add(stripPanel);
        host.Children.Add(smallPanel);
        host.Children.Add(normalPanel);
        host.Children.Add(largePanel);

        root.Children.Add(host);

        object? current = null;

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);
            var next = size switch
            {
                WidgetSizeClass.Strip => (UIElement)stripPanel,
                WidgetSizeClass.Small => smallPanel,
                WidgetSizeClass.Normal => normalPanel,
                _ => largePanel,
            };
            Log.Info($"weather: relayout {w:0}x{h:0} → {next.GetType().Name}, " +
                     $"children={host.Children.Count}, largeVis={largePanel.Visibility}");
            WidgetAdaptive.Show(host, next, ref current);
        };

        // Единственное общее место: сюда сходятся все варианты, чтобы не
        // дублировать обновление четырёх наборов текста.
        void SetData(string temp, string city, string state, string sub)
        {
            tempStrip.Text = temp;
            tempSmall.Text = temp;
            tempNormal.Text = temp;
            tempLarge.Text = temp;

            citySmall.Text = city;
            cityNormal.Text = city;
            cityLarge.Text = city;

            condition.Text = state;
            conditionLarge.Text = state;
            detailsLarge.Text = sub;
        }

        async void Refresh()
        {
            try
            {
                var settings = ShellServices.Instance.Settings;
                var weather = await WeatherService.GetAsync(settings.Current.WeatherCity);

                if (weather is null)
                {
                    SetData("—", "ПОГОДА", "нет связи", string.Empty);
                    return;
                }

                var glyph = weather.Glyph;
                iconStrip.Text = iconSmall.Text = iconNormal.Text = iconLarge.Text = glyph;

                string temp = $"{weather.Temperature:+0;-0;0}°";
                string city = weather.City.ToUpperInvariant();
                string state = weather.Condition;

                string sub = $"ощущается {weather.FeelsLike:+0;-0;0}°\n" +
                             $"ветер {weather.Wind:F0} м/с · влажность {weather.Humidity:F0}%";

                SetData(temp, city, state, sub);
            }
            catch
            {
                SetData("—", "ПОГОДА", "нет связи", string.Empty);
            }
        }

        var menu = new ContextMenu();
        var change = new MenuItem { Header = "Сменить город…" };
        change.Click += (_, _) =>
        {
            var settings = ShellServices.Instance.Settings;
            var prompt = new TextPromptWindow("Город", settings.Current.WeatherCity, entered =>
            {
                if (string.IsNullOrWhiteSpace(entered)) return;
                settings.Update(s => s.WeatherCity = entered.Trim());
                Refresh();
            });
            prompt.ShowDialog();
        };
        menu.Items.Add(change);
        menu.Items.Add(new Separator());
        menu.Items.Add(RemoveWidgetItem());

        // Клик по названию города — самый очевидный путь: не надо знать про меню.
        // Подписчиков вешаем на все варианты: виден всегда только один, но
        // какой именно — зависит от размера.
        foreach (var caption in new[] { citySmall, cityNormal, cityLarge })
        {
            caption.Cursor = Cursors.Hand;
            caption.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                change.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            };
        }

        root.MouseRightButtonUp += (_, e) =>
        {
            menu.PlacementTarget = root;
            menu.IsOpen = true;
            e.Handled = true;
        };

        Refresh();
        Timer(15 * 60 * 1000, Refresh);

        return root;
    }

    private static TextBlock WeatherIcon(double size) => new()
    {
        Text = "\uE753",
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = WidgetAdaptive.Accent,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TrackedText CityCaption()
    {
        var caption = new TrackedText
        {
            Text = "ПОГОДА",
            FontSize = 8,
            Tracking = 0.28,
            Margin = new Thickness(0, 4, 0, 0),
        };
        caption.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        return caption;
    }
}
