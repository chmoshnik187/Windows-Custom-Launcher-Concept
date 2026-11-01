namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// Отклик на нажатие: плитка слегка проседает и возвращается.
///
/// Зачем: без отклика интерфейс кажется мёртвым — нажал, и непонятно, услышали ли
/// тебя. Анимация короткая (90 мс вниз, 130 мс назад) и на 6% масштаба: её
/// замечаешь пальцем, а не глазом, и она не спорит с минимализмом.
/// </summary>
public static class Tap
{
    public static void Attach(FrameworkElement element)
    {
        var scale = new ScaleTransform(1, 1);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        element.MouseLeftButtonDown += (_, _) => Animate(scale, 0.94, 90);
        element.MouseLeftButtonUp += (_, _) => Animate(scale, 1.0, 130);
        element.MouseLeave += (_, _) => Animate(scale, 1.0, 130);
    }

    private static void Animate(ScaleTransform scale, double to, int ms)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }
}
