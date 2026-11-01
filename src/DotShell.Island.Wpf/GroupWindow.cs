namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core;
using DotShell.Core.Launcher;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Группа программ отдельным окном.
///
/// Почему окно, а не разворот внутри острова: в острове тесно — 740 пикселей
/// ширины на всё, и группа из двенадцати программ превращала его в длинную
/// простыню. Отдельное окно можно растянуть, бросить в него ярлык из проводника
/// и не бояться, что клик по плитке свернёт остров.
///
/// Вид — как у полки Nothing: крупный значок группы в акценте, имя тайпографикой
/// Bahnschrift, точечная подложка, а ниже — сетка плиток с иконками.
/// </summary>
public sealed class GroupWindow : Window
{
    private readonly PinnedGroup _group;
    private readonly WrapPanel _grid;
    private readonly TextBlock _counter;
    private readonly TextBlock _title;
    private readonly System.Windows.Shapes.Ellipse? _none;

    /// <summary>Точки «программа работает»: пересчитываются таймером.</summary>
    private readonly List<(System.Windows.Shapes.Ellipse Dot, PinnedApp App)> _runDots = new();

    public GroupWindow(PinnedGroup group)
    {
        _group = group;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.WidthAndHeight;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = $"DotShell — {group.Name}";

        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            Margin = new Thickness(14),
            Width = 560,
            ClipToBounds = true,
            AllowDrop = true,
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var layer = new Grid();

        // Точечная подложка — тот же движок, что в острове, только тише.
        var backdrop = new IslandBackdrop { Style = "dots", Strength = 0.5, Corner = 16, IsHitTestVisible = false };
        layer.Children.Add(backdrop);

        var content = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };

        // ---- шапка ----
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border
        {
            Width = 54,
            Height = 54,
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            VerticalAlignment = VerticalAlignment.Top,
        };
        badge.SetResourceReference(Border.BorderBrushProperty, "DsAccent");

        var glyph = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(group.Glyph) ? "\uE8B7" : group.Glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        badge.Child = glyph;
        head.Children.Add(badge);

        var titles = new StackPanel { Margin = new Thickness(14, 2, 0, 0) };

        _title = new TextBlock
        {
            Text = group.Name,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        titles.Children.Add(_title);

        var micro = new TrackedText
        {
            Text = "ГРУППА",
            FontSize = 8,
            Tracking = 0.34,
            Margin = new Thickness(1, 4, 0, 0),
        };
        micro.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        titles.Children.Add(micro);

        _counter = new TextBlock
        {
            Text = "0",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 10,
            Margin = new Thickness(1, 6, 0, 0),
        };
        _counter.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        titles.Children.Add(_counter);

        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);

        var close = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = FlatTemplate(),
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        head.Children.Add(close);

        content.Children.Add(head);

        // ---- сетка программ ----
        _grid = new WrapPanel { Margin = new Thickness(0, 18, 0, 0) };
        content.Children.Add(_grid);

        var hint = new TrackedText
        {
            Text = "ПЕРЕТАЩИ СЮДА ЯРЛЫК ИЛИ ПРОГРАММУ · ПРАВЫЙ КЛИК — КЛАВИША, ПАПКА, УБРАТЬ",
            FontSize = 8,
            Tracking = 0.28,
            Margin = new Thickness(1, 14, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        hint.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        content.Children.Add(hint);

        layer.Children.Add(content);
        root.Child = layer;
        Content = root;

        root.DragOver += OnDragOver;
        root.DragLeave += (_, _) => root.BorderBrush = (Brush)FindResource("DsBorder");
        root.Drop += OnDrop;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        ShellServices.Instance.Pinned.Changed += OnStoreChanged;
        Closed += (_, _) => ShellServices.Instance.Pinned.Changed -= OnStoreChanged;

        Rebuild();

        // Точки «работает»: раз в 3 секунды. Запуск и выход программы
        // случаются вне нашего окна, событий нет — только опрос.
        var watchdog = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        watchdog.Tick += (_, _) => RefreshRunDots();
        watchdog.Start();
        Closed += (_, _) => watchdog.Stop();
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
    private void RefreshRunDots()
    {
        foreach (var (dot, app) in _runDots)
        {
            if (dot.Parent is not UIElement) continue;
            dot.Visibility = DotShell.Core.Launcher.ProcessProbe.IsRunning(app)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void OnStoreChanged() => Dispatcher.BeginInvoke(Rebuild);

    private void Rebuild()
    {
        _grid.Children.Clear();

        var items = _group.Items.ToList();
        _counter.Text = items.Count switch
        {
            0 => "ПУСТО",
            1 => "1 ПРОГРАММА",
            _ => $"{items.Count} ПРОГРАММ",
        };

        foreach (var app in items) _grid.Children.Add(AppTile(app));

        if (items.Count == 0)
        {
            _grid.Children.Add(new TextBlock
            {
                Text = "здесь пока ничего нет",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = (Brush)FindResource("DsTextMuted"),
                Margin = new Thickness(2, 6, 0, 0),
            });
        }
    }

    private UIElement AppTile(PinnedApp app)
    {
        var tile = new Border
        {
            Width = 84,
            Height = 84,
            Margin = new Thickness(0, 0, 10, 10),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = app.Target,
            Background = Brushes.Transparent,
        };
        tile.SetResourceReference(Border.BorderBrushProperty, "DsBorder");
        DotShell.Island.Wpf.Controls.Tap.Attach(tile);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Монохром, как в Nothing: программы отличаются силуэтом, а не цветом.
        // Папки (цель — каталог) рисуем значком папки, чтобы не путать с программами.
        if (Directory.Exists(app.Target))
        {
            var folder = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            folder.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
            stack.Children.Add(folder);
        }
        else
        {
        var icon = LoadIcon(DotShell.Core.Launcher.IconCache.Monochrome(app.IconPath));
        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 30, Height = 30 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            stack.Children.Add(image);
        }
        else
        {
            var letter = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            letter.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
            stack.Children.Add(letter);
        }
        }

        var label = new TextBlock
        {
            Text = app.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            MaxWidth = 74,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        stack.Children.Add(label);

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 5,
            Height = 5,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = DotShell.Core.Launcher.ProcessProbe.IsRunning(app) ? Visibility.Visible : Visibility.Collapsed,
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DsAccent");
        stack.Children.Add(dot);
        _runDots.Add((dot, app));


        // Без этого стек не попадал в плитку, и она рисовалась пустой.
        tile.Child = stack;

        tile.MouseEnter += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "DsAccent");
        tile.MouseLeave += (_, _) => tile.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        // Программу удалили или перенесли: плитка остаётся, но видно, что она
        // «мертва», и клик ничего не запускает. Раньше запись тихо исчезала.
        if (!app.Exists)
        {
            tile.Opacity = 0.38;
            tile.Cursor = Cursors.No;
            tile.ToolTip = $"файл не найден:\n{app.Target}";
            return tile;
        }

        tile.MouseLeftButtonUp += (_, _) => Launch(app);

        // Перетаскивание в другую группу — через остров (там их полка).
        Point start = default;
        bool pressed = false;
        tile.PreviewMouseLeftButtonDown += (_, e) => { start = e.GetPosition(this); pressed = true; };
        tile.PreviewMouseLeftButtonUp += (_, _) => pressed = false;
        tile.MouseMove += (_, e) =>
        {
            if (!pressed || e.LeftButton != MouseButtonState.Pressed) return;
            var now = e.GetPosition(this);
            if (Math.Abs(now.X - start.X) < 6 && Math.Abs(now.Y - start.Y) < 6) return;
            pressed = false;
            DragDrop.DoDragDrop(tile, new DataObject("DotShell.PinnedTarget", app.Target), DragDropEffects.Move);
        };

        var assign = new MenuItem { Header = "Назначить клавишу…" };
        assign.Click += (_, _) => _ = new HotkeyCaptureWindow(app.Name, app.Hotkey, combo =>
        {
            if (combo is null) return;
            ShellServices.Instance.Pinned.SetHotkey(app.Target, combo);
        }).ShowDialog();

        var open = new MenuItem { Header = "Папка программы" };
        open.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.Target}\"")
                    { UseShellExecute = true });
            }
            catch { }
        };

        var remove = new MenuItem { Header = "Убрать из группы" };
        remove.Click += (_, _) => ShellServices.Instance.Pinned.Remove(app);

        tile.ContextMenu = new ContextMenu();
        tile.ContextMenu.Items.Add(assign);
        tile.ContextMenu.Items.Add(open);
        tile.ContextMenu.Items.Add(new Separator());
        tile.ContextMenu.Items.Add(remove);

        return tile;
    }

    private void Launch(PinnedApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info($"group launch failed: {ex.Message}"); }
    }

    // ------------------------------------------------------------------
    //  Приём ярлыков из проводника
    // ------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool can = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent("DotShell.PinnedTarget");
        e.Effects = can ? DragDropEffects.Copy : DragDropEffects.None;
        if (can) ((Border)sender).SetResourceReference(Border.BorderBrushProperty, "DsAccent");
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ((Border)sender).SetResourceReference(Border.BorderBrushProperty, "DsBorder");
        e.Handled = true;

        if (e.Data.GetData("DotShell.PinnedTarget") is string dragged)
        {
            var store = ShellServices.Instance.Pinned;
            var app = store.Items.FirstOrDefault(a =>
                string.Equals(a.Target, dragged, StringComparison.OrdinalIgnoreCase));
            if (app is not null) store.MoveToGroup(app, _group.Id);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var target = ShellServices.Instance.Pinned;
        int added = 0;
        foreach (var path in files)
        {
            try
            {
                if (Directory.Exists(path)) continue;
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".lnk" or ".exe" or ".bat" or ".cmd" or ".url" or ".msc")) continue;

                target.Add(Path.GetFileNameWithoutExtension(path), path,
                    ext == ".lnk" ? path : string.Empty, _group.Id);
                added++;
            }
            catch (Exception ex) { Log.Info($"group drop failed: {ex.Message}"); }
        }

        if (added > 0) Rebuild();
    }

    private static BitmapSource? LoadIcon(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}