namespace DotShell.Island;

using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Island.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

/// <summary>
/// Code-behind главной страницы острова.
/// Здесь только "нажать -> сделать": никакой бизнес-логики, она вся в ядре и во ViewModel.
/// </summary>
public sealed partial class MainPage : Page
{
    public IslandViewModel VM { get; }

    private readonly ShellServices _services;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _uiTimer = null!;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _searchDebounce = null!;
    private bool _expanded;

    // Пресеты акцента. В Фазе 2 они переедут в theme.json и будут приходить из темы.
    private static readonly (string Name, string Hex)[] Presets =
    {
        ("NOTHING", "#D71920"),
        ("AMBER",   "#FF9F1C"),
        ("TOXIC",   "#B8FF2E"),
        ("ICE",     "#4CC9F0"),
        ("VIOLET",  "#9B5CFF"),
        ("MONO",    "#E8E8EA"),
    };

    public MainPage(ShellServices services)
    {
        _services = services;
        VM = new IslandViewModel(services);

        InitializeComponent();
        DataContext = VM;

        BuildAccentPresets();
        WireMedia();
        StartUiTimer();
        SelectTab("notes", fromUser: false);

        // Остров обязан реагировать на то, что пользователь ушёл в игру или видео.
        _services.WindowWatcher.FullscreenChanged += OnFullscreenChanged;
    }

    // ================= раскрытие / сворачивание =================

    private void OnPillTapped(object sender, TappedRoutedEventArgs e)
    {
        // Клики по кнопкам, полям ввода и спискам не должны сворачивать остров.
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        SetExpanded(!_expanded);
        e.Handled = true;
    }

    public void SetExpanded(bool value)
    {
        if (_expanded == value) return;
        _expanded = value;
        VM.IsExpanded = value;

        if (value)
        {
            ExpandStory.Begin();
            Eq.IsRunning = VM.ActiveTab == "eq";
        }
        else
        {
            CollapseStory.Begin();
            Eq.IsRunning = false; // не жжём CPU, когда острова не видно
            VM.SaveNote();
        }
    }

    /// <summary>
    /// Проверка "не кликнули ли мы в интерактивный контрол".
    /// Идём вверх по дереву от источника события: Button и TextBox сами помечают
    /// событие обработанным, но Tapped всё равно может всплыть до Border.
    /// </summary>
    private static bool IsInteractive(DependencyObject? source)
    {
        var node = source;
        int guard = 0;
        while (node is not null && guard++ < 40)
        {
            switch (node)
            {
                case Button:
                case ToggleButton:
                case ToggleSwitch:
                case TextBox:
                case ListView:
                case Slider:
                case ScrollViewer:
                case ComboBox:
                    return true;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    // ================= вкладки =================

    private void OnTabClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag }) SelectTab(tag, fromUser: true);
    }

    /// <summary>Открыть вкладку извне (например, по глобальной горячей клавише).</summary>
    public void OpenTab(string tab) => SelectTab(tab, fromUser: true);

    private void SelectTab(string tab, bool fromUser)
    {
        VM.ActiveTab = tab;

        TabNotes.Visibility = tab == "notes" ? Visibility.Visible : Visibility.Collapsed;
        TabClipboard.Visibility = tab == "clipboard" ? Visibility.Visible : Visibility.Collapsed;
        TabEq.Visibility = tab == "eq" ? Visibility.Visible : Visibility.Collapsed;
        TabSystem.Visibility = tab == "system" ? Visibility.Visible : Visibility.Collapsed;
        TabTheme.Visibility = tab == "theme" ? Visibility.Visible : Visibility.Collapsed;

        // Анимацию эквалайзера держим включённой только пока вкладка видна.
        Eq.IsRunning = _expanded && tab == "eq";

        if (fromUser)
        {
            SetExpanded(true);
            if (tab == "clipboard") ClipSearch.Focus(FocusState.Programmatic);
            if (tab == "notes") NoteBox.Focus(FocusState.Programmatic);
        }

        RefreshTabButtons(tab);
    }

    private void RefreshTabButtons(string active)
    {
        for (int i = 0; i < TabsGrid.Children.Count; i++)
        {
            if (TabsGrid.Children[i] is not Button button) continue;
            bool isActive = button.Tag as string == active;
            button.Background = isActive
                ? new SolidColorBrush(ColorHelper.Accent)
                : new SolidColorBrush(Colors.Transparent);
            button.Foreground = isActive
                ? new SolidColorBrush(Colors.Black)
                : (SolidColorBrush)Application.Current.Resources["DsTextMuted"];
            button.BorderBrush = isActive
                ? new SolidColorBrush(ColorHelper.Accent)
                : (SolidColorBrush)Application.Current.Resources["DsBorder"];
        }
    }

    // ================= плеер =================

    private void WireMedia()
    {
        _services.Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Core.Media.MediaBridge.IsPlaying))
                PlayGlyph.Glyph = _services.Media.IsPlaying ? "\uE769" : "\uE768";
            if (e.PropertyName == nameof(Core.Media.MediaBridge.ArtworkBytes))
                UpdateArtwork();
        };
        UpdateArtwork();
    }

    /// <summary>
    /// Обложка альбома. Пока просто показываем её как есть.
    /// В Фазе 2 здесь же прогоняем битмап через DotGrid.Sample -> DotGrid.Rasterize,
    /// и обложка становится точечной (в прототипе prototype.html это видно).
    /// </summary>
    private void UpdateArtwork()
    {
        var bytes = _services.Media.ArtworkBytes;
        if (bytes is null || bytes.Length == 0)
        {
            ArtworkImage.Source = null;
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            using var ms = new System.IO.MemoryStream(bytes);
            bitmap.SetSource(ms.AsRandomAccessStream());
            ArtworkImage.Source = bitmap;
        }
        catch
        {
            ArtworkImage.Source = null;
        }
    }

    private async void OnPlayClick(object sender, RoutedEventArgs e) => await _services.Media.TogglePlayAsync();
    private async void OnNextClick(object sender, RoutedEventArgs e) => await _services.Media.NextAsync();
    private async void OnPrevClick(object sender, RoutedEventArgs e) => await _services.Media.PreviousAsync();

    // ================= буфер обмена =================

    private void OnClipboardSearch(object sender, TextChangedEventArgs e)
    {
        // Дебаунс: не дёргаем поиск на каждую букву мгновенно, а ждём 150 мс тишины.
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnClearClipboard(object sender, RoutedEventArgs e)
    {
        VM.ClearClipboard();
        VM.StatusLine = "буфер очищен (закреплённые остались)";
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ClipboardItem item })
        {
            VM.TogglePin(item);
            VM.StatusLine = item.Pinned ? "закреплено" : "откреплено";
        }
    }

    private void OnClipboardItemUse(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipboardItem item) return;

        VM.UseClipboardItem(item);
        SetExpanded(false);

        // Даём системе вернуть фокус тому окну, где работал пользователь,
        // и только потом жмём Ctrl+V. Без задержки вставка иногда улетает
        // в никуда: фокус ещё не успел переключиться.
        _ = PasteAfterDelayAsync();
    }

    private async Task PasteAfterDelayAsync()
    {
        await Task.Delay(140);
        DispatcherQueue.TryEnqueue(() => SendKeys.Paste());
    }

    // ================= заметки =================

    private void OnNoteLostFocus(object sender, RoutedEventArgs e)
    {
        VM.SaveNote();
        VM.StatusLine = "заметка сохранена";
    }

    // ================= тема =================

    private void BuildAccentPresets()
    {
        AccentPresets.Items.Clear();
        foreach (var (name, hex) in Presets)
        {
            var button = new Button
            {
                Content = name,
                Tag = hex,
                FontSize = 9,
                Padding = new Thickness(9, 6, 9, 6),
                CornerRadius = new CornerRadius(999),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(Core.Theming.ThemeBus.ParseHex(hex))),
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(Core.Theming.ThemeBus.ParseHex(hex)))
            };
            button.Click += OnPresetClick;
            AccentPresets.Items.Add(button);
        }
    }

    private void OnPresetClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string hex }) return;

        // Ручной выбор цвета обязан выключить авто-подбор, иначе обои перекрасят всё обратно.
        _services.Settings.Update(s =>
        {
            s.AutoAccent = false;
            s.AccentHex = hex;
        });
        _services.Theme.SetAccent(Core.Theming.ThemeBus.ParseHex(hex), "preset");
        VM.StatusLine = $"акцент: {hex} (авто-цвет выключен)";
        RefreshTabButtons(VM.ActiveTab);
    }

    // ================= прочее =================

    private void StartUiTimer()
    {
        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(150);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => VM.SearchClipboard(ClipSearch.Text);

        // Раз в секунду: двигаем прогресс трека, обновляем подписи возраста и строку отладки.
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromSeconds(1);
        _uiTimer.Tick += (_, _) =>
        {
            _services.Media.Tick();
            var media = _services.Media;
            if (media.Duration > TimeSpan.Zero)
                Progress.Value = Math.Clamp(media.Position.TotalSeconds / media.Duration.TotalSeconds * 100, 0, 100);

            foreach (var item in VM.ClipboardItems) item.RefreshAge();

            StatsLine.Text =
                $"CPU {_services.Stats.Cpu:F0}%   ·   RAM {_services.Stats.UsedMemoryMb}/{_services.Stats.TotalMemoryMb} МБ   ·   " +
                $"AUTO-COLOR: {(VM.Settings.AutoAccent ? $"ON (уверенность {_services.AccentSampler.LastConfidence:F2})" : "OFF")}   ·   " +
                $"ACCENT {_services.Theme.AccentHex}   ·   FOCUS: {(_services.WindowWatcher.ForegroundProcessName.Length > 0 ? _services.WindowWatcher.ForegroundProcessName : "—")}";
        };
        _uiTimer.Start();
    }

    private void OnFullscreenChanged(bool fullscreen)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnFullscreenChanged(fullscreen));
            return;
        }

        if (fullscreen && _services.Settings.Current.HideInFullscreen)
        {
            SetExpanded(false);
            IslandChrome.SetVisible(false);
        }
        else
        {
            IslandChrome.SetVisible(true);
            IslandChrome.Reposition(); // на случай, если окно переехало на другой монитор
        }
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        VM.SaveNote();
        Application.Current.Exit();
    }

    /// <summary>Для внешнего кода (IslandWindow): текущий размер таблетки в логических px.</summary>
    public (double width, double height) CurrentPillSize =>
        _expanded
            ? (IslandChrome.PillExpandedWidth, IslandChrome.PillExpandedHeight)
            : (IslandChrome.PillCollapsedWidth, IslandChrome.PillCollapsedHeight);
}
