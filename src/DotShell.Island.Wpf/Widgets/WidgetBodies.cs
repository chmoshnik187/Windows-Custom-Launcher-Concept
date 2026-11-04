namespace DotShell.Island.Wpf.Widgets;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core;
using DotShell.Core.Files;
using DotShell.Core.Launcher;
using DotShell.Core.Maintenance;
using DotShell.Island.Wpf.Controls;
using DotShell.Core.Settings;
using DotShell.Core.Windowing;

/// <summary>
/// Тела «полезных» виджетов 0.5.0: поиск настроек, завод мусора, радар
/// дубликатов, светофор обновлений и карта окон. Каждое тело самодостаточно:
/// свои таймеры регистрирует через WidgetWindow.Timer (остановятся на Closed),
/// тяжёлую работу гоняет в Task.Run, чтобы не морозить шелл.
/// </summary>
public sealed partial class WidgetWindow
{
    // ------------------------------------------------------------------
    //  Мелкие помощники построения
    // ------------------------------------------------------------------

    private TextBox WTextBox(string hint)
    {
        var box = new TextBox { Style = (Style)FindResource("WBox"), ToolTip = hint };
        return box;
    }

    private Button WBtn(string label, string? toolTip = null)
    {
        var btn = new Button { Style = (Style)FindResource("WButton"), Content = label };
        if (toolTip is not null) btn.ToolTip = toolTip;
        return btn;
    }

    private static void OpenUri(string uri)
    {
        try
        {
            string file = uri, args = "";
            var space = uri.IndexOf(".exe ", StringComparison.OrdinalIgnoreCase);
            if (space > 0) { file = uri[..(space + 4)]; args = uri[(space + 5)..]; }
            Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info($"open settings failed for {uri}: {ex.Message}"); }
    }

    private static BitmapSource? IconFor(string exePath)
    {
        var png = IconCache.Extract(string.Empty, exePath);
        if (string.IsNullOrEmpty(png)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 32;
            bmp.UriSource = new Uri(png);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------
    //  15. Объединитель настроек
    // ------------------------------------------------------------------

    private UIElement BuildSettings()
    {
        var search = WTextBox("Пиши по-человечески: «спящий режим», «автозагрузка», «второй экран»");
        var rows = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = rows;

        void Refresh()
        {
            rows.Children.Clear();
            foreach (var entry in SettingsCatalog.Search(search.Text, 10))
            {
                var row = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 3),
                    Cursor = Cursors.Hand,
                    ToolTip = entry.Uri,
                };
                row.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
                var stack = new StackPanel();
                stack.Children.Add(new TextBlock { Style = (Style)FindResource("WTitle"), Text = entry.Title, FontSize = 12 });
                stack.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Text = entry.Uri });
                row.Child = stack;
                row.MouseLeftButtonUp += (_, _) => OpenUri(entry.Uri);
                rows.Children.Add(row);
            }
            if (rows.Children.Count == 0)
                rows.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("WSub"),
                    Text = "ничего не нашлось — попробуй другими словами",
                });
        }

        search.TextChanged += (_, _) => Refresh();
        Refresh();

        var panel = new StackPanel { MinWidth = 300 };
        panel.Children.Add(new TextBlock { Style = (Style)FindResource("WTitle"), Text = "ГДЕ ЭТА НАСТРОЙКА?", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(search);
        panel.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Margin = new Thickness(0, 4, 0, 4), Text = "клик открывает точную страницу параметров или панели управления" });
        panel.Children.Add(scroll);
        return panel;
    }

    // ------------------------------------------------------------------
    //  12. Мусоросжигательный завод
    // ------------------------------------------------------------------

    /// <summary>
    /// Зашлакованность: на маленьком размере — цифра и две кнопки,
    /// на большом — разбивка по зонам, чтобы было видно, ИЗ ЧЕГО эти мегабайты.
    /// Раньше виджет растягивался в чёрное поле: размер рос, информации — нет.
    /// </summary>
    private UIElement BuildClean()
    {
        var total = new TextBlock { Style = (Style)FindResource("WTitle"), FontSize = 20, Text = "…" };
        var detail = new TextBlock { Style = (Style)FindResource("WSub"), TextWrapping = TextWrapping.Wrap, MaxWidth = 300 };
        var status = new TextBlock { Style = (Style)FindResource("WSub"), Text = "" };
        var breakdown = new StackPanel { Margin = new Thickness(0, 14, 0, 0), Visibility = Visibility.Collapsed };

        void Rescan()
        {
            status.Text = "считаю шлак…";
            Task.Run(() =>
            {
                var bytes = JunkCleaner.Scan(out var recycle);
                var parts = JunkCleaner.Breakdown();

                Dispatcher.BeginInvoke(() =>
                {
                    total.Text = JunkCleaner.Human(bytes + recycle);
                    detail.Text = $"временные и кэши: {JunkCleaner.Human(bytes)} · корзина: {JunkCleaner.Human(recycle)}";
                    status.Text = "занятые файлы честно пропускаются";

                    breakdown.Children.Clear();
                    breakdown.Children.Add(CleanAreaRow(new JunkArea("корзина", ""), recycle, Math.Max(bytes, recycle)));

                    foreach (var (area, size) in parts)
                        breakdown.Children.Add(CleanAreaRow(area, size, Math.Max(bytes, recycle)));
                });
            });
        }

        var cleanTemp = WBtn("ПОЧИСТИТЬ TEMP");
        cleanTemp.Click += (_, _) =>
        {
            status.Text = "чищу…";
            Task.Run(() =>
            {
                var freed = JunkCleaner.Clean();
                Dispatcher.BeginInvoke(() =>
                {
                    status.Text = $"освобождено {JunkCleaner.Human(freed)}";
                    Rescan();
                });
            });
        };

        var cleanBin = WBtn("ОЧИСТИТЬ КОРЗИНУ");
        cleanBin.Margin = new Thickness(6, 0, 0, 0);
        cleanBin.Click += (_, _) =>
        {
            status.Text = "чищу корзину…";
            Task.Run(() =>
            {
                var freed = JunkCleaner.EmptyRecycleBin();
                Dispatcher.BeginInvoke(() =>
                {
                    status.Text = $"корзина: освобождено {JunkCleaner.Human(freed)}";
                    Rescan();
                });
            });
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(cleanTemp);
        buttons.Children.Add(cleanBin);

        var rescan = WBtn("ПЕРЕСЧИТАТЬ");
        rescan.Margin = new Thickness(6, 0, 0, 0);
        rescan.Click += (_, _) => Rescan();
        buttons.Children.Add(rescan);

        var caption = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = "ЗАШЛАКОВАННОСТЬ",
        };

        var panel = new StackPanel();
        panel.Children.Add(caption);
        panel.Children.Add(total);
        panel.Children.Add(detail);
        panel.Children.Add(buttons);
        panel.Children.Add(status);
        panel.Children.Add(breakdown);

        Rescan();
        Timer(300, Rescan);

        // Композиция по размеру: полоска — только цифра, маленький — цифра и
        // кнопки, большой — плюс разбивка по зонам. Раньше пряталась одна разбивка,
        // и на полоске оставались кнопки, которые некуда нажать.
        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);

            // На полоске видна только цифра: подпись и размер занимают её место.
            caption.Visibility = size == WidgetSizeClass.Strip ? Visibility.Collapsed : Visibility.Visible;
            total.Visibility = Visibility.Visible;
            detail.Visibility = size == WidgetSizeClass.Strip ? Visibility.Collapsed : Visibility.Visible;
            buttons.Visibility = size >= WidgetSizeClass.Normal ? Visibility.Visible : Visibility.Collapsed;
            status.Visibility = size >= WidgetSizeClass.Normal ? Visibility.Visible : Visibility.Collapsed;
            breakdown.Visibility = size == WidgetSizeClass.Large ? Visibility.Visible : Visibility.Collapsed;

            total.FontSize = size == WidgetSizeClass.Strip ? 22 : 30;
        };

        return panel;
    }

    /// <summary>Строка разбивки: имя зоны, размер и точечная дорожка доли.</summary>
    private UIElement CleanAreaRow(JunkArea area, long bytes, long max)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = area.Name,
            Style = (Style)FindResource("WSub"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var size = new TextBlock
        {
            Text = JunkCleaner.Human(bytes),
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Foreground = (Brush)FindResource("DsText"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(size, 1);
        grid.Children.Add(size);

        var bar = new DotBar
        {
            Height = 8,
            Margin = new Thickness(0, 4, 0, 0),
            Value = max > 0 ? (double)bytes / max : 0,
        };

        var stack = new StackPanel();
        stack.Children.Add(grid);
        stack.Children.Add(bar);
        return stack;
    }
    private UIElement BuildDupes()
    {
        var status = new TextBlock { Style = (Style)FindResource("WSub"), Text = "выбери папку — просканирую на дубликаты" };
        var rows = new StackPanel();
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.Content = rows;

        string? root = null;

        void Render(List<DupeGroup> groups)
        {
            rows.Children.Clear();
            if (groups.Count == 0)
            {
                status.Text = "дубликатов не найдено — диск чист";
                return;
            }

            long waste = groups.Sum(g => g.Size * (g.Files.Count - 1));
            status.Text = $"групп: {groups.Count} · лишнего на {JunkCleaner.Human(waste)}";

            foreach (var group in groups.Take(40))
            {
                var row = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                };
                row.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
                row.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

                var stack = new StackPanel();
                stack.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("WTitle"),
                    FontSize = 11,
                    Text = (group.Visual ? "≈ " : "") + group.NameLabel,
                    ToolTip = string.Join("\n", group.Files),
                });
                stack.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Text = group.SizeLabel });

                var kill = WBtn("УДАЛИТЬ ЛИШНИЕ");
                kill.Margin = new Thickness(0, 4, 0, 0);
                kill.Click += (_, _) =>
                {
                    var answer = MessageBox.Show(
                        "Останется самая ранняя копия:\n" + group.Files.First() +
                        "\n\nУдалить остальные (" + (group.Files.Count - 1) + ")?",
                        "Радар дубликатов", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer != MessageBoxResult.Yes) return;
                    var (removed, freed) = DuplicateScanner.RemoveExtras(group);
                    status.Text = $"удалено копий: {removed} · освобождено {JunkCleaner.Human(freed)}";
                    rows.Children.Remove(row);
                };
                stack.Children.Add(kill);

                row.Child = stack;
                rows.Children.Add(row);
            }
        }

        var pick = WBtn("ВЫБРАТЬ ПАПКУ");
        pick.Click += (_, _) =>
        {
            // .NET 8 даёт штатный WPF-диалог выбора папки: WinForms здесь больше
            // не нужен, а значит и 21 МБ его сборок в публикации.
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Где искать дубликаты?",
            };
            if (dialog.ShowDialog() != true) return;
            root = dialog.FolderName;
            status.Text = "сканирую…";
            rows.Children.Clear();
            Task.Run(() =>
            {
                var groups = DuplicateScanner.Scan(root!, p => Dispatcher.BeginInvoke(() => status.Text = p));
                Dispatcher.BeginInvoke(() => Render(groups));
            });
        };

        var panel = new StackPanel { MinWidth = 300 };
        panel.Children.Add(new TextBlock { Style = (Style)FindResource("WTitle"), Text = "РАДАР ДУБЛИКАТОВ", Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(pick);
        panel.Children.Add(status);
        panel.Children.Add(scroll);

        // Полоска — только статус, маленький — плюс кнопка выбора папки, дальше
        // уже список: у списков «композиция» и есть «сводка против списка».
        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);
            pick.Visibility = size == WidgetSizeClass.Strip ? Visibility.Collapsed : Visibility.Visible;
            scroll.Visibility = size >= WidgetSizeClass.Normal ? Visibility.Visible : Visibility.Collapsed;
        };

        return panel;
    }

    // ------------------------------------------------------------------
    //  9. Светофор обновлений
    // ------------------------------------------------------------------

    /// <summary>
    /// Обновления Windows — понятным языком.
    ///
    /// Раньше виджет показывал «признаки косвенные, без админ-прав» и кнопку
    /// «приостановить», по которой было непонятно, что вообще происходит.
    /// Теперь он отвечает на три вопроса: что сейчас, что это значит и что
    /// делать. На большом размере добавляется расшифровка светофора и время
    /// последней установки, а на маленьком — только лампа и одна строка.
    /// </summary>
    private UIElement BuildUpdate()
    {
        var lamp = new System.Windows.Shapes.Ellipse
        {
            Width = 26,
            Height = 26,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        var headline = new TextBlock
        {
            Style = (Style)FindResource("WTitle"),
            Text = "…",
            TextWrapping = TextWrapping.Wrap,
        };

        var meaning = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var details = new StackPanel { Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };

        var compactLamp = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };

        var compactText = new TextBlock
        {
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 11,
            Foreground = WidgetAdaptive.Ink,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        void Refresh()
        {
            Task.Run(() =>
            {
                var (light, _, when) = UpdateStatus.Get();
                Dispatcher.BeginInvoke(() =>
                {
                    var color = light switch
                    {
                        UpdateStatus.Light.Red => Color.FromRgb(0xE5, 0x48, 0x4D),
                        UpdateStatus.Light.Yellow => Color.FromRgb(0xFF, 0xC4, 0x00),
                        _ => Color.FromRgb(0x3D, 0xDC, 0x84),
                    };
                    lamp.Fill = new SolidColorBrush(color);
                    compactLamp.Fill = new SolidColorBrush(color);

                    headline.Text = light switch
                    {
                        UpdateStatus.Light.Red => "ПЕРЕЗАГРУЗКА ЖДЁТ",
                        UpdateStatus.Light.Yellow => "ОБНОВЛЯЕТСЯ ПРЯМО СЕЙЧАС",
                        _ => "ВСЁ АКТУАЛЬНО",
                    };

                    compactText.Text = light switch
                    {
                        UpdateStatus.Light.Red => "НУЖНА ПЕРЕЗАГРУЗКА",
                        UpdateStatus.Light.Yellow => "СТАВИТСЯ СЕЙЧАС",
                        _ => "ВСЁ АКТУАЛЬНО",
                    };

                    meaning.Text = light switch
                    {
                        UpdateStatus.Light.Red =>
                            "Windows уже поставил обновления, но им нужна перезагрузка. " +
                            "Пока не перезагрузишься — изменения не заработают.",
                        UpdateStatus.Light.Yellow =>
                            "Центр обновлений качает или устанавливает в фоне. Делать ничего не нужно.",
                        _ =>
                            "Перезагрузка не требуется и установка не идёт.",
                    };

                    details.Children.Clear();
                    details.Children.Add(LegendRow(Color.FromRgb(0xE5, 0x48, 0x4D), "красный — нужна перезагрузка"));
                    details.Children.Add(LegendRow(Color.FromRgb(0xFF, 0xC4, 0x00), "жёлтый — ставится в фоне"));
                    details.Children.Add(LegendRow(Color.FromRgb(0x3D, 0xDC, 0x84), "зелёный — всё спокойно"));
                    details.Children.Add(new TextBlock
                    {
                        Style = (Style)FindResource("WSub"),
                        Text = $"последняя успешная установка: {when}\nвиджет ничего не ставит и не удаляет — только показывает состояние",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 10, 0, 0),
                    });
                });
            });
        }

        var open = WBtn("ОТКРЫТЬ ОБНОВЛЕНИЯ");
        open.Click += (_, _) => OpenUri("ms-settings:windowsupdate");

        var again = WBtn("ПРОВЕРИТЬ СНОВА", "перечитать состояние");
        again.Margin = new Thickness(6, 0, 0, 0);
        again.Click += (_, _) => Refresh();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(open);
        buttons.Children.Add(again);

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        head.Children.Add(lamp);
        var headText = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
        headText.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Text = "ОБНОВЛЕНИЯ WINDOWS" });
        headText.Children.Add(headline);
        headText.Children.Add(meaning);
        Grid.SetColumn(headText, 1);
        head.Children.Add(headText);

        var panel = new StackPanel();
        panel.Children.Add(head);
        panel.Children.Add(buttons);
        panel.Children.Add(details);

        // Полоска: только лампа и статус одной строкой. Кнопки и легенда на высоте
        // в 50 px — это очередь из обрезанных элементов, а не информация.
        var compact = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        compact.Children.Add(compactLamp);
        compact.Children.Add(compactText);

        var host = new Grid();
        host.Children.Add(compact);
        host.Children.Add(panel);

        object? current = null;

        _relayout = (w, h) =>
        {
            var size = WidgetAdaptive.Of(w, h);
            var next = size == WidgetSizeClass.Strip ? (UIElement)compact : panel;
            WidgetAdaptive.Show(host, next, ref current);
        };

        Refresh();
        Timer(60, Refresh);
        return host;
    }

    /// <summary>Строка легенды «что значит этот цвет».</summary>
    private UIElement LegendRow(Color color, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = new SolidColorBrush(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        grid.Children.Add(dot);

        var label = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = text,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        return grid;
    }
    private UIElement BuildWindows()
    {
        var rows = new StackPanel();
        var scroll = new ScrollViewer { MinWidth = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalAlignment = HorizontalAlignment.Stretch };
        scroll.Content = rows;

        var preview = new Image
        {
            Width = 190,
            Height = 130,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var previewBox = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            Width = 200,
            Height = 140,
        };
        previewBox.SetResourceReference(Border.BorderBrushProperty, "DsBorder");
        previewBox.Child = preview;

        void Refresh()
        {
            var windows = WindowList.Get(ShellServices.Instance.OwnWindows.ToArray());
            rows.Children.Clear();
            foreach (var win in windows.Take(30))
            {
                var row = new Border
                {
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(6, 4, 6, 4),
                    Margin = new Thickness(0, 0, 0, 3),
                    Cursor = Cursors.Hand,
                };
                row.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                if (win.ExePath.Length > 0 && IconFor(win.ExePath) is { } icon)
                    grid.Children.Add(new Image { Source = icon, Width = 18, Height = 18, VerticalAlignment = VerticalAlignment.Center });
                else
                    grid.Children.Add(new TextBlock
                    {
                        Text = win.Title[..1].ToUpperInvariant(),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontWeight = FontWeights.Bold,
                        Foreground = new SolidColorBrush(ColorPalette.Accent),
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                var title = new TextBlock
                {
                    Style = (Style)FindResource("WSub"),
                    Text = win.Title,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0),
                };
                Grid.SetColumn(title, 1);
                grid.Children.Add(title);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var go = new Button { Style = (Style)FindResource("WRound"), Width = 22, Height = 22, FontSize = 9, Content = "→", ToolTip = "переключиться" };
                go.Click += (_, _) => WindowList.Focus(win.Hwnd);
                var kill = new Button { Style = (Style)FindResource("WRound"), Width = 22, Height = 22, FontSize = 9, Content = "✕", ToolTip = "закрыть окно (как крестик)" };
                kill.Margin = new Thickness(4, 0, 0, 0);
                kill.Click += (_, _) => { WindowList.Close(win.Hwnd); Timer(600, Refresh); };
                actions.Children.Add(go);
                actions.Children.Add(kill);
                Grid.SetColumn(actions, 2);
                grid.Children.Add(actions);

                row.Child = grid;
                // Клик по строке — снимок окна в превью (честный PrintWindow).
                row.MouseLeftButtonUp += (_, _) =>
                {
                    preview.Source = null;
                    Task.Run(() =>
                    {
                        using var bmp = WindowList.Snapshot(win.Hwnd);
                        if (bmp is null) return;
                        var handle = bmp.GetHbitmap();
                        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        Win32DeleteObject(handle);
                        Dispatcher.BeginInvoke(() => preview.Source = source);
                    });
                };
                rows.Children.Add(row);
            }

            if (rows.Children.Count == 0)
                rows.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Text = "видимых окон нет" });
        }

        var refresh = WBtn("ОБНОВИТЬ");
        refresh.Click += (_, _) => Refresh();

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Style = (Style)FindResource("WTitle"), Text = "КАРТА ОКОН", Margin = new Thickness(0, 0, 0, 6) });
        left.Children.Add(refresh);
        left.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Margin = new Thickness(0, 4, 0, 4), Text = "клик — превью · → — перейти · ✕ — закрыть" });
        left.Children.Add(scroll);

        // Раньше `left` добавляли и в body, и в outer — второй Add падал
        // исключением «элемент уже логический ребёнок другого элемента»,
        // и виджет «карта окон» не открывался вообще.
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        previewBox.Margin = new Thickness(10, 0, 0, 0);
        Grid.SetColumn(previewBox, 1);
        outer.Children.Add(left);
        outer.Children.Add(previewBox);

        // Маленькому виджету превью не нужно: список окон и так занимает всё
        // место, а превью делает его нечитаемым.
        _relayout = (_, h) =>
        {
            previewBox.Visibility = h >= 190 ? Visibility.Visible : Visibility.Collapsed;
        };

        Refresh();
        Timer(15000, Refresh);
        return outer;
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static void Win32DeleteObject(IntPtr handle) => DeleteObject(handle);

    // ------------------------------------------------------------------
    //  18. Аудио-роутер: приложение → устройство вывода
    // ------------------------------------------------------------------

    /// <summary>
    /// Дискорд — в наушники, игра — в колонки, браузер — в HDMI монитора.
    /// Такого не умеет ни один системный экран Windows без сторонних микшеров.
    ///
    /// Сценарий нарочно в два клика: выбрал приложение слева, ткнул устройство
    /// справа. Никаких перетаскиваний иконок на устройства — на 3440-пиксельном
    /// мониторе это красиво в рекламе и мучительно в жизни.
    /// </summary>
    private UIElement BuildAudioRouter()
    {
        var router = ShellServices.Instance.Audio;

        int selectedPid = 0;
        string selectedName = string.Empty;
        bool suppressVolume = false;

        var appRows = new StackPanel();
        var deviceRows = new StackPanel();
        var appsSignature = string.Empty;
        var devicesSignature = string.Empty;

        var selectedTitle = new TextBlock
        {
            Style = (Style)FindResource("WTitle"),
            Text = "ничего не выбрано",
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 286,
        };
        var selectedHint = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = "клик по приложению слева, потом по устройству справа",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var volume = new Slider
        {
            Style = (Style)FindResource("WSlider"),
            Minimum = 0,
            Maximum = 100,
            Width = 286,
            IsEnabled = false,
            Margin = new Thickness(0, 8, 0, 0),
        };

        var volumeLabel = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = "—",
            Margin = new Thickness(0, 4, 0, 0),
        };

        var status = new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };

        void SetStatus(string text) => status.Text = text;

        // ---- отрисовка строк приложений -------------------------------------

        Border AppRow(DotShell.Core.Audio.AudioApp app)
        {
            var row = new Border
            {
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 3),
                Cursor = Cursors.Hand,
                Tag = app.ProcessId,
            };
            row.SetResourceReference(Border.BackgroundProperty,
                app.ProcessId == selectedPid ? "DsAccentSoft" : "DsSurfaceAlt");

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Image { Width = 16, Height = 16, Margin = new Thickness(0, 0, 7, 0) };
            var source = app.ExePath is null ? null : IconFor(app.ExePath);
            if (source is not null) icon.Source = source;
            else icon.Visibility = Visibility.Collapsed;
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var name = new TextBlock
            {
                Style = (Style)FindResource(app.IsPlaying ? "WTitle" : "WSub"),
                Text = app.Name,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 210,
            };
            Grid.SetColumn(name, 1);
            grid.Children.Add(name);

            var level = new TextBlock
            {
                Style = (Style)FindResource("WSub"),
                Text = app.Muted ? "ТИШИНА" : $"{app.Volume * 100:F0}%",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            Grid.SetColumn(level, 2);
            grid.Children.Add(level);

            row.Child = grid;

            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                selectedPid = app.ProcessId;
                selectedName = app.Name;
                selectedTitle.Text = app.Name;
                volume.IsEnabled = true;
                suppressVolume = true;
                volume.Value = Math.Round(app.Volume * 100);
                suppressVolume = false;
                volumeLabel.Text = $"{app.Volume * 100:F0}%";
                SetStatus(string.Empty);
                RedrawApps();
            };

            // Колесо над строкой — быстрая громкость приложения, как на свёрнутой пилюле.
            row.MouseWheel += (_, e) =>
            {
                var next = Math.Clamp(app.Volume + (e.Delta > 0 ? 0.05f : -0.05f), 0f, 1f);
                router.SetVolume(app.ProcessId, next);
                level.Text = next >= 0.999f ? "100%" : $"{next * 100:F0}%";
                e.Handled = true;
            };

            return row;
        }

        void RedrawApps()
        {
            var apps = router.Apps();
            appsSignature = string.Join(";", apps.Select(a => $"{a.ProcessId}:{a.IsPlaying}:{a.Volume:F2}"));

            appRows.Children.Clear();
            if (apps.Count == 0)
            {
                appRows.Children.Add(new TextBlock
                {
                    Style = (Style)FindResource("WSub"),
                    Text = "тишина — ни одно приложение сейчас не звучит",
                });
                return;
            }

            foreach (var app in apps) appRows.Children.Add(AppRow(app));
        }

        // ---- отрисовка устройств --------------------------------------------

        void RedrawDevices()
        {
            var devices = router.Devices();
            devicesSignature = string.Join(";", devices.Select(d => d.Id + d.IsDefault));

            deviceRows.Children.Clear();
            foreach (var dev in devices)
            {
                var current = selectedPid != 0 && router.GetAppDevice(selectedPid) == dev.Id;

                var label = new TextBlock
                {
                    Text = (current ? "● " : "○ ") + dev.Name + (dev.IsDefault ? "  · системное" : string.Empty),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    MaxWidth = 240,
                };
                label.SetResourceReference(TextBlock.ForegroundProperty, current ? "DsText" : "DsTextMuted");

                var button = WBtn(string.Empty, dev.Id);
                button.Content = label;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Margin = new Thickness(0, 0, 0, 4);
                button.MaxWidth = 286;

                if (current)
                    button.SetResourceReference(Button.BorderBrushProperty, "DsAccent");

                var captured = dev;
                button.Click += (_, _) =>
                {
                    if (selectedPid == 0)
                    {
                        SetStatus("сначала выбери приложение слева");
                        return;
                    }

                    if (!router.RoutingAvailable)
                    {
                        SetStatus(router.Error ?? "эта сборка Windows не умеет переключать устройства приложений");
                        return;
                    }

                    var ok = router.Route(selectedPid, captured.Id);
                    SetStatus(ok
                        ? $"{selectedName} → {captured.Name}. Приложение переедет при следующем запуске звука."
                        : "не получилось: " + (router.Error ?? "Windows отказала"));
                    RedrawDevices();
                };

                deviceRows.Children.Add(button);
            }

            if (devices.Count == 0)
                deviceRows.Children.Add(new TextBlock { Style = (Style)FindResource("WSub"), Text = "устройств вывода нет" });

            // Кнопка сброса привязки.
            if (selectedPid != 0 && !string.IsNullOrEmpty(router.GetAppDevice(selectedPid)))
            {
                var reset = WBtn("сбросить на системное", "Вернуть приложение на устройство по умолчанию");
                reset.Margin = new Thickness(0, 2, 0, 0);
                reset.Click += (_, _) =>
                {
                    router.Route(selectedPid, null);
                    SetStatus($"{selectedName} снова звучит туда, куда указывает Windows");
                    RedrawDevices();
                };
                deviceRows.Children.Add(reset);
            }
        }

        volume.ValueChanged += (_, e) =>
        {
            if (suppressVolume || selectedPid == 0) return;
            var percent = (float)(e.NewValue / 100.0);
            router.SetVolume(selectedPid, percent);
            volumeLabel.Text = $"{e.NewValue:F0}%";
        };

        var refresh = new Button
        {
            Style = (Style)FindResource("WRound"),
            Content = "↻",
            Width = 26,
            Height = 26,
            ToolTip = "Обновить список приложений и устройств",
        };
        refresh.Click += (_, _) =>
        {
            RedrawApps();
            RedrawDevices();
            SetStatus(string.Empty);
        };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("WTitle"),
            Text = "АУДИО",
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(refresh, 1);
        head.Children.Add(refresh);

        var left = new StackPanel { Width = 258 };
        left.Children.Add(head);
        left.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = "колесо над строкой — громкость",
            Margin = new Thickness(0, 2, 0, 8),
        });
        left.Children.Add(appRows);

        var right = new StackPanel { MinWidth = 240, Margin = new Thickness(14, 0, 0, 0) };
        right.Children.Add(selectedTitle);
        right.Children.Add(selectedHint);
        right.Children.Add(volume);
        right.Children.Add(volumeLabel);
        right.Children.Add(new TextBlock
        {
            Style = (Style)FindResource("WSub"),
            Text = "ВЫВОД ЗВУКА",
            Margin = new Thickness(0, 12, 0, 6),
        });
        right.Children.Add(deviceRows);
        right.Children.Add(status);

        var body = new StackPanel { Orientation = Orientation.Horizontal };
        body.Children.Add(left);
        body.Children.Add(right);

        RedrawApps();
        RedrawDevices();

        // Список обновляем не на каждый тик: 2 секунды хватает, а перерисовка
        // строк мешала бы попасть колесом в нужную.
        Timer(2000, () =>
        {
            var apps = router.Apps();
            var sig = string.Join(";", apps.Select(a => $"{a.ProcessId}:{a.IsPlaying}:{a.Volume:F2}"));
            var devSig = string.Join(";", router.Devices().Select(d => d.Id + d.IsDefault));

            if (sig != appsSignature) RedrawApps();
            if (devSig != devicesSignature) RedrawDevices();
        });

        return body;
    }
}


