namespace DotShell.Island.Wpf.Controls;

using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// Часы из точек: ЧЧ : ММ.
///
/// Тонкость, которую легко упустить и получить вечно загруженный CPU:
/// текст обновляется НЕ каждый тик таймера, а только когда сменилась МИНУТА.
/// Рендер точек — операция не бесплатная, и дёргать её дважды в секунду смысла нет.
/// Таймер при этом тикает раз в 500 мс — он нужен только для мигания двоеточия,
/// а это смена двух Fill'ов, то есть почти ничего.
/// </summary>
public partial class DotClock : UserControl
{
    private readonly DispatcherTimer _timer;
    private string _lastMinutes = string.Empty;
    private bool _separatorOn = true;
    private bool _paused;

    public DotClock()
    {
        InitializeComponent();

        ApplyPalette(ColorPalette.Accent, ColorPalette.GhostDotColor);
        ColorPalette.PaletteChanged += OnPaletteChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Update();

        Loaded += (_, _) => { Update(force: true); if (!_paused) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    /// <summary>
    /// Остановить тиканье. Вызывается, когда остров свёрнут или спрятан в полноэкранном
    /// режиме: незачем будить процесс ради того, чего никто не видит.
    /// </summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (paused) _timer.Stop();
        else { Update(force: true); _timer.Start(); }
    }

    private void OnPaletteChanged() =>
        Dispatcher.BeginInvoke(() =>
        {
            ApplyPalette(ColorPalette.Accent, ColorPalette.GhostDotColor);
            Update(force: true);
        });

    private void ApplyPalette(Color accent, Color ghost)
    {
        HoursDots.DotColor = Color.FromRgb(0xEC, 0xEC, 0xEE);
        HoursDots.GhostColor = ghost;
        MinutesDots.DotColor = accent;
        MinutesDots.GhostColor = ghost;
    }

    private void Update(bool force = false)
    {
        var now = DateTime.Now;
        string mm = now.ToString("mm");

        if (force || mm != _lastMinutes)
        {
            _lastMinutes = mm;
            HoursDots.Text = now.ToString("HH");
            MinutesDots.Text = mm;
        }

        _separatorOn = !_separatorOn;
        var brush = new SolidColorBrush(_separatorOn ? ColorPalette.Accent : ColorPalette.AccentFaded);
        brush.Freeze();
        SepTop.Fill = brush;
        SepBottom.Fill = brush;
    }
}
