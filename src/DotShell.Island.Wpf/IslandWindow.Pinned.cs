namespace DotShell.Island.Wpf;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Time;
using DotShell.Core.Settings;
using DotShell.Core.Launcher;
using DotShell.Island.Wpf.Widgets;

public partial class IslandWindow
{
    // ================================================================
    //  Главная: мои программы (плитки) и выбор из индекса
    // ================================================================

private string? _openGroupId;

private const string PinnedDragFormat = "DotShell.PinnedTarget";

/// <summary>
/// Плитки «мои программы»: сначала полка групп, внутри группы — её программы.
///
/// Владелец просил не плоский список, а группировки, и не как в проводнике:
/// поэтому группа — это плитка с именем, счётчиком и превью иконок, а внутри
/// неё те же квадратные плитки программ. Ярлык можно притащить мышкой прямо
/// из проводника или с рабочего стола — группа подсветится и примет его.
/// </summary>
private void RebuildPinnedTiles()
{
    if (PinnedWrap is null) return;

    PinnedWrap.Children.Clear();

        // Без этой строки каждая перестройка плиток добавляла записи в _groupDots:
        // тик раз в секунду опрашивал процессы по удалённым группам, а старые
        // плитки не освобождались (эллипс держит родителя).
        _groupDots.Clear();
    var store = ShellServices.Instance.Pinned;

    // Группу могли удалить, пока она была открыта.
    if (_openGroupId is not null && store.FindGroup(_openGroupId) is null) _openGroupId = null;

    BuildGroupShelf(store);

    var hint = new TextBlock
    {
        Text = "перетащи ярлык с рабочего стола на группу — он останется в ней",
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 9,
        Width = 620,
        Margin = new Thickness(0, 2, 0, 0),
        TextWrapping = TextWrapping.Wrap,
    };
    hint.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    PinnedWrap.Children.Add(hint);
}

/// <summary>Полка групп: плитки-группы плюс плитка «новая группа».</summary>
private void BuildGroupShelf(PinnedStore store)
{
    foreach (var group in store.Groups)
    {
        var tile = GroupTile(group, store);
        PinnedWrap.Children.Add(tile);
    }

    PinnedWrap.Children.Add(NewGroupTile(store));
}
/// <summary>Плитка группы на полке.</summary>
private System.Windows.Controls.Border GroupTile(PinnedGroup group, PinnedStore store)
{
    // Плитка-«полка»: точечная подложка, крупный значок, имя типографикой и
    // счётчик акцентом. Раньше это была серая коробка с мелкими иконками —
    // аккуратно, но без характера, на что владелец и пожаловался.
    var tile = new System.Windows.Controls.Border
    {
        Width = 152,
        Height = 104,
        Margin = new Thickness(0, 0, 10, 10),
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        AllowDrop = true,
        ClipToBounds = true,
        ToolTip = $"{group.Name} · программ: {group.Items.Count}\nперетащи сюда ярлык · клик — открыть окном",
    };
    tile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DsSurfaceAlt");
    tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    var layer = new Grid();

    var backdrop = new DotShell.Island.Wpf.Controls.IslandBackdrop
    {
        Style = "dots",
        Strength = 0.65,
        Corner = 14,
        IsHitTestVisible = false,
    };
    layer.Children.Add(backdrop);

    var grid = new Grid { Margin = new Thickness(14, 12, 14, 12) };
    grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
    grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

    // Значок группы крупно — это её «лицо».
    var glyph = new TextBlock
    {
        Text = string.IsNullOrWhiteSpace(group.Glyph) ? "\uE8B7" : group.Glyph,
        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
        FontSize = 22,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    glyph.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
    grid.Children.Add(glyph);

    var count = new TextBlock
    {
        Text = group.Items.Count.ToString("00"),
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 26,
        FontWeight = FontWeights.Light,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, -4, 0, 0),
    };
    count.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
    grid.Children.Add(count);

    var name = new TextBlock
    {
        Text = group.Name,
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 10,
        FontWeight = FontWeights.SemiBold,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    name.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
    // Нижняя строка: имя и точка «в группе что-то запущено».
    var footer = new Grid();
    footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

    name.VerticalAlignment = VerticalAlignment.Center;
    Grid.SetColumn(name, 0);
    footer.Children.Add(name);

    var runDot = new System.Windows.Shapes.Ellipse
    {
        Width = 5,
        Height = 5,
        Margin = new Thickness(6, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Visibility = group.Items.Any(DotShell.Core.Launcher.ProcessProbe.IsRunning)
            ? Visibility.Visible
            : Visibility.Collapsed,
    };
    runDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "DsAccent");
    Grid.SetColumn(runDot, 1);
    footer.Children.Add(runDot);

    Grid.SetRow(footer, 1);
    grid.Children.Add(footer);
    _groupDots.Add((runDot, group));
    DotShell.Island.Wpf.Controls.Tap.Attach(tile);

    layer.Children.Add(grid);
    tile.Child = layer;

    tile.MouseEnter += (_, _) => tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsAccent");
    tile.MouseLeave += (_, _) => tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    tile.MouseLeftButtonUp += (_, e) =>
    {
        // Иначе клик долетит до таблетки, и она свернёт остров вместо открытия группы.
        e.Handled = true;
        new GroupWindow(group).Show();
    };

    var rename = new MenuItem { Header = "Переименовать группу…" };
    rename.Click += (_, _) => RenameGroupPrompt(store, group);
    var glyphPick = new MenuItem { Header = "Сменить значок" };
    glyphPick.Click += (_, _) => CycleGlyph(store, group);
    var remove = new MenuItem { Header = "Удалить группу" };
    remove.Click += (_, _) =>
    {
        store.RemoveGroup(group.Id);
        RebuildPinnedTiles();

    };
    tile.ContextMenu = new ContextMenu();
    tile.ContextMenu.Items.Add(rename);
    tile.ContextMenu.Items.Add(glyphPick);
    tile.ContextMenu.Items.Add(remove);

    WireDrop(tile, store, group.Id);
    return tile;
}

private System.Windows.Controls.Border NewGroupTile(PinnedStore store)
{
    var tile = new System.Windows.Controls.Border
    {
        Width = 92,
        Height = 92,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Создать группу",
    };
    tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

    var plus = new TextBlock
    {
        Text = "+",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 20,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    plus.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    tile.Child = plus;

    tile.MouseLeftButtonUp += (_, _) =>
    {
        var group = store.AddGroup($"ГРУППА {store.Groups.Count + 1}");
        store.SetLook(group.Id, glyph: "\uE8B7");
        // группа создана — сразу спрашиваем имя
        RebuildPinnedTiles();

        RenameGroupPrompt(store, group);
    };

    return tile;
}

/// <summary>Внутри группы: плитки программ, «назад» и «добавить».</summary>
private void BuildGroupApps(PinnedStore store, PinnedGroup group)
{
    var back = new System.Windows.Controls.Border
    {
        Width = 88,
        Height = 64,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Все группы",
    };
    back.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");
    var backText = new TextBlock
    {
        Text = "← ГРУППЫ",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 9,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    backText.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    back.Child = backText;
    back.MouseLeftButtonUp += (_, _) =>
    {
        _openGroupId = null;
        RebuildPinnedTiles();

    };
    PinnedWrap.Children.Add(back);

    foreach (var app in group.Items.ToList())
    {
        var row = _vm.PinnedItems.FirstOrDefault(r =>
            string.Equals(r.Target, app.Target, StringComparison.OrdinalIgnoreCase));

        var tile = new System.Windows.Controls.Border
        {
            Width = 88,
            Height = 64,
            Margin = new Thickness(0, 0, 8, 8),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = app.Target,
        };
        tile.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DsSurfaceAlt");
        tile.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var icon = row?.Icon ?? LoadIconFile(app.IconPath);
        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 26, Height = 26 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            stack.Children.Add(image);
        }
        else
        {
            var letter = new TextBlock
            {
                Text = app.Name.Length > 0 ? app.Name[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            letter.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
            stack.Children.Add(letter);
        }

        var label = new TextBlock
        {
            Text = app.Name,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            MaxWidth = 80,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 3, 0, 0),
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        stack.Children.Add(label);
        tile.Child = stack;

        if (row is not null) tile.MouseLeftButtonUp += (_, e) => { e.Handled = true; _vm.LaunchPinned(row); };

        // Перетаскивание плитки в другую группу.
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
            DragDrop.DoDragDrop(tile, new DataObject(PinnedDragFormat, app.Target), DragDropEffects.Move);
        };

        var assign = new MenuItem
        {
            Header = row is null || string.IsNullOrWhiteSpace(row.Hotkey)
                ? "Назначить клавишу…"
                : $"Клавиша: {row.Hotkey} — изменить",
        };
        if (row is not null) assign.Click += (_, _) => AssignHotkey(row);

        var unpin = new MenuItem { Header = "Убрать с острова" };
        unpin.Click += (_, _) =>
        {
            if (row is not null) _vm.Unpin(row);
            else { store.Remove(app); RebuildPinnedTiles(); }
        };

        var folder = new MenuItem { Header = "Папка программы" };
        folder.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{app.Target}\"") { UseShellExecute = true });
            }
            catch (Exception ex) { Log.Info($"open folder failed: {ex.Message}"); }
        };

        tile.ContextMenu = new ContextMenu();
        tile.ContextMenu.Items.Add(assign);
        tile.ContextMenu.Items.Add(new Separator());
        tile.ContextMenu.Items.Add(unpin);
        tile.ContextMenu.Items.Add(folder);

        PinnedWrap.Children.Add(tile);
    }

    var add = new System.Windows.Controls.Border
    {
        Width = 88,
        Height = 64,
        Margin = new Thickness(0, 0, 8, 8),
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Cursor = Cursors.Hand,
        Background = Brushes.Transparent,
        ToolTip = "Добавить программу из меню «Пуск»",
    };
    add.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "DsBorder");
    var plus = new TextBlock
    {
        Text = "+",
        FontFamily = new FontFamily("Bahnschrift"),
        FontSize = 20,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    plus.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
    add.Child = plus;
    add.MouseLeftButtonUp += (_, e) => { e.Handled = true; OpenPicker(); };
    PinnedWrap.Children.Add(add);
}

/// <summary>Приём файлов из проводника и плиток из других групп.</summary>
private void WireDrop(UIElement element, PinnedStore store, string groupId)
{
    element.AllowDrop = true;

    element.DragOver += (_, e) =>
    {
        bool can = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(PinnedDragFormat);
        e.Effects = can ? DragDropEffects.Copy : DragDropEffects.None;
        if (can) element.Opacity = 0.75;
        e.Handled = true;
    };

    element.DragLeave += (_, _) => element.Opacity = 1;

    element.Drop += (_, e) =>
    {
        element.Opacity = 1;
        e.Handled = true;

        if (e.Data.GetData(PinnedDragFormat) is string dragged)
        {
            var app = store.Items.FirstOrDefault(a =>
                string.Equals(a.Target, dragged, StringComparison.OrdinalIgnoreCase));
            if (app is not null) store.MoveToGroup(app, groupId);
            RebuildPinnedTiles();

            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        foreach (var path in files)
            if (TryAddFile(path, store, groupId)) added++;

        _vm.StatusLine = added > 0
            ? $"добавлено в группу: {added}"
            : "это не программа — нужен ярлык или .exe";
        RebuildPinnedTiles();

    };
}

/// <summary>Добавить файл из проводника: ярлык, exe, bat, url.</summary>
private bool TryAddFile(string path, PinnedStore store, string groupId)
{
    try
    {
        if (Directory.Exists(path)) return false;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".lnk" or ".exe" or ".bat" or ".cmd" or ".url" or ".msc")) return false;

        var name = Path.GetFileNameWithoutExtension(path);
        store.Add(name, path, ext == ".lnk" ? path : string.Empty, groupId);
        return true;
    }
    catch (Exception ex)
    {
        Log.Info($"drop add failed: {ex.Message}");
        return false;
    }
}

/// <summary>Перебор значков группы: у каждой полки должно быть своё «лицо».</summary>
private void CycleGlyph(PinnedStore store, PinnedGroup group)
{
    string[] glyphs =
    {
        "\uE8B7", "\uE7FC", "\uE8D6", "\uE7C4", "\uE9D9", "\uE713",
        "\uE721", "\uE787", "\uE8F1", "\uEC4F", "\uE706", "\uE81C",
    };

    int index = Array.IndexOf(glyphs, group.Glyph);
    var next = glyphs[(index + 1 + glyphs.Length) % glyphs.Length];
    store.SetLook(group.Id, glyph: next);
    RebuildPinnedTiles();
}

private void RenameGroupPrompt(PinnedStore store, PinnedGroup group)
{
    var dialog = new TextPromptWindow("ИМЯ ГРУППЫ", group.Name, name =>
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        store.RenameGroup(group.Id, name);
        RebuildPinnedTiles();

    });
    dialog.ShowDialog();
}

private static BitmapSource? LoadIconFile(string path)
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


    /// <summary>Закрепить выбранную программу сразу в открытую группу.</summary>
    private void PinIntoOpenGroup(LauncherRow row)
    {
        _vm.PinFromPicker(row);
        if (_openGroupId is null) return;

        var store = ShellServices.Instance.Pinned;
        var app = store.Items.FirstOrDefault(a =>
            string.Equals(a.Target, row.Target, StringComparison.OrdinalIgnoreCase));
        if (app is not null) store.MoveToGroup(app, _openGroupId);

        RebuildPinnedTiles();

    }
    private void OpenPicker()
    {
        PickerPanel.Visibility = Visibility.Visible;
        _vm.PickerOpen();
        PickerSearch.Focus();
    }

    private void OnPickerSearch(object sender, TextChangedEventArgs e) => _vm.SearchPicker(PickerSearch.Text);

    private void OnPickerPin(object sender, MouseButtonEventArgs e)
    {
        if (PickerList.SelectedItem is LauncherRow row) PinIntoOpenGroup(row);
    }

    private void OnPickerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && PickerList.SelectedItem is LauncherRow row)
        {
            _vm.PinFromPicker(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ClosePicker();
            e.Handled = true;
        }
    }

    private void OnPickerDone(object sender, RoutedEventArgs e) => ClosePicker();

    private void ClosePicker()
    {
        PickerPanel.Visibility = Visibility.Collapsed;
        PickerSearch.Text = string.Empty;
    }
}
