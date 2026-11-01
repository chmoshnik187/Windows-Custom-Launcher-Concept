namespace DotShell.Island;

using Microsoft.UI.Xaml.Data;

/// <summary>true -> Visible, false -> Collapsed. Стандартного конвертера в WinUI нет, пишем свой.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true
            ? Microsoft.UI.Xaml.Visibility.Visible
            : Microsoft.UI.Xaml.Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Microsoft.UI.Xaml.Visibility.Visible;
}

/// <summary>
/// Делает из числа подпись вида "CPU 7%" или "BAT 82%".
/// parameter — префикс. Используется в чипах свёрнутого острова.
/// </summary>
public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var prefix = parameter as string ?? string.Empty;
        double number = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            float f => f,
            _ => 0
        };
        return $"{prefix} {number:F0}%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
