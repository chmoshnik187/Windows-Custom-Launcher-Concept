namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Витрина юнита: все настроения и оба персонажа рядом.
///
/// Зачем окно: характер видно только в сравнении. Здесь стоят «спокойный», «рад»,
/// «грустит» и «спит» одновременно — можно посмотреть, как меняются глаза, рот и поза,
/// и подкрутить спрайт, не дожидаясь трёх минут реального игнора.
/// </summary>
public sealed class UnitDemoWindow : Window
{
    public UnitDemoWindow()
    {
        Title = "DotShell — юнит: настроения";
        Width = 620;
        Height = 360;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var card = new Border
        {
            Margin = new Thickness(12),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E)),
            Child = BuildContent(),
        };
        card.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        Content = card;

        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }

    private static UIElement BuildContent()
    {
        var grid = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = "ЮНИТ · НАСТРОЕНИЯ",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 14),
        };
        grid.Children.Add(title);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Grid.SetRow(row, 1);

        (UnitCharacter.Mood Mood, string Label)[] states =
        {
            (UnitCharacter.Mood.Idle, "СПОКОЙНАЯ"),
            (UnitCharacter.Mood.Happy, "РАДА (КЛИК)"),
            (UnitCharacter.Mood.Sad, "ГРУСТИТ"),
            (UnitCharacter.Mood.Sleepy, "СПИТ"),
        };

        foreach (var (mood, label) in states)
        {
            var cell = new StackPanel { Margin = new Thickness(14, 0, 14, 0), Width = 110 };

            foreach (var kind in new[] { UnitCharacter.UnitKind.Goth, UnitCharacter.UnitKind.Skeleton })
            {
                var unit = new UnitCharacter
                {
                    AutoMood = false,
                    CurrentMood = mood,
                    Kind = kind,
                    Width = 56,
                    Height = 62,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 4, 0, 4),
                };

                cell.Children.Add(unit);
            }

            cell.Children.Add(new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 8,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });

            row.Children.Add(cell);
        }

        grid.Children.Add(row);
        return grid;
    }
}
