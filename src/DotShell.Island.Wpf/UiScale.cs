namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Media;
using DotShell.Core;

/// <summary>
/// Масштаб интерфейса: один множитель для окон, которые не завязаны на пиксельные
/// константы острова (настройки, обзор, центр управления).
///
/// Зачем: на 1080p микро-подписи в 9 px читаются, на 4K без масштаба — нет; и наоборот,
/// на большом мониторе с DPI 100 % интерфейс выглядит мелким. Остров масштабируется
/// своими настройками (высота, радиус, размер точки), а эти окна — общим множителем.
/// </summary>
public static class UiScale
{
    /// <summary>Текущий множитель из настроек, ограниченный безопасным диапазоном.</summary>
    public static double Factor
    {
        get
        {
            var settings = ShellServices.Instance?.Settings.Current;
            if (settings is null) return 1.0;

            return Math.Clamp(settings.InterfaceScale, 0.8, 1.4);
        }
    }

    /// <summary>
    /// Применить масштаб к окну: содержимое растягиваем через LayoutTransform, а окно
    /// увеличиваем пропорционально — иначе содержимое обрежется.
    /// </summary>
    public static void Apply(Window window, double baseWidth, double baseHeight)
    {
        double factor = Factor;
        if (Math.Abs(factor - 1.0) < 0.01) return;

        if (window.Content is FrameworkElement content)
        {
            content.LayoutTransform = new ScaleTransform(factor, factor);
        }

        if (baseWidth > 0) window.Width = baseWidth * factor;
        if (baseHeight > 0) window.Height = baseHeight * factor;
    }

    /// <summary>Применить к окну, у которого размер задаётся содержимым.</summary>
    public static void ApplyToContent(Window window)
    {
        double factor = Factor;
        if (Math.Abs(factor - 1.0) < 0.01) return;

        if (window.Content is FrameworkElement content)
            content.LayoutTransform = new ScaleTransform(factor, factor);
    }
}
