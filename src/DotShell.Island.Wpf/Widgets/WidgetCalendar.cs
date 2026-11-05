namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DotShell.Core;

/// <summary>
/// Календарь месяца в виджете: шапка акцентом, дни недели мелко, сегодня — акцентом.
///
/// Почему отдельным виджетом, а не только в обзоре: календарь нужен на виду весь день,
/// а обзор открывают на время. UniformGrid сам раскладывает 7 колонок под любой размер —
/// от полоски до квадрата.
/// </summary>
public sealed partial class WidgetWindow
{
    private UIElement BuildCalendar()
    {
        var today = DateTime.Today;
        var first = new DateTime(today.Year, today.Month, 1);
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

        var ink = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
        var dim = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66));
        var accent = new SolidColorBrush(ColorPalette.Accent);

        var root = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };

        root.Children.Add(new TextBlock
        {
            Text = $"{culture.DateTimeFormat.GetMonthName(today.Month).ToUpperInvariant()} {today.Year}",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = accent,
        });

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 10, 0, 4) };

        foreach (var name in new[] { "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС" })
        {
            weekdays.Children.Add(new TextBlock
            {
                Text = name,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 8,
                Foreground = dim,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }

        root.Children.Add(weekdays);

        var grid = new UniformGrid { Columns = 7, VerticalAlignment = VerticalAlignment.Top };

        // Понедельник — первый день: смещение от воскресенья пересчитываем вручную.
        int offset = ((int)first.DayOfWeek + 6) % 7;
        for (int i = 0; i < offset; i++) grid.Children.Add(new TextBlock());

        int days = DateTime.DaysInMonth(today.Year, today.Month);

        for (int day = 1; day <= days; day++)
        {
            bool isToday = day == today.Day;
            bool weekend = ((offset + day - 1) % 7) >= 5;

            grid.Children.Add(new TextBlock
            {
                Text = day.ToString(),
                FontFamily = new FontFamily("Bahnschrift"),
                // 10, а не 12: в узком виджете ячейки по ~25 px, и двузначные числа
                // при 12 слипались в «23242526».
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(1, 3, 1, 3),
                Foreground = isToday ? accent : weekend ? dim : ink,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }

        root.Children.Add(grid);
        return root;
    }
}
