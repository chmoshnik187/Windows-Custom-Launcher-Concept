namespace DotShell.Island.Wpf.Controls;

using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using DotShell.Core;

/// <summary>
/// Точечный прогресс плеера: ряд точек, прошедшие — акцентом, впереди — приглушённые,
/// на текущей позиции — крупная точка-«иголка».
///
/// Раньше в виджете плеера была системная ProgressBar высотой 2 px — владелец
/// справедливо назвал это тонкой линией без характера. Здесь тот же материал,
/// что и во всём шелле: точки. Значение API совместимо с ProgressBar (Value 0..100),
/// чтобы старый код обновления не переписывать целиком.
/// </summary>
public sealed class DotProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(DotProgressBar),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(DotProgressBar),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DotCountProperty = DependencyProperty.Register(
        nameof(DotCount), typeof(int), typeof(DotProgressBar),
        new FrameworkPropertyMetadata(32, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(DotProgressBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(DotProgressBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int DotCount
    {
        get => (int)GetValue(DotCountProperty);
        set => SetValue(DotCountProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    public Brush? Track
    {
        get => (Brush?)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public DotProgressBar()
    {
        Height = 10;
        SnapsToDevicePixels = true;
    }

    /// <summary>
    /// UIA-пир: аудит плеера ищет ControlType.ProgressBar и читает RangeValuePattern.
    /// Без пира точечный прогресс для проверок невидим (и для скринридеров тоже).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new DotProgressBarPeer(this);

    private sealed class DotProgressBarPeer : FrameworkElementAutomationPeer, IRangeValueProvider
    {
        public DotProgressBarPeer(DotProgressBar owner) : base(owner) { }

        private DotProgressBar Bar => (DotProgressBar)Owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        protected override string GetClassNameCore() => "DotProgressBar";

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

        public double Value => Bar.Value;
        public bool IsReadOnly => true;
        public double Maximum => Bar.Maximum;
        public double Minimum => 0;
        public double LargeChange => 10;
        public double SmallChange => 1;
        public void SetValue(double value) => throw new InvalidOperationException("progress is read-only");
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 8 || h < 2) return;

        double progress = Maximum <= 0 ? 0 : Math.Clamp(Value / Maximum, 0, 1);

        int count = Math.Clamp(DotCount, 6, 120);
        // На узкой полосе точек меньше, но не мельче 4 px друг от друга.
        count = Math.Min(count, Math.Max(6, (int)(w / 5)));

        double step = w / count;
        double cy = h / 2;
        double r = Math.Min(step * 0.34, h * 0.42);
        if (r < 0.7) r = 0.7;

        var accent = Accent ?? new SolidColorBrush(ColorPalette.Accent);
        var track = Track ?? new SolidColorBrush(Color.FromArgb(0x55, 0x8A, 0x8A, 0x93));
        if (accent.CanFreeze && !accent.IsFrozen) accent.Freeze();
        if (track.CanFreeze && !track.IsFrozen) track.Freeze();

        int filled = (int)Math.Round(progress * count);
        int head = Math.Clamp(filled, 0, count - 1);

        for (int i = 0; i < count; i++)
        {
            double cx = i * step + step / 2;
            bool done = i < filled;
            dc.DrawEllipse(done ? accent : track, null, new Point(cx, cy), r, r);
        }

        // «Иголка»: крупная точка на текущей позиции — читается лучше, чем кончик линии.
        if (filled < count)
        {
            double hx = head * step + step / 2;
            dc.DrawEllipse(accent, null, new Point(hx, cy), r * 1.55, r * 1.55);
        }
    }
}
