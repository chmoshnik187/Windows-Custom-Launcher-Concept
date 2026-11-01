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
    private readonly RectangleGeometry _clip = new();

    public IslandNotification()
    {
        InitializeComponent();

        // Текст не проявляется сам по себе — он уже лежит в капсуле и обрезается
        // по её текущей ширине. Раскрывается капсула — «проступают» буквы, начиная
        // с середины. Владелец: «пусть буквы появляются только внутри окна».
        Texts.Clip = _clip;
        SizeChanged += (_, _) => ApplyClip(_clipWidth);
        Texts.SizeChanged += (_, _) => ApplyClip(_clipWidth);
    }

    private double _clipWidth = 84;

    /// <summary>Видимая ширина текста: ровно ширина капсулы в этот кадр.</summary>
    public void SetVisibleWidth(double width)
    {
        _clipWidth = Math.Max(0, width);

        // Ширина слоя идёт за капсулой: юнит остаётся в её центре, а текст
        // проступает справа, ровно по краю раскрывающегося окна.
        Width = _clipWidth;
        ApplyClip(_clipWidth);
    }

    /// <summary>
    /// Обрезка текста по ширине капсулы. Юнит при этом не трогаем: он должен быть
    /// виден всегда, а буквы проступают из середины, будто окно раздвигается.
    /// </summary>
    private void ApplyClip(double width)
    {
        if (Texts.ActualWidth <= 2 || Texts.ActualHeight <= 2) return;

        // Из ширины капсулы вычитаем место юнита и отступы — остаётся окно текста.
        double available = Math.Max(0, width - 78);
        double w = Math.Min(available, Texts.ActualWidth);
        double left = (Texts.ActualWidth - w) / 2;

        _clip.Rect = new Rect(left, -4, Math.Max(0.1, w), Texts.ActualHeight + 8);
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
        Texts.Visibility = Visibility.Visible;
        Texts.Opacity = 1;

        // Ширину текста фиксируем по содержимому: иначе при узком слое он схлопнется
        // в многоточие вместо того, чтобы проступать по краю.
        Texts.Width = MeasureWidth(title, message) + 4;
        ApplyClip(_clipWidth);
    }

    /// <summary>Второй шаг: юнит начинает читать, текст проявляется.</summary>
    /// <summary>Юнит начинает читать; текст уже виден через обрезку по ширине.</summary>
    public void StartReading() => Faces.Reading = true;

    /// <summary>Юнит перестаёт читать: взгляд возвращается к курсору.</summary>
    public void StopReading() => Faces.Reading = false;

    public void HideText() { }

    /// <summary>Спрятать уведомление.</summary>
    public void Hide(Action? after = null)
    {
        Visibility = Visibility.Collapsed;
        Faces.Reading = false;
        after?.Invoke();
    }
}