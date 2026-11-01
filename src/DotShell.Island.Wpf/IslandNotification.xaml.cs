namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// Уведомление внутри капсулы острова.
///
/// Показ идёт двумя шагами: капсула сжимается к юниту, и только потом рядом с ним
/// проявляется текст, а капсула разрастается под него. Юнит в это время «читает»:
/// водит взглядом по строке (Units.Reading), а не стоит истуканом.
///
/// Раньше здесь были отдельные треугольники-«зубы» — владелец сказал, что это
/// выглядит чужеродно, потому что зубы должны быть частью существа. Убрали.
/// </summary>
public partial class IslandNotification : UserControl
{
    public IslandNotification()
    {
        InitializeComponent();
    }

    /// <summary>Сколько места нужно тексту (DIP), без юнита и отступов.</summary>
    public double MeasureWidth(string title, string message)
    {
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var messageText = new FormattedText(message ?? string.Empty, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal), 11.5, Brushes.White, pixelsPerDip);

        var titleText = new FormattedText((title ?? string.Empty).ToUpperInvariant(),
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Bahnschrift"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            8, Brushes.White, pixelsPerDip);

        return Math.Max(messageText.Width, titleText.Width) + 2;
    }

    /// <summary>Первый шаг: только юнит, текст убран из раскладки.</summary>
    public void Prepare(string title, string message)
    {
        TitleText.Text = (title ?? string.Empty).ToUpperInvariant();
        MessageText.Text = message ?? string.Empty;

        Visibility = Visibility.Visible;

        // Текст вне раскладки, пока не показан: иначе он держит ширину, и юнит
        // стоит слева от центра вместо центра капсулы.
        Texts.Visibility = Visibility.Collapsed;
        Texts.Opacity = 0;
    }

    /// <summary>Второй шаг: юнит начинает читать, текст проявляется.</summary>
    public void StartReading()
    {
        Faces.Reading = true;
        Texts.Visibility = Visibility.Visible;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        Texts.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>Юнит перестаёт читать: взгляд возвращается к курсору.</summary>
    public void StopReading() => Faces.Reading = false;

    public void HideText() => Texts.BeginAnimation(OpacityProperty,
        new DoubleAnimation(Texts.Opacity, 0, TimeSpan.FromMilliseconds(140)));

    /// <summary>Спрятать уведомление.</summary>
    public void Hide(Action? after = null)
    {
        Visibility = Visibility.Collapsed;
        Faces.Reading = false;
        after?.Invoke();
    }
}