namespace DotShell.Island.Wpf;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Weather;

/// <summary>
/// Панель-обзор, выезжающая сверху по центру: часы, календарь, медиа,
/// производительность, погода.
///
/// Почему отдельным окном, а не вкладкой острова: это самостоятельная поверхность
/// в духе Linux-шеллов (AGS/Quickshell) — её открывают на время и закрывают,
/// а остров должен оставаться тонкой полоской.
/// </summary>
public partial class DashboardWindow : Window
{
    private static DashboardWindow? _open;

    private readonly DispatcherTimer _tick;
    private readonly CultureInfo _ru = CultureInfo.GetCultureInfo("ru-RU");

    private DateTime _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private DateTime _lastWeather = DateTime.MinValue;

    private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94));
    private static readonly Brush Faint = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66));
    private static readonly Brush Accent = new SolidColorBrush(ColorPalette.Accent);

    /// <summary>Открыть панель (или закрыть, если уже открыта).</summary>
    public static void Toggle()
    {
        if (_open is not null)
        {
            _open.Close();
            return;
        }

        var window = new DashboardWindow();
        _open = window;
        window.Show();
        window.Activate();
        window.DropIn();
    }

    public DashboardWindow()
    {
        InitializeComponent();

        Foreground = Ink;
        CpuBar.Background = Accent;
        RamBar.Background = Accent;
        Caption.Foreground = Accent;

        PrevMonth.MouseLeftButtonUp += (_, _) => { _month = _month.AddMonths(-1); BuildCalendar(); };
        NextMonth.MouseLeftButtonUp += (_, _) => { _month = _month.AddMonths(1); BuildCalendar(); };

        PrevTrack.MouseLeftButtonUp += (_, _) => _ = Services.Media.PreviousAsync();
        NextTrack.MouseLeftButtonUp += (_, _) => _ = Services.Media.NextAsync();
        PlayPause.MouseLeftButtonUp += async (_, _) => { await Services.Media.TogglePlayAsync(); RefreshMedia(); };

        BuildTabs();
        BuildCalendar();
        RefreshClock();
        RefreshMedia();
        RefreshStats();

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) =>
        {
            RefreshClock();
            RefreshStats();
            RefreshMedia();

            if (DateTime.Now - _lastWeather > TimeSpan.FromMinutes(10)) _ = RefreshWeatherAsync();
        };
        _tick.Start();

        _ = RefreshWeatherAsync();

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        // Закрываем по потере фокуса, но только если фокус вообще был: окно,
        // которое открылось «на секунду» без активации, иначе закрывается сразу.
        bool activated = false;
        Activated += (_, _) => activated = true;
        Deactivated += (_, _) =>
        {
            if (!activated) return;

            // Фокус ушёл к нашему же окну (док, виджет, остров) — не закрываемся.
            // Без этого обзор захлопывался сразу после открытия, стоило доку
            // получить активацию: в аудите окно вообще не появлялось.
            if (ForegroundProcessId() == Environment.ProcessId) return;

            Close();
        };
        Closed += (_, _) =>
        {
            _tick.Stop();
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    private static ShellServices Services => ShellServices.Instance;

    private void BuildTabs()
    {
        Tabs.Children.Clear();

        var all = new (string Id, string Name, Grid Panel)[]
        {
            ("overview", "ОБЗОР", OverviewPanel),
            ("media", "МЕДИА", MediaPanel),
            ("perf", "ПРОИЗВОДИТЕЛЬНОСТЬ", PerfPanel),
            ("weather", "ПОГОДА", WeatherPanel),
        };

        // Состав и порядок вкладок — из настроек: "media,overview" покажет только
        // две и в этом порядке.
        string spec = ShellServices.Instance.Settings.Current.DashboardTabs ?? string.Empty;
        var wanted = spec.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().ToLowerInvariant())
            .ToList();

        var tabs = wanted.Count == 0
            ? all
            : all.Where(tab => wanted.Contains(tab.Id)).OrderBy(tab => wanted.IndexOf(tab.Id)).ToArray();

        if (tabs.Length == 0) tabs = all;

        foreach (var tab in tabs)
        {
            var text = new TextBlock
            {
                Text = tab.Name,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 10,
                Margin = new Thickness(0, 0, 26, 0),
                Cursor = Cursors.Hand,
                Foreground = Faint,
                // Без фона TextBlock ловит клики только по буквам — вкладку было
                // почти невозможно нажать мышью.
                Background = Brushes.Transparent,
                Padding = new Thickness(0, 6, 0, 6),
                Tag = tab.Id,
            };

            text.MouseLeftButtonUp += (_, _) => SelectTab(tab.Id);
            text.MouseEnter += (_, _) => { if (!Equals(text.Tag, _selected)) text.Foreground = Muted; };
            text.MouseLeave += (_, _) => { if (!Equals(text.Tag, _selected)) text.Foreground = Faint; };

            Tabs.Children.Add(text);
        }

        SelectTab(tabs[0].Id);
    }

    private string _selected = "overview";

    private void SelectTab(string id)
    {
        _selected = id;

        foreach (var child in Tabs.Children)
        {
            if (child is not TextBlock text) continue;

            bool active = Equals(text.Tag, id);
            text.Foreground = active ? Ink : Faint;
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        OverviewPanel.Visibility = id == "overview" ? Visibility.Visible : Visibility.Collapsed;
        MediaPanel.Visibility = id == "media" ? Visibility.Visible : Visibility.Collapsed;
        PerfPanel.Visibility = id == "perf" ? Visibility.Visible : Visibility.Collapsed;
        WeatherPanel.Visibility = id == "weather" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RefreshClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm", _ru);
        DateText.Text = now.ToString("dddd, d MMMM", _ru).ToUpperInvariant();

        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        UptimeText.Text = $"{(int)up.TotalDays} д {(int)up.Hours} ч {(int)up.Minutes} мин";
    }

    private void RefreshStats()
    {
        var stats = Services.Stats;

        CpuText.Text = $"{stats.Cpu:0}%";
        CpuBig.Text = $"{stats.Cpu:0}%";
        CpuBar.Width = 300 * Math.Clamp(stats.Cpu / 100.0, 0, 1);

        double total = Math.Max(1, stats.TotalMemoryMb);
        double usedPercent = stats.UsedMemoryMb * 100.0 / total;

        RamText.Text = $"{stats.UsedMemoryMb / 1024.0:0.0} из {total / 1024.0:0.0} ГБ · {usedPercent:0}%";
        RamBig.Text = $"{usedPercent:0}%";
        RamBar.Width = 300 * Math.Clamp(usedPercent / 100.0, 0, 1);

        BatteryText.Text = stats.Battery <= 0
            ? "нет батареи"
            : $"{stats.Battery}%{(stats.Charging ? " · заряжается" : string.Empty)}";
    }

    private void RefreshMedia()
    {
        var media = Services.Media;

        if (!media.HasSession)
        {
            MediaTitle.Text = "Ничего не играет";
            MediaArtist.Text = string.Empty;

            if (OvMediaTitle is not null) OvMediaTitle.Text = "ничего не играет";
            if (OvMediaArtist is not null) OvMediaArtist.Text = "включи что-нибудь — покажу здесь";
            MediaAlbum.Text = string.Empty;
            PlayPause.Text = "▶";
            ProgressText.Text = "— / —";
            Cover.Source = null;
            return;
        }

        MediaTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? "Без названия" : media.Title;
        MediaArtist.Text = media.Artist ?? string.Empty;

        if (OvMediaTitle is not null)
            OvMediaTitle.Text = string.IsNullOrWhiteSpace(media.Title) ? "Без названия" : media.Title;

        if (OvMediaArtist is not null)
            OvMediaArtist.Text = string.IsNullOrWhiteSpace(media.Artist) ? media.SourceApp : media.Artist;
        MediaAlbum.Text = string.IsNullOrWhiteSpace(media.Album) ? media.SourceApp : media.Album;
        PlayPause.Text = media.IsPlaying ? "❚❚" : "▶";

        ProgressText.Text = $"{Format(media.LivePosition)} / {Format(media.Duration)}";

        if (media.ArtworkBytes is { Length: > 0 } bytes)
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                Cover.Source = image;
            }
            catch
            {
                Cover.Source = null;
            }
        }

        static string Format(TimeSpan time) => $"{(int)time.TotalMinutes}:{time.Seconds:00}";
    }

    private async Task RefreshWeatherAsync()
    {
        _lastWeather = DateTime.Now;

        try
        {
            string city = Services.Settings.Current.WeatherCity ?? "Moscow";
            var weather = await WeatherService.GetAsync(city);

            if (weather is null)
            {
                WeatherCity.Text = "ПОГОДА НЕДОСТУПНА";
                return;
            }

            WeatherCity.Text = weather.City.ToUpperInvariant();

            if (OvWeatherTemp is not null) OvWeatherTemp.Text = $"{weather.Temperature:0}°";
            if (OvWeatherCity is not null)
                OvWeatherCity.Text = $"{weather.City} · {WeatherNow.Translate(weather.Condition)}";
            WeatherTemp.Text = $"{weather.Temperature:0}°";
            WeatherCondition.Text = WeatherNow.Translate(weather.Condition);
            WeatherFeels.Text = $"{weather.FeelsLike:0}°";
            WeatherWind.Text = $"{weather.Wind:0.0} м/с";
            WeatherHumidity.Text = $"{weather.Humidity:0}%";
        }
        catch (Exception ex)
        {
            Log.Info($"dashboard: погода не пришла — {ex.Message}");
        }
    }

    private void BuildCalendar()
    {
        MonthText.Text = _month.ToString("MMMM yyyy", _ru).ToUpperInvariant();

        WeekHeader.Children.Clear();
        foreach (var day in new[] { "ПН", "ВТ", "СР", "ЧТ", "ПТ", "СБ", "ВС" })
        {
            WeekHeader.Children.Add(new TextBlock
            {
                Text = day,
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 9,
                Foreground = Faint,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        CalGrid.Children.Clear();

        // Первая ячейка — понедельник той недели, куда попало 1-е число.
        int offset = ((int)_month.DayOfWeek + 6) % 7;
        var start = _month.AddDays(-offset);

        for (int i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            bool otherMonth = date.Month != _month.Month;
            bool today = date.Date == DateTime.Today;

            var cell = new Border
            {
                Margin = new Thickness(2),
                // Штамп, а не «таблетка»: в этом шелле акцент — метка с радиусом 2,
            // скругления 14 px выбивались из языка (тот же принцип, что в плитках).
            CornerRadius = new CornerRadius(2),
                Background = today ? Accent : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = date.Day.ToString(_ru),
                    FontFamily = new FontFamily("Bahnschrift"),
                    FontSize = 13,
                    Foreground = today ? Brushes.White : otherMonth ? Faint : Ink,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            CalGrid.Children.Add(cell);
        }
    }

    /// <summary>Выезд сверху: окно появляется из-за края и проезжает вниз.</summary>
    private void DropIn()
    {
        var area = SystemParameters.WorkArea;

        UiScale.Apply(this, 1000, 560);

        Left = area.Left + (area.Width - Width) / 2;

        // Отступ сверху: иначе раскрытый остров висит поверх шапки обзора и
        // перекрывает вкладки. 68 px — высота свёрнутой капсулы с запасом.
        Top = area.Top + 68;

        // Токены общие: скорость и кривая из настроек теперь влияют и на обзор.
        // Конечная позиция обязана совпадать с Top выше: раньше стояло «18», и
        // окно заканчивало выезд на 18 px от края — поверх свёрнутой капсулы,
        // хотя чуть выше мы явно отодвинули его на 68 px, чтобы НЕ перекрывать остров.
        var slide = new DoubleAnimation(-Height * 0.35, area.Top + 68, TimeSpan.FromMilliseconds(Motion.Ms(Motion.Slow)))
        {
            EasingFunction = Motion.Ease(),
        };

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(Motion.Ms(Motion.Normal)));

        BeginAnimation(TopProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>PID окна, которое сейчас на переднем плане (0 — не удалось).</summary>
    private static uint ForegroundProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;

            GetWindowThreadProcessId(hwnd, out uint pid);
            return pid;
        }
        catch
        {
            return 0;
        }
    }
}
