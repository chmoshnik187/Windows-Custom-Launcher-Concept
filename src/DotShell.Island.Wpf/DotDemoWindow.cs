namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Витрина «за что отвечают размер точки и зазор»: открывается флагом --dot-demo.
///
/// Одна и та же надпись нарисована при разных значениях: видно, что размер точки —
/// это «толщина карандаша», а зазор — «воздух между точками». Оба параметра
/// влияют на ВСЁ точечное в шелле: часы, заголовок точками, полосы эквалайзера,
/// точечные подписи.
/// </summary>
public sealed class DotDemoWindow : Window
{
    private static readonly (int Cell, int Gap, string Note)[] Combos =
    {
        (2, 0, "cell 2 · gap 0 — самые мелкие и плотные"),
        (2, 2, "cell 2 · gap 2 — мелко, с воздухом"),
        (4, 2, "cell 4 · gap 2 — по умолчанию"),
        (4, 6, "cell 4 · gap 6 — та же точка, больше воздуха"),
        (8, 2, "cell 8 · gap 2 — крупные точки, почти сливаются"),
        (8, 8, "cell 8 · gap 8 — крупные и редкие, «кружево»"),
    };

    public DotDemoWindow()
    {
        Title = "DotShell — размер точки и зазор";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
        Width = 720;
        Height = 880;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        root.Children.Add(new TextBlock
        {
            Text = "ОДНА И ТА ЖЕ НАДПИСЬ ПРИ РАЗНЫХ НАСТРОЙКАХ",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 14),
        });

        foreach (var (cell, gap, note) in Combos)
        {
            root.Children.Add(new TextBlock
            {
                Text = note,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0x6C, 0x78)),
                Margin = new Thickness(0, 10, 0, 4),
            });

            var dots = new DotText
            {
                Text = "DOTSHELL 20:45",
                CellSize = cell,
                Gap = gap,
                SourceFontSize = 30,
                Wave = 0.4,
                DotColor = ColorPalette.Accent,
                GhostColor = Colors.Transparent,
                Height = 46,
                HorizontalAlignment = HorizontalAlignment.Left,
            };

            root.Children.Add(dots);
        }

        root.Children.Add(new TextBlock
        {
            Text = "те же параметры действуют на часы, эквалайзер и точечные подписи",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 18, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        });

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
