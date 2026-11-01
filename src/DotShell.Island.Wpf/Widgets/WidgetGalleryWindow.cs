namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DotShell.Core.Native;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Витрина виджетов: вместо контекстного меню со списком — окно с карточками.
///
/// Зачем: список в меню не показывает, ЧТО ты ставишь, и не даёт места —
/// виджет появляется там, где решил код. Здесь карточка с иконкой, названием
/// и описанием, её можно **потянуть на рабочий стол** — виджет встанет ровно
/// под курсором. Клик без перетаскивания включает/выключает виджет.
///
/// Окно живёт по правилам шелла: без рамки, попиксельная прозрачность,
/// закрывается по Esc и по потере фокуса.
/// </summary>
public sealed class WidgetGalleryWindow : Window
{
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static readonly FontFamily Display = new("Bahnschrift");

    private readonly WrapPanel _grid;
    private readonly TextBlock _status;

    // Состояние перетаскивания одной карточки.
    private string? _dragType;
    private Point _dragStart;
    private bool _dragging;

    public WidgetGalleryWindow()
    {
        Title = "DotShell — витрина виджетов";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        var root = new Border
        {
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Margin = new Thickness(14),
        };
        root.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        root.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var content = new StackPanel { Width = 690 };

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel();
        title.Children.Add(Micro("ВИТРИНА ВИДЖЕТОВ", 9, 0.3, "DsText"));
        title.Children.Add(Micro("потяни карточку на рабочий стол · клик — вкл/выкл · Esc — закрыть", 9, 0.02, "DsTextMuted", new Thickness(0, 6, 0, 0)));
        head.Children.Add(title);

        var close = new Button
        {
            Content = "✕",
            Width = 26,
            Height = 26,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Foreground = Res("DsTextMuted"),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Template = FlatTemplate(),
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        head.Children.Add(close);

        content.Children.Add(head);

        _grid = new WrapPanel { Margin = new Thickness(0, 16, 0, 6) };
        content.Children.Add(_grid);

        _status = new TextBlock
        {
            FontFamily = Display,
            FontSize = 9,
            Foreground = Res("DsTextMuted"),
            Margin = new Thickness(0, 4, 0, 0),
        };
        content.Children.Add(_status);

        root.Child = content;
        Content = root;

        WidgetsManager.Changed += OnWidgetsChanged;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Deactivated += (_, _) => Close();
        Closed += (_, _) => WidgetsManager.Changed -= OnWidgetsChanged;

        Rebuild();
    }

    /// <summary>Показать витрину по центру верхней части экрана.</summary>
    public void ShowGallery()
    {
        UpdateLayout();

        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + 90;

        Show();
        Activate();
        Focus();
    }

    private void OnWidgetsChanged() => Dispatcher.BeginInvoke(Rebuild);

    // ------------------------------------------------------------------
    //  Карточки
    // ------------------------------------------------------------------

    private void Rebuild()
    {
        _grid.Children.Clear();

        int onDesktop = 0;
        foreach (var type in WidgetsManager.Types)
        {
            bool active = WidgetsManager.Has(type);
            if (active) onDesktop++;
            _grid.Children.Add(Card(type, active));
        }

        _status.Text = onDesktop == 0
            ? "на рабочем столе пока пусто"
            : $"на рабочем столе: {onDesktop} из {WidgetsManager.Types.Length}";
    }

    private UIElement Card(string type, bool active)
    {
        var card = new Border
        {
            Width = 214,
            MinHeight = 108,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14, 12, 14, 12),
            CornerRadius = new CornerRadius(2),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Tag = type,
        };
        card.SetResourceReference(Border.BackgroundProperty, active ? "DsAccentGhost" : "DsSurfaceAlt");
        card.SetResourceReference(Border.BorderBrushProperty, active ? "DsAccent" : "DsBorder");

        var stack = new StackPanel();

        var glyph = new TextBlock
        {
            Text = Glyph(type),
            FontFamily = IconFont,
            FontSize = 20,
            Foreground = active ? Res("DsAccent") : Res("DsText"),
            Margin = new Thickness(0, 0, 0, 10),
        };
        stack.Children.Add(glyph);

        stack.Children.Add(new TextBlock
        {
            Text = WidgetsManager.Label(type),
            FontFamily = Display,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Res("DsText"),
            TextWrapping = TextWrapping.Wrap,
        });

        stack.Children.Add(new TextBlock
        {
            Text = Hint(type),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9.5,
            Foreground = Res("DsTextMuted"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        if (active)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "НА СТОЛЕ",
                FontFamily = Display,
                FontSize = 8,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("DsAccent"),
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        card.Child = stack;

        card.MouseLeftButtonDown += (_, e) =>
        {
            _dragType = type;
            _dragStart = e.GetPosition(this);
            _dragging = false;
            card.CaptureMouse();
            e.Handled = true;
        };

        card.MouseMove += (_, e) =>
        {
            if (_dragType != type || e.LeftButton != MouseButtonState.Pressed) return;

            var pos = e.GetPosition(this);
            if (!_dragging)
            {
                if (Math.Abs(pos.X - _dragStart.X) < 6 && Math.Abs(pos.Y - _dragStart.Y) < 6) return;
                _dragging = true;
                card.Opacity = 0.55;
                Cursor = Cursors.Cross;
            }
        };

        card.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragType != type) return;

            card.ReleaseMouseCapture();
            _dragType = null;
            Cursor = Cursors.Arrow;
            card.Opacity = 1;

            if (!_dragging)
            {
                // Клик: включить/выключить на столе.
                WidgetsManager.Toggle(type);
                return;
            }

            _dragging = false;
            DropAtCursor(type);
        };

        return card;
    }

    /// <summary>
    /// Создать виджет ровно там, где отпустили мышь. Курсор отдаёт физические
    /// пиксели, а окна WPF живут в DIP — переводим через масштаб монитора.
    /// </summary>
    private void DropAtCursor(string type)
    {
        if (!Win32.GetCursorPos(out var screen)) return;

        double scale = 1.0;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget is not null) scale = source.CompositionTarget.TransformToDevice.M11;
        }
        catch { }

        int x = (int)Math.Round(screen.X / scale);
        int y = (int)Math.Round(screen.Y / scale);

        // Не даём уронить виджет за пределы экрана.
        var wa = SystemParameters.WorkArea;
        x = (int)Math.Clamp(x, wa.Left, wa.Right - 80);
        y = (int)Math.Clamp(y, wa.Top, wa.Bottom - 60);

        WidgetsManager.Close(type); // если уже стоит — снимаем и ставим в новое место
        WidgetsManager.OpenWidget(type, x, y);
        Close();
    }

    // ------------------------------------------------------------------
    //  Мелочи
    // ------------------------------------------------------------------

    private TrackedText Micro(string text, double size, double tracking, string brushKey, Thickness? margin = null)
    {
        var control = new TrackedText
        {
            Text = text,
            FontSize = size,
            Tracking = tracking,
            Brush = Res(brushKey),
            Margin = margin ?? new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        return control;
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

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;

    /// <summary>Иконка виджета (Segoe Fluent Icons).</summary>
    private static string Glyph(string type) => type switch
    {
        "clock" => "\uE916",
        "eq" => "\uE9E9",
        "media" => "\uE768",
        "system" => "\uE950",
        "units" => "\uE77B",
        "settings" => "\uE713",
        "clean" => "\uE74D",
        "dupes" => "\uE8C8",
        "update" => "\uE895",
        "windows" => "\uE8A7",
        "audio" => "\uE767",
        "currency" => "\uE8C7",
        "tray" => "\uE7F4",
        "weather" => "\uE706",
        _ => "\uE946",
    };

    /// <summary>Одна строка «что это даёт».</summary>
    private static string Hint(string type) => type switch
    {
        "clock" => "время точечной матрицей",
        "eq" => "спектр из настоящего звука",
        "media" => "трек, обложка, кнопки",
        "system" => "загрузка CPU и память",
        "units" => "существа, следящие за курсором",
        "settings" => "найти спрятанную настройку Windows",
        "clean" => "сколько мусора и кнопка чистки",
        "dupes" => "поиск копий файлов и картинок",
        "update" => "статус обновлений Windows",
        "windows" => "живые окна: превью, переход, закрыть",
        "audio" => "приложение → устройство вывода",
        "tray" => "значки из области уведомлений, вместо стрелки",
        "weather" => "температура и состояние, без ключей",
        "currency" => "доллар и юань к рублю, с графиком",
        _ => string.Empty,
    };
}





