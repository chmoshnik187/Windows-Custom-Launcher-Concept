namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Витрина стилей ползунков и таймлайна: открывается флагом --slider-demo.
///
/// Нужна по двум причинам: владелец выбирает стиль глазами, а не по описанию, и все
/// одиннадцать вариантов можно сравнить в одном окне одновременно.
/// </summary>
public sealed class SliderDemoWindow : Window
{
    private static readonly (string Id, string Title, string Kind)[] Variants =
    {
        ("beads",    "1. Бусины — точки, прошедшее плотнее",                    "seek"),
        ("rail",     "2. Рельс и иголка — линия с вертикальной иглой",           "seek"),
        ("segments", "4. Деления ресивера — 24 сегмента",                        "seek"),
        ("wave",     "5. Волна трека — псевдо-волноформа",                       "seek"),
        ("sand",     "7. Пески — прошедшее осыпается вниз",                      "seek"),
        ("arc",      "8. Дуга — круговой прогресс",                              "seek"),
        ("pulse",    "10. Пульс — бусины, дышащие с басом",                      "seek"),
        ("cassette", "11. Кассета — две катушки и лента",                        "seek"),
        ("capsule",  "12. Капсула — закруглённая заливка (ползунок)",            "slider"),
        ("blocks",   "13. Блоки — 20 квадратов (ползунок)",                      "slider"),
        ("dots",     "14. Точки с хвостом (ползунок)",                           "slider"),
    };

    public SliderDemoWindow()
    {
        Title = "DotShell — стили ползунков";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x0E));
        ShowInTaskbar = true;
        Width = 640;
        Height = 1000;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };

        root.Children.Add(new TextBlock
        {
            Text = "ТАЙМЛАЙН ТРЕКА",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
            Margin = new Thickness(0, 0, 0, 10),
        });

        foreach (var (id, title, kind) in Variants)
        {
            if (kind == "slider" && root.Children.Count > 0 && !_sliderHeaderAdded)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "ПОЛЗУНКИ: ГРОМКОСТЬ, ЯРКОСТЬ, РАЗМЕР ТОЧКИ",
                    FontFamily = new FontFamily("Bahnschrift"),
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93)),
                    Margin = new Thickness(0, 22, 0, 10),
                });
                _sliderHeaderAdded = true;
            }

            root.Children.Add(new TextBlock
            {
                Text = title,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xD0)),
                Margin = new Thickness(0, 8, 0, 2),
            });

            var bar = new DotSeekBar
            {
                Variant = id,
                Value = 42,
                Duration = TimeSpan.FromSeconds(210),
                Height = kind == "seek" && id == "arc" ? 92 : 26,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                IsHitTestVisible = true,
            };

            root.Children.Add(bar);
        }

        Content = new ScrollViewer
        {
            Content = root,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        Loaded += (_, _) => FadeIn();
    }

    private bool _sliderHeaderAdded;

    private void FadeIn()
    {
        Opacity = 0;
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }
}
