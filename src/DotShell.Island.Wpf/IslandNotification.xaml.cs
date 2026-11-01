namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

/// <summary>
/// Уведомление внутри капсулы острова.
///
/// Показ идёт тремя шагами, а не одним: капсула сначала сжимается к юниту, юнит
/// остаётся в центре, и только потом вокруг него раскрывается пасть, а текст
/// выезжает следом. Раньше зубки двигались, пока юнит стоял на месте — владелец
/// справедливо заметил, что зубы должны быть его, а не отдельные.
///
/// Пасть — два треугольника, которые расходятся вверх и вниз, выходя за края
/// капсулы: слой лежит НАД пилюлей, поэтому её ClipToBounds их не режет.
/// </summary>
public partial class IslandNotification : UserControl
{
    private readonly TranslateTransform? _topShift;
    private readonly TranslateTransform? _bottomShift;

    public IslandNotification()
    {
        InitializeComponent();

        // Трансформы зубков лежат в RenderTransform — XAML-поля для них не
        // создаются, поэтому берём ссылки у самих путей.
        _topShift = MouthTop.RenderTransform as TranslateTransform;
        _bottomShift = MouthBottom.RenderTransform as TranslateTransform;
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

    /// <summary>Первый шаг: только юнит, рот закрыт, текст спрятан.</summary>
    public void Prepare(string title, string message)
    {
        TitleText.Text = (title ?? string.Empty).ToUpperInvariant();
        MessageText.Text = message ?? string.Empty;

        Visibility = Visibility.Visible;

        // Текст убираем из раскладки совсем: иначе он, невидимый, всё равно занимает
        // ширину, и юнит стоит слева от центра вместо центра капсулы.
        Texts.Visibility = Visibility.Collapsed;
        Texts.Opacity = 0;
        SetMouth(open: false, animate: false);
    }

    /// <summary>Второй шаг: пасть раскрывается.</summary>
    public void OpenMouth() => SetMouth(open: true, animate: true);

    /// <summary>Третий шаг: текст проявляется рядом с юнитом.</summary>
    public void ShowText()
    {
        Texts.Visibility = Visibility.Visible;
        Fade(Texts, 1, 220);
    }

    public void HideText() => Fade(Texts, 0, 140);

    public void CloseMouth() => SetMouth(open: false, animate: true);

    /// <summary>Спрятать после закрытия: ждём, пока зубки сойдутся.</summary>
    public void Hide(Action? after = null)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Visibility = Visibility.Collapsed;
            after?.Invoke();
        };
        timer.Start();
    }

    private void SetMouth(bool open, bool animate)
    {
        double target = open ? 17 : 0;

        if (!animate)
        {
            if (_topShift is not null) _topShift.Y = -target;
            if (_bottomShift is not null) _bottomShift.Y = target;
            MouthTop.Opacity = open ? 1 : 0;
            MouthBottom.Opacity = open ? 1 : 0;
            return;
        }

        MoveY(_topShift, -target, 230);
        MoveY(_bottomShift, target, 230);
        Fade(MouthTop, open ? 1 : 0, 160);
        Fade(MouthBottom, open ? 1 : 0, 160);
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
