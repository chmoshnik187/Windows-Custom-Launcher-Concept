namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DotShell.Core;

/// <summary>
/// Единый язык движения для всех окон, кроме острова (у него своя геометрия).
///
/// Зачем отдельный класс: длительности и кривые были размазаны по файлам — в обзоре
/// 260 мс и CubicEase, в доке 200 мс, в настройках вообще ничего. Настройки
/// «скорость» и «кривая» влияли только на остров, хотя человек ждёт, что они
/// притормозят весь интерфейс.
/// </summary>
public static class Motion
{
    /// <summary>Базовые длительности в миллисекундах (до поправки на скорость).</summary>
    public const int Fast = 120;
    public const int Normal = 200;
    public const int Slow = 260;

    /// <summary>Длительность с поправкой на пользовательскую скорость (0.5–2×).</summary>
    public static int Ms(int baseMs)
    {
        double speed = Speed();
        return (int)Math.Clamp(baseMs / speed, 60, 900);
    }

    /// <summary>Кривая движения из настроек: out / inout / linear / spring.</summary>
    public static IEasingFunction? Ease()
    {
        return Settings()?.AnimationCurve switch
        {
            "linear" => null,
            "inout" => new CubicEase { EasingMode = EasingMode.EaseInOut },
            "spring" => new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut },
            _ => new CubicEase { EasingMode = EasingMode.EaseOut },
        };
    }

    /// <summary>Окно «падает» сверху: сдвиг + проявление. Используется обзором и настройками.</summary>
    public static void DropIn(Window window, double offsetY = -24)
    {
        double start = window.Top;
        double target = start - offsetY;

        window.Opacity = 0;
        window.BeginAnimation(Window.TopProperty, new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(Ms(Slow)))
        {
            EasingFunction = Ease(),
        });

        window.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(Normal))) { EasingFunction = Ease() });
    }

    /// <summary>Проявить элемент (для появления виджетов, панелей, карточек).</summary>
    public static void FadeIn(UIElement element, double from = 0)
    {
        element.Opacity = from;

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(Normal)))
        {
            EasingFunction = Ease(),
        });
    }

    /// <summary>Мягкое «проявление из чуть меньшего масштаба» — для карточек и плиток.</summary>
    public static void PopIn(FrameworkElement element, double scaleFrom = 0.97)
    {
        element.Opacity = 0;

        var scale = new ScaleTransform(scaleFrom, scaleFrom);
        element.RenderTransform = scale;
        element.RenderTransformOrigin = new Point(0.5, 0.5);

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(Normal)))
        {
            EasingFunction = Ease(),
        });

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(Slow)))
        {
            EasingFunction = Ease(),
        });

        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Ms(Slow)))
        {
            EasingFunction = Ease(),
        });
    }

    /// <summary>Анимировать изменение числа (ширина, высота, прозрачность) с общими токенами.</summary>
    public static void Animate(DependencyObject target, DependencyProperty property, double to, int baseMs = Normal)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(Ms(baseMs))) { EasingFunction = Ease() };

        if (target is UIElement element) element.BeginAnimation(property, animation);
        else if (target is Window window) window.BeginAnimation(property, animation);
    }

    private static double Speed()
    {
        var settings = Settings();
        return settings is null ? 1.0 : Math.Clamp(settings.AnimationSpeed, 0.5, 2);
    }

    private static DotShell.Core.Settings.AppSettings? Settings() =>
        ShellServices.Instance?.Settings.Current;
}
