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
        var root = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = "\uE753",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        head.Children.Add(icon);
        Grid.SetColumn(icon, 0);

        var city = new TrackedText
        {
            Text = "ПОГОДА",
            FontSize = 8,
            Tracking = 0.28,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        city.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        head.Children.Add(city);
        Grid.SetColumn(city, 1);

        root.Children.Add(head);
        Grid.SetRow(head, 0);

        var value = new TextBlock
        {
            Text = "—",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 32,
            FontWeight = FontWeights.Light,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        value.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        root.Children.Add(value);
        Grid.SetRow(value, 1);

        var details = new TextBlock
        {
            Text = "получаю данные…",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            TextWrapping = TextWrapping.Wrap,
        };
        details.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        root.Children.Add(details);
        Grid.SetRow(details, 2);

        async void Refresh()
        {
            try
            {
                var settings = ShellServices.Instance.Settings;
                var weather = await WeatherService.GetAsync(settings.Current.WeatherCity);
                if (weather is null)
                {
                    value.Text = "—";
                    details.Text = "нет связи с погодой";
                    return;
                }

                value.Text = $"{weather.Temperature:+0;-0;0}°";
                icon.Text = weather.Glyph;
                city.Text = weather.City.ToUpperInvariant();
                details.Text = $"{weather.Condition} · ощущается {weather.FeelsLike:+0;-0;0}°\n" +
                               $"ветер {weather.Wind:F0} м/с · влажность {weather.Humidity:F0}%";
            }
            catch { value.Text = "—"; details.Text = "нет связи с погодой"; }
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

        // Клик по названию города — самый очевидный путь: не надо знать про меню.
        city.Cursor = Cursors.Hand;
        city.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            change.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        };

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
}
