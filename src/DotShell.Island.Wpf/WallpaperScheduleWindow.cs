namespace DotShell.Island.Wpf;

using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core.Wallpaper;
using DotShell.Island.Wpf.Controls;
using Microsoft.Win32;

/// <summary>
/// Окно «обои по времени суток»: четыре картинки на сутки и точка возврата.
///
/// Окно ничего не делает само — оно только редактирует wallpaper-schedule.json.
/// Смену обоев сторожит <see cref="WallpaperScheduleService"/>: окно можно закрыть,
/// а расписание продолжит работать.
/// </summary>
public sealed class WallpaperScheduleWindow : Window
{
    private static WallpaperScheduleWindow? _open;

    private readonly WallpaperSchedule _schedule;
    private readonly List<SlotRow> _rows = new();
    private readonly TextBlock _status;
    private readonly TextBlock _next;
    private readonly TextBlock _current;
    private readonly TextBlock _message;
    private readonly Border _enableFrame;
    private readonly TextBlock _enableLabel;
    private readonly DispatcherTimer _refresh;

    private bool _enabled;

    /// <summary>Открыть окно или поднять уже открытое (трей, настройки, флаг).</summary>
    public static void Toggle()
    {
        if (_open is { IsLoaded: true })
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        var window = new WallpaperScheduleWindow();
        _open = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        window.Show();
    }

    public WallpaperScheduleWindow()
    {
        _schedule = WallpaperScheduleStore.Load();
        _enabled = _schedule.Enabled;

        Title = "DotShell — обои по времени";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14),
            Width = 720,
            ClipToBounds = true,
        };
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        // Фон почти непрозрачный: DsBackground — это «островная» подложка с
        // настройкой прозрачности, а окно настроек должно читаться поверх чего угодно.
        root.Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x0B, 0x0E));

        var layer = new Grid();
        layer.Children.Add(new IslandBackdrop
        {
            Style = "dots",
            Strength = 0.45,
            Corner = 16,
            IsHitTestVisible = false,
        });

        var content = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

        // Шапка — ещё и ручка перетаскивания: рамки у окна нет.
        var heading = new StackPanel { Background = Brushes.Transparent, Cursor = Cursors.SizeAll };
        heading.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { }
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        var mark = new TextBlock
        {
            Text = "\uE91B",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        mark.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        top.Children.Add(mark);

        var micro = new TrackedText
        {
            Text = "ОБОИ ПО ВРЕМЕНИ СУТОК",
            FontSize = 8,
            Tracking = 0.32,
            Margin = new Thickness(10, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        micro.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        top.Children.Add(micro);
        heading.Children.Add(top);

        var title = new TextBlock
        {
            Text = "Обои по времени",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        heading.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = "Четыре картинки на сутки: час в слоте — момент, когда обои меняются. " +
                   "Если включён авто-цвет, акцент острова подстроится под новую картинку.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        heading.Children.Add(subtitle);
        content.Children.Add(heading);

        // Статус: что стоит сейчас и когда случится следующая смена.
        var card = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 14, 0, 0),
        };
        card.SetResourceReference(Border.BackgroundProperty, "DsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var statusStack = new StackPanel();
        _status = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        statusStack.Children.Add(_status);

        _next = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _next.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        statusStack.Children.Add(_next);

        _current = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Margin = new Thickness(0, 5, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _current.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        statusStack.Children.Add(_current);

        card.Child = statusStack;
        content.Children.Add(card);

        // Включение — отдельной строкой: это главный переключатель всего окна.
        var enableRow = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        enableRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        enableRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var enableText = new StackPanel();
        var enableTitle = new TextBlock
        {
            Text = "Расписание включено",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
        };
        enableTitle.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        enableText.Children.Add(enableTitle);

        var enableHint = new TextBlock
        {
            Text = "проверка раз в 20 секунд, без окна и без спешки",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            Margin = new Thickness(0, 2, 0, 0),
        };
        enableHint.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        enableText.Children.Add(enableHint);
        Grid.SetColumn(enableText, 0);
        enableRow.Children.Add(enableText);

        var enableButton = FlatButton("ВЫКЛЮЧЕНО", true, ToggleEnabled, 9, new Thickness(12, 5, 12, 5));
        System.Windows.Automation.AutomationProperties.SetName(enableButton.Button, "Переключить расписание");
        _enableFrame = enableButton.Frame;
        _enableLabel = enableButton.Label;
        Grid.SetColumn(enableButton.Button, 1);
        enableRow.Children.Add(enableButton.Button);
        content.Children.Add(enableRow);

        // Слоты.
        foreach (var slot in _schedule.Slots)
            content.Children.Add(BuildRow(slot));

        _message = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            MinHeight = 14,
            TextWrapping = TextWrapping.Wrap,
        };
        _message.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        content.Children.Add(_message);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0),
        };

        buttons.Children.Add(FlatButton("СОХРАНИТЬ", true, OnSave).Button);
        buttons.Children.Add(FlatButton("ПРИМЕНИТЬ СЕЙЧАС", false, ApplyNow).Button);
        buttons.Children.Add(FlatButton("ВЕРНУТЬ ИСХОДНЫЕ", false, RestoreOriginal).Button);
        buttons.Children.Add(FlatButton("ЗАКРЫТЬ", false, Close).Button);
        content.Children.Add(buttons);

        layer.Children.Add(content);
        root.Child = layer;
        Content = root;

        UpdateEnabledLook();
        RefreshStatus();

        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _refresh.Tick += (_, _) => RefreshStatus();
        _refresh.Start();
        Closed += (_, _) => _refresh.Stop();

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };

        Loaded += (_, _) => CenterOnAnchorMonitor();
    }

    /// <summary>Одна строка слота: название, час, превью, путь и две кнопки.</summary>
    private Border BuildRow(WallpaperSlot slot)
    {
        var row = new SlotRow { Slot = slot };

        var frame = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 0, 0, 6),
        };
        frame.SetResourceReference(Border.BackgroundProperty, "DsSurface");
        frame.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TrackedText
        {
            Text = string.IsNullOrWhiteSpace(slot.Name) ? "СЛОТ" : slot.Name.ToUpperInvariant(),
            FontSize = 9,
            Tracking = 0.2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        name.SetResourceReference(TrackedText.BrushProperty, "DsText");
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var hourFrame = new Border
        {
            Width = 38,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        hourFrame.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
        hourFrame.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        row.Hour = new TextBox
        {
            Text = slot.StartHour.ToString("00", CultureInfo.InvariantCulture),
            MaxLength = 2,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 14,
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            CaretBrush = (Brush)FindResource("DsAccent"),
            ToolTip = "Час, когда включается этот слот (00–23)",
        };
        row.Hour.SetResourceReference(TextBox.ForegroundProperty, "DsText");
        hourFrame.Child = row.Hour;
        Grid.SetColumn(hourFrame, 1);
        grid.Children.Add(hourFrame);

        var previewFrame = new Border
        {
            Width = 50,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        previewFrame.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");

        var previewGrid = new Grid();
        row.Preview = new Image { Stretch = Stretch.UniformToFill };
        previewGrid.Children.Add(row.Preview);
        row.PreviewFallback = new TextBlock
        {
            Text = "—",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.PreviewFallback.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        previewGrid.Children.Add(row.PreviewFallback);
        previewFrame.Child = previewGrid;
        Grid.SetColumn(previewFrame, 2);
        grid.Children.Add(previewFrame);

        var pathFrame = new Border
        {
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 0, 8, 0),
            Margin = new Thickness(2, 0, 6, 0),
        };
        pathFrame.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");

        row.PathText = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.PathText.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        pathFrame.Child = row.PathText;
        Grid.SetColumn(pathFrame, 3);
        grid.Children.Add(pathFrame);

        var choose = FlatButton("ВЫБРАТЬ", false, () => ChooseFile(row), 9, new Thickness(10, 5, 10, 5));
        System.Windows.Automation.AutomationProperties.SetName(
            choose.Button, "Выбрать картинку для слота «" + row.DisplayName + "»");
        choose.Button.ToolTip = "Выбрать картинку для этого слота";
        Grid.SetColumn(choose.Button, 4);
        grid.Children.Add(choose.Button);

        var clear = FlatButton("×", false, () => ClearFile(row), 12, new Thickness(9, 3, 9, 3));
        System.Windows.Automation.AutomationProperties.SetName(clear.Button, "Убрать картинку из слота «" + row.DisplayName + "»");
        clear.Button.ToolTip = "Убрать картинку из слота";
        clear.Button.Margin = new Thickness(6, 0, 0, 0);
        Grid.SetColumn(clear.Button, 5);
        grid.Children.Add(clear.Button);

        frame.Child = grid;

        _rows.Add(row);
        SetPathDisplay(row);
        return frame;
    }

    private void ChooseFile(SlotRow row)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Картинка для слота «" + (string.IsNullOrWhiteSpace(row.Slot.Name) ? "слот" : row.Slot.Name) + "»",
            Filter = "Картинки (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };

        try
        {
            var folder = Path.GetDirectoryName(row.Slot.Path);
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                dialog.InitialDirectory = folder;
        }
        catch { }

        if (dialog.ShowDialog(this) != true) return;

        row.Slot.Path = dialog.FileName;
        SetPathDisplay(row);
        ShowMessage("выбрано — не забудь сохранить", false);
    }

    private void ClearFile(SlotRow row)
    {
        row.Slot.Path = string.Empty;
        SetPathDisplay(row);
        ShowMessage("картинка убрана — не забудь сохранить", false);
    }

    private static void SetPathDisplay(SlotRow row)
    {
        bool hasPath = !string.IsNullOrWhiteSpace(row.Slot.Path);
        bool exists = hasPath && File.Exists(row.Slot.Path);

        row.PathText.Text = hasPath ? row.Slot.Path : "не выбрана";
        row.PathText.ToolTip = hasPath ? row.Slot.Path : "Нажми «Выбрать», чтобы указать картинку";
        row.PathText.SetResourceReference(TextBlock.ForegroundProperty, exists ? "DsText" : "DsTextMuted");

        var image = exists ? LoadPreview(row.Slot.Path!) : null;
        row.Preview.Source = image;
        row.Preview.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        row.PreviewFallback.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ImageSource? LoadPreview(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.DecodePixelWidth = 100;
            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void ToggleEnabled()
    {
        if (_rows.Count == 0) return;

        bool previous = _enabled;
        _enabled = !previous;
        UpdateEnabledLook();

        if (!Persist(out var error))
        {
            _enabled = previous;
            UpdateEnabledLook();
            ShowMessage(error, true);
            return;
        }

        var service = ShellServices.Instance?.WallpaperSchedule;
        if (_enabled)
        {
            var result = service?.ApplyNow() ?? (false, "сервис обоев не запущен");
            ShowMessage(result.Item1 ? "расписание включено · " + result.Item2
                                     : "расписание включено, но " + result.Item2, !result.Item1);
        }
        else
        {
            service?.Refresh();
            ShowMessage("расписание выключено · обои остались как есть", false);
        }

        RefreshStatus();
    }

    private void OnSave()
    {
        if (!Persist(out var error))
        {
            ShowMessage(error, true);
            return;
        }

        var service = ShellServices.Instance?.WallpaperSchedule;
        if (service is null)
        {
            ShowMessage("сервис обоев не запущен", true);
            return;
        }

        if (!_enabled)
        {
            service.Refresh();
            ShowMessage("сохранено · расписание выключено", false);
        }
        else
        {
            var result = service.ApplyNow();
            ShowMessage(result.Ok ? "сохранено · " + result.Message
                                  : "сохранено, но " + result.Message, !result.Ok);
        }

        RefreshStatus();
    }

    private void ApplyNow()
    {
        if (!Persist(out var error))
        {
            ShowMessage(error, true);
            return;
        }

        if (!_enabled)
        {
            ShowMessage("сначала включите расписание", true);
            return;
        }

        var service = ShellServices.Instance?.WallpaperSchedule;
        if (service is null)
        {
            ShowMessage("сервис обоев не запущен", true);
            return;
        }

        var result = service.ApplyNow();
        ShowMessage(result.Message, !result.Ok);
        RefreshStatus();
    }

    private void RestoreOriginal()
    {
        _enabled = false;
        _schedule.Enabled = false;
        _schedule.LastAppliedKey = string.Empty;
        WallpaperScheduleStore.Save(_schedule);
        UpdateEnabledLook();

        ShellServices.Instance?.WallpaperSchedule.Refresh();

        bool ok = WallpaperApi.TryRestoreOriginal(_schedule, out var error);
        ShowMessage(ok
            ? "исходные обои возвращены · расписание выключено"
            : "расписание выключено, но " + error, !ok);

        RefreshStatus();
    }

    /// <summary>Прочитать часы из полей и записать расписание на диск.</summary>
    private bool Persist(out string error)
    {
        error = string.Empty;

        foreach (var row in _rows)
        {
            var text = (row.Hour.Text ?? string.Empty).Trim();
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int hour) ||
                hour is < 0 or > 23)
            {
                error = $"«{row.DisplayName}»: час должен быть числом от 00 до 23";
                return false;
            }

            row.Slot.StartHour = hour;
        }

        if (!_schedule.Validate(out var bad))
        {
            error = bad;
            return false;
        }

        // Запоминаем исходные обои ДО первой смены: иначе возвращать будет нечего.
        if (_enabled && !_schedule.OriginalCaptured)
            WallpaperApi.CaptureOriginal(_schedule);

        _schedule.Enabled = _enabled;

        // Выключенное расписание при следующем включении должно применить слот
        // заново, даже если этот час уже когда-то применялся.
        if (!_enabled) _schedule.LastAppliedKey = string.Empty;

        WallpaperScheduleStore.Save(_schedule);
        return true;
    }

    private void UpdateEnabledLook()
    {
        if (_enableLabel is null) return;

        _enableLabel.Text = _enabled ? "ВКЛЮЧЕНО" : "ВЫКЛЮЧЕНО";
        _enableLabel.SetResourceReference(TextBlock.ForegroundProperty, _enabled ? "DsAccent" : "DsTextMuted");
        _enableFrame.SetResourceReference(Border.BorderBrushProperty, _enabled ? "DsAccent" : "DsBorder");
        _enableFrame.SetResourceReference(Border.BackgroundProperty, _enabled ? "DsAccentGhost" : "DsSurfaceAlt");
    }

    private void ShowMessage(string text, bool error)
    {
        _message.Text = text;
        _message.SetResourceReference(TextBlock.ForegroundProperty, error ? "DsAccent" : "DsTextMuted");
    }

    private void RefreshStatus()
    {
        var now = DateTime.Now;

        if (!_schedule.Enabled)
        {
            _status.Text = "Расписание выключено";
            _next.Text = "обои меняются только вручную";
        }
        else
        {
            var slot = _schedule.CurrentSlot(now);
            var next = _schedule.NextSlot(now);
            var ordered = _schedule.Ordered();
            int index = ordered.IndexOf(slot);
            var end = ordered[(index + 1) % ordered.Count];

            _status.Text = $"Сейчас: {DisplayName(slot)} · {slot.StartHour:00}:00–{end.StartHour:00}:00";

            if (string.IsNullOrWhiteSpace(slot.Path))
            {
                _next.Text = $"у слота «{DisplayName(slot)}» не выбрана картинка";
            }
            else
            {
                var nextTime = now.Date.AddHours(next.StartHour);
                if (nextTime <= now) nextTime = nextTime.AddDays(1);
                var left = nextTime - now;
                var leftText = left.TotalHours >= 1
                    ? $"{(int)left.TotalHours} ч {left.Minutes:00} мин"
                    : $"{Math.Max(1, left.Minutes)} мин";

                _next.Text = $"Дальше: {DisplayName(next)} в {next.StartHour:00}:00 · через {leftText}";
            }
        }

        var current = WallpaperApi.CurrentPath();
        _current.Text = string.IsNullOrWhiteSpace(current)
            ? "На столе: сплошной цвет или обои Wallpaper Engine"
            : $"На столе: {current}";
        _current.ToolTip = string.IsNullOrWhiteSpace(current) ? null : current;
    }

    private static string DisplayName(WallpaperSlot slot) =>
        string.IsNullOrWhiteSpace(slot.Name) ? $"{slot.StartHour:00}:00" : slot.Name;

    /// <summary>
    /// Кнопка без шаблона острова: окно создаётся кодом и не видит window-scoped
    /// стили IslandWindow.xaml, поэтому рамку и текст собираем вручную.
    /// </summary>
    private static (Button Button, Border Frame, TextBlock Label) FlatButton(
        string text, bool accent, Action onClick, double fontSize = 10, Thickness? padding = null)
    {
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = padding ?? new Thickness(12, 6, 12, 6),
        };
        frame.SetResourceReference(Border.BorderBrushProperty, accent ? "DsAccent" : "DsBorder");
        frame.SetResourceReference(Border.BackgroundProperty, accent ? "DsAccentGhost" : "DsSurfaceAlt");

        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = fontSize,
            FontWeight = FontWeights.SemiBold,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, accent ? "DsAccent" : "DsTextMuted");
        frame.Child = label;

        var button = new Button
        {
            Content = frame,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Template = FlatTemplate(),
            Margin = new Thickness(0, 0, 8, 0),
        };

        button.MouseEnter += (_, _) =>
            frame.SetResourceReference(Border.BackgroundProperty, accent ? "DsAccentSoft" : "DsAccentGhost");
        button.MouseLeave += (_, _) =>
            frame.SetResourceReference(Border.BackgroundProperty, accent ? "DsAccentGhost" : "DsSurfaceAlt");

        button.Click += (_, _) => onClick();
        System.Windows.Automation.AutomationProperties.SetName(button, text);
        return (button, frame, label);
    }

    private static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(BackgroundProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private void CenterOnAnchorMonitor()
    {
        try
        {
            var area = MonitorHelper.GetAnchorWorkAreaDip(this);
            double w = ActualWidth > 0 ? ActualWidth : Width;
            double h = ActualHeight > 0 ? ActualHeight : Height;
            if (double.IsNaN(w) || w <= 0) w = 720;
            if (double.IsNaN(h) || h <= 0) h = 520;
            Left = area.Left + (area.Width - w) / 2;
            Top = area.Top + (area.Height - h) / 2;
            MonitorHelper.ClampToWorkArea(this, area);
        }
        catch { }
    }

    /// <summary>Строка одного слота — держит контролы, чтобы прочитать/обновить их.</summary>
    private sealed class SlotRow
    {
        public required WallpaperSlot Slot { get; init; }
        public TextBox Hour { get; set; } = null!;
        public TextBlock PathText { get; set; } = null!;
        public Image Preview { get; set; } = null!;
        public TextBlock PreviewFallback { get; set; } = null!;

        public string DisplayName =>
            string.IsNullOrWhiteSpace(Slot.Name) ? $"{Slot.StartHour:00}:00" : Slot.Name;
    }
}
