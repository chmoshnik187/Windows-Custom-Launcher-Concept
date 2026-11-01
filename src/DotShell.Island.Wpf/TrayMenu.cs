namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

/// <summary>
/// Меню трея как собственное окно, а не WPF-попап.
///
/// Почему так. С `Shell_NotifyIcon` попап не держится: дважды проверено — меню
/// открывается и закрывается само, потому что у скрытого окна трея нет нормальной
/// активации. Своё окно решает вопрос иначе: это обычное topmost-окно без рамки,
/// ему не нужен ни владелец, ни фокус — оно само ловит клики и само закрывается
/// по потере активации или Esc.
///
/// Вид — тот же язык, что у остальных меню: тёмная карточка, тонкая рамка,
/// акцентная подсветка строки, галочка у переключаемых пунктов.
/// </summary>
public sealed class TrayMenu : Window
{
    private readonly StackPanel _rows = new();
    private readonly List<TrayMenuItem> _items = new();

    /// <summary>Вызывается перед показом: пункты можно скрыть или переименовать.</summary>
    public event Action? Opening;

    public TrayMenu()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            MinWidth = 220,
        };
        card.SetResourceReference(Border.BackgroundProperty, "DsSurface");
        card.SetResourceReference(Border.BorderBrushProperty, "DsBorder");
        card.Child = _rows;
        Content = card;

        Deactivated += (_, _) => Close();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Обычный пункт. Возвращает ручку, чтобы менять видимость и подпись.</summary>
    public TrayMenuItem Add(string header, Action action)
    {
        var item = new TrayMenuItem(header, action);
        _items.Add(item);
        _rows.Children.Add(BuildRow(item));
        return item;
    }

    public TrayMenuItem AddCheckable(string header, bool isChecked, Action action)
    {
        var item = new TrayMenuItem(header, action) { IsCheckable = true, IsChecked = isChecked };
        _items.Add(item);
        _rows.Children.Add(BuildRow(item));
        return item;
    }

    public void AddSeparator()
    {
        var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
        line.SetResourceReference(Border.BackgroundProperty, "DsBorder");
        _rows.Children.Add(line);
    }

    private UIElement BuildRow(TrayMenuItem item)
    {
        var row = new Border
        {
            Padding = new Thickness(10, 7, 12, 7),
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        var grid = new Grid();

        var label = new TextBlock
        {
            Text = item.Header,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        grid.Children.Add(label);

        var tick = new TextBlock
        {
            Text = "✓",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Margin = new Thickness(0, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = item.IsCheckable && item.IsChecked ? Visibility.Visible : Visibility.Collapsed,
        };
        tick.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        grid.Children.Add(tick);

        // Галочка слева, текст справа: без этого текст прыгает при переключении.
        if (item.IsCheckable)
        {
            var withTick = new StackPanel { Orientation = Orientation.Horizontal };
            withTick.Children.Add(tick);
            withTick.Children.Add(label);
            grid.Children.Clear();
            grid.Children.Add(withTick);
        }

        row.Child = grid;

        row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "DsSurfaceAlt");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            item.Update(_ => { });
            if (item.IsCheckable) item.IsChecked = !item.IsChecked;

            var action = item.Action;
            Close();
            action?.Invoke();
        };

        item.Row = row;
        item.Label = label;
        item.Tick = tick;

        return row;
    }

    /// <summary>Показать у курсора. Координаты окна — в DIP, курсор — в пикселях.</summary>
    public void ShowAtCursor()
    {
        Opening?.Invoke();

        // Скрытые пункты не должны занимать место.
        foreach (var item in _items)
            if (item.Row is not null)
                item.Row.Visibility = item.IsVisible ? Visibility.Visible : Visibility.Collapsed;

        Show();

        var point = CursorPosition();
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var area = SystemParameters.WorkArea;
        Left = Math.Min(point.X / scale, area.Right - ActualWidth - 4);
        Top = Math.Min(point.Y / scale, area.Bottom - ActualHeight - 4);

        Activate();
        Focus();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out PointStruct point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X, Y;
    }

    private static (double X, double Y) CursorPosition()
    {
        try
        {
            GetCursorPos(out var point);
            return (point.X, point.Y);
        }
        catch { return (400, 400); }
    }
}

/// <summary>Пункт меню трея: подпись, действие, состояние.</summary>
public sealed class TrayMenuItem
{
    public TrayMenuItem(string header, Action? action)
    {
        Header = header;
        Action = action;
    }

    public string Header { get; internal set; }
    public Action? Action { get; }
    public bool IsCheckable { get; set; }
    public bool IsChecked { get; set; }
    public bool IsVisible { get; set; } = true;

    internal Border? Row { get; set; }
    internal TextBlock? Label { get; set; }
    internal TextBlock? Tick { get; set; }

    /// <summary>Переименовать и обновить галочку после изменения состояния.</summary>
    public void Update(Action<TrayMenuItem> change)
    {
        change(this);

        if (Label is not null) Label.Text = Header;
        if (Tick is not null)
            Tick.Visibility = IsCheckable && IsChecked ? Visibility.Visible : Visibility.Collapsed;
    }
}
