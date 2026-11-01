namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// Уведомление внутри капсулы острова.
///
/// Юнит «раскрывает пасть»: два треугольника расходятся вверх и вниз от центра,
/// выходя за края капсулы, — и текст уведомления появляется между ними. Ширину
/// капсулы просит владелец, контрол только сообщает, сколько места нужно тексту.
///
/// Переносы здесь не нужны: сообщение идёт одной строкой и обрезается, если места
/// не хватило. Переносы в капсуле высотой 46 px превращаются в кашу.
/// </summary>
public partial class IslandNotification : UserControl
{
    // Именованные элементы внутри RenderTransform не попадают в поля XAML —
    // это Freezable, а не визуальный элемент. Берём трансформы по ссылке из Path.
    private readonly TranslateTransform? _topShift;
    private readonly TranslateTransform? _bottomShift;
    public IslandNotification()
    {
        InitializeComponent();

        // Трансформы зубков лежат в RenderTransform — XAML-поля для них не
        // создаются, поэтому берём ссылки у самих путей.
        _topShift = MouthTop.RenderTransform as TranslateTransform;
        _bottomShift = MouthBottom.RenderTransform as TranslateTransform;

        // Пасть в покое: зубки сомкнуты в точку.
        if (_topShift is not null) _topShift.Y = 0;
        if (_bottomShift is not null) _bottomShift.Y = 0;
    }
    /// <summary>Сколько места нужно тексту (DIP), без учёта юнита и отступов.</summary>
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

    /// <summary>Показать текст и открыть пасть.</summary>
    public void Present(string title, string message)
    {

        TitleText.Text = (title ?? string.Empty).ToUpperInvariant();
        MessageText.Text = message ?? string.Empty;
        Visibility = Visibility.Visible;

        MoveY(_topShift, -17, 230);
        MoveY(_bottomShift, 17, 230);
        Fade(MouthTop, 1, 160);
        Fade(MouthBottom, 1, 160);
    }

    /// <summary>Закрыть пасть и спрятать уведомление.</summary>
    public void Dismiss(Action? after = null)
    {
        MoveY(_topShift, 0, 180);
        MoveY(_bottomShift, 0, 180);
        Fade(MouthTop, 0, 160);
        Fade(MouthBottom, 0, 160);

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Visibility = Visibility.Collapsed;
            after?.Invoke();
        };
        timer.Start();
    }

    /// <summary>Двигать зубок: анимация вешается прямо на трансформ, без Storyboard.</summary>
    private static void MoveY(TranslateTransform? target, double to, int milliseconds)
    {
        if (target is null) return;
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        target.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private static void Fade(UIElement target, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        target.BeginAnimation(OpacityProperty, animation);
    }
}