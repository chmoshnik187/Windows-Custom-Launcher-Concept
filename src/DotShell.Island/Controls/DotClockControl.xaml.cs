namespace DotShell.Island.Controls;

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

/// <summary>
/// Часы из точек: ЧЧ : ММ.
///
/// Одна тонкость, которую легко упустить и получить вечно грузящийся CPU:
/// обновлять текст надо НЕ каждый тик таймера, а только когда сменилась МИНУТА.
/// Рендер точек — операция не бесплатная, и дёргать её 2 раза в секунду смысла нет.
/// Таймер при этом тикает раз в 500 мс — он нужен только для мигания двоеточия.
/// </summary>
public sealed partial class DotClockControl : UserControl
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private string _lastMinutes = string.Empty;
    private bool _separatorOn = true;

    public DotClockControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        Update(force: true);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }

    private void OnUnloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // Обязательно останавливаем таймер: если вкладка/окно закрыты,
        // а таймер жив, мы продолжаем будить процесс и жечь батарею.
        _timer?.Stop();
    }

    private void Update(bool force = false)
    {
        var now = DateTime.Now;
        string hh = now.ToString("HH");
        string mm = now.ToString("mm");

        if (force || mm != _lastMinutes)
        {
            _lastMinutes = mm;
            HoursDots.Text = hh;
            MinutesDots.Text = mm;
        }

        // Мигание двоеточия — дешёвая операция (меняется только Fill), точки не перерисовываются.
        _separatorOn = !_separatorOn;
        var brush = new SolidColorBrush(_separatorOn ? ColorHelper.Accent : ColorHelper.AccentFaded);
        SepTop.Fill = brush;
        SepBottom.Fill = brush;
    }
}
