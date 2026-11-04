namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>Класс размера виджета — от него зависит композиция, а не «что спрятать».</summary>
public enum WidgetSizeClass
{
    /// <summary>Полоска: высоты хватает на одну строку.</summary>
    Strip,

    /// <summary>Маленький: главное число и подпись.</summary>
    Small,

    /// <summary>Обычный: число, подпись и вторая строка данных.</summary>
    Normal,

    /// <summary>Большой: всё, включая детали и вторую колонку.</summary>
    Large,
}

/// <summary>
/// Общий механизм адаптивных виджетов.
///
/// Владелец сформулировал правило на примере плеера: размер виджета определяет не
/// «сколько элементов спрятать», а композицию. Полоса, маленький квадрат и большой
/// прямоугольник — это три разных ответа на вопрос «что здесь главное», а не один
/// каркас с выключенными деталями.
///
/// Поэтому каждый виджет собирает несколько раскладок сразу, а здесь — выбор нужной
/// по размеру и кросс-фейд между ними (мгновенная подмена выглядит как рывок).
/// </summary>
public static class WidgetAdaptive
{
    /// <summary>Порог, ниже которого виджет считается полоской.</summary>
    public const double StripHeight = 58;

    public static WidgetSizeClass Of(double width, double height)
    {
        if (height < StripHeight) return WidgetSizeClass.Strip;
        if (height < 120 || width < 190) return WidgetSizeClass.Small;
        if (height < 235) return WidgetSizeClass.Normal;
        return WidgetSizeClass.Large;
    }

    /// <summary>
    /// Показать нужную раскладку из набора. Панели лежат в одной сетке друг на друге,
    /// поэтому переключение не дёргает размеры окна.
    /// </summary>
    public static void Show(Grid host, UIElement? next, ref object? current)
    {
        if (next is null || ReferenceEquals(current, next)) return;

        // До загрузки окна анимацию прозрачности отыграть некому: элемент остаётся
        // с Opacity = 0 и выглядит пустым. Поэтому до Loaded — переключаем в лоб.
        bool animate = next is FrameworkElement fe && fe.IsLoaded;

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
        }

        current = next;
    }

    /// <summary>Подпись-ключ: мелкий трекинг-шрифт.</summary>
    public static TextBlock Caption(string text)
    {
        var block = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9,
            Foreground = Muted,
            Margin = new Thickness(0, 0, 0, 2),
        };
        return block;
    }

    /// <summary>Крупное число: то, ради чего виджет держат на столе.</summary>
    public static TextBlock BigValue(double size)
    {
        return new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = size,
            FontWeight = FontWeights.Light,
            Foreground = Ink,
            TextWrapping = TextWrapping.NoWrap,
        };
    }

    public static TextBlock Detail(double size = 10)
    {
        return new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = size,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    public static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x93));

    /// <summary>Акцент: берём из палитры, чтобы виджеты жили с темой.</summary>
    public static Brush Accent => new SolidColorBrush(ColorPalette.Accent);
}
