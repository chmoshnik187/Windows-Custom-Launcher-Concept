namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// Жалюзи-«дверь» для появления острова из панели задач.
///
/// Концепция владельца: интерфейс не просто выезжает, а открывается слоями,
/// как футуристичная металлическая дверь снизу вверх. Здесь три жёсткие плиты
/// с волосяными гранями (без градиентов и свечения — правило вкуса), которые
/// уходят вверх с разной задержкой и открывают содержимое полосами.
/// </summary>
public sealed class TaskbarBlinds : Grid
{
    private readonly Border[] _slats = new Border[3];
    private readonly TranslateTransform[] _moves = new TranslateTransform[3];

    public TaskbarBlinds()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        ClipToBounds = true;

        for (int i = 0; i < _slats.Length; i++)
        {
            var move = new TranslateTransform();
            _moves[i] = move;

            var slat = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x17, 0x1B)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)),
                BorderThickness = new Thickness(0, 1, 0, 0),
                RenderTransform = move,
                VerticalAlignment = VerticalAlignment.Top,
                Height = 1,
            };

            // Металлическая грань: тонкая светлая линия сверху и тёмная снизу.
            slat.Effect = null;
            _slats[i] = slat;
            Children.Add(slat);
        }

        SizeChanged += (_, _) => LayoutSlats();

        Loaded += (_, _) => LayoutSlats();
    }

    private void LayoutSlats()
    {
        double h = Math.Max(12, ActualHeight);
        double each = h / _slats.Length + 0.5;
        for (int i = 0; i < _slats.Length; i++)
        {
            _slats[i].Height = each;
            _slats[i].Margin = new Thickness(0, i * each, 0, 0);
            _slats[i].VerticalAlignment = VerticalAlignment.Top;
        }
    }

    /// <summary>Двери открываются: плиты уходят вверх с задержкой.</summary>
    public void Open()
    {
        if (ActualHeight < 12) return;

        LayoutSlats();
        Visibility = Visibility.Visible;
        double h = ActualHeight + 8;

        for (int i = 0; i < _slats.Length; i++)
        {
            var move = _moves[i];
            move.Y = 0;
            var anim = new DoubleAnimation(-h, TimeSpan.FromMilliseconds(200))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * 60),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            };
            anim.Completed += (_, _) =>
            {
                if (i == _slats.Length - 1) Visibility = Visibility.Collapsed;
            };
            move.BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }

    /// <summary>Двери закрываются — сначала панели, потом схлопывание окна.</summary>
    public void Close()
    {
        if (ActualHeight < 12) return;

        LayoutSlats();
        Visibility = Visibility.Visible;
        double h = ActualHeight + 8;

        for (int i = 0; i < _slats.Length; i++)
        {
            var move = _moves[i];
            move.Y = -h;
            var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(170))
            {
                BeginTime = TimeSpan.FromMilliseconds(i * 50),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };
            move.BeginAnimation(TranslateTransform.YProperty, anim);
        }
    }
}
