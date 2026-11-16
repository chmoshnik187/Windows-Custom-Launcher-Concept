namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core.Theming;
using DotShell.Island.Wpf.Controls;
using Microsoft.Win32;

/// <summary>
/// Окно «Темы»: список тем из папки тем и встроенных скинов, применение, экспорт/импорт.
///
/// Окно не меняет ничего само — смена идёт через <see cref="ThemeBus.ApplyTheme"/>,
/// а визуал получает изменения через шину, как и для обоев по времени.
/// </summary>
public sealed class ThemePickerWindow : Window
{
    private static ThemePickerWindow? _open;

    private readonly ThemeBus _theme;
    private readonly List<ThemeItem> _items = new();
    private ThemeItem? _selected;
    private readonly TextBlock _status;

    public static void Toggle()
    {
        if (_open is { IsLoaded: true })
        {
            if (_open.WindowState == WindowState.Minimized) _open.WindowState = WindowState.Normal;
            _open.Activate();
            return;
        }

        var window = new ThemePickerWindow();
        _open = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_open, window)) _open = null; };
        window.Show();
    }

    public ThemePickerWindow()
    {
        _theme = ShellServices.Instance.Theme;

        Title = "DotShell — темы";
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
            Text = "ТЕМЫ",
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
            Text = "Темы",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "DsText");
        heading.Children.Add(title);

        var subtitle = new TextBlock
        {
            Text = "Один JSON-файл — вся тема: акцент, сетка точек, движение. Скины — это мини-темы.",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        };
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        heading.Children.Add(subtitle);

        content.Children.Add(heading);

        _status = new TextBlock
        {
            Text = "нет тем — есть встроенные скины",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 10,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
        content.Children.Add(_status);

        var list = new ListBox
        {
            Height = 260,
            Width = 660,
            SelectionMode = SelectionMode.Single,
            DisplayMemberPath = "Name",
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE)),
            Padding = new Thickness(4),
        };
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(ListBoxItem.PaddingProperty, new Thickness(6, 4, 6, 4)));
        itemStyle.Setters.Add(new Setter(ListBoxItem.BackgroundProperty, Brushes.Transparent));
        itemStyle.Setters.Add(new Setter(ListBoxItem.ForegroundProperty,
            new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEE))));
        itemStyle.Setters.Add(new Setter(ListBoxItem.HorizontalContentAlignmentProperty,
            HorizontalAlignment.Left));
        list.ItemContainerStyle = itemStyle;
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ThemeItem item) _selected = item;
            else _selected = null;
            _status.Text = _selected is null ? "ничего не выбрано" : _selected.Name;
        };
        list.ItemsSource = _items;
        content.Children.Add(list);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var apply = FlatButton("ПРИМЕНИТЬ", true, Apply, 10, new Thickness(12, 5, 12, 5));
        var export = FlatButton("ЭКСПОРТ", false, Export, 10, new Thickness(12, 5, 12, 5));
        var close = FlatButton("ЗАКРЫТЬ", false, Close, 10, new Thickness(12, 5, 12, 5));
        buttons.Children.Add(apply.Button);
        buttons.Children.Add(export.Button);
        buttons.Children.Add(close.Button);
        content.Children.Add(buttons);

        layer.Children.Add(content);
        root.Child = layer;
        Content = root;

        LoadThemes();
        Loaded += (_, _) => CenterOnAnchorMonitor();
    }

    private void LoadThemes()
    {
        _items.Clear();
        foreach (var m in ThemeStore.List())
        {
            _items.Add(new ThemeItem(m.Id, m.Name, m.Author, m.Version, m.Path, null));
        }

        foreach (var s in Skins.All)
        {
            _items.Add(new ThemeItem(s.Id, s.Name, "встроенный скин", 1, null, s));
        }

        _status.Text = _items.Count > 0 ? $"{_items.Count} тем" : "нет тем — есть встроенные скины";
    }

    private void Apply()
    {
        if (_selected is null)
        {
            _status.Text = "выбери тему из списка";
            return;
        }

        if (_selected.Skin is not null)
        {
            _theme.ApplySkin(_selected.Skin, "theme");
            _status.Text = "применён скин: " + _selected.Name;
            return;
        }

        if (string.IsNullOrWhiteSpace(_selected.Path))
        {
            _status.Text = "для этого пункта нет файла темы";
            return;
        }

        var spec = ThemeStore.Load(_selected.Path);
        _theme.ApplyTheme(spec, "theme");
        _status.Text = "применена тема: " + _selected.Name;
    }

    private void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт темы",
            Filter = "Тема DotShell (*.json)|*.json|Все файлы (*.*)|*.*",
            FileName = "dotsh-theme",
        };
        if (dialog.ShowDialog(this) != true) return;

        var spec = new ThemeSpec
        {
            Name = "Моя тема",
            Author = "DotShell",
            Version = 1,
            Colors = new ThemeColors
            {
                Accent = _theme.AccentHex,
                Background = "#000000",
                Surface = "#121214",
                SurfaceAlt = "#17171A",
                Border = "#26262B",
                Text = "#ECECEE",
                TextMuted = "#8A8A93",
                GhostDot = "#FFFFFF14",
            },
            Dot = new ThemeDot
            {
                Cell = _theme.CellSize,
                Gap = _theme.Gap,
                Fill = 0.86,
                MinRadius = 0.16,
                Threshold = 0.07,
            },
            Motion = new ThemeMotion
            {
                IslandExpandMs = 420,
                Easing = "cubic-bezier(0.2, 0.9, 0.2, 1)",
                PanelFadeMs = 220,
            },
            Widgets = new ThemeWidgets
            {
                Collapsed = new[] { "clock", "media", "cpu", "battery" },
                Tabs = new[] { "notes", "clipboard", "eq", "system", "theme" },
                LauncherHotkey = "Ctrl+Space",
                ClipboardHotkey = "Ctrl+Shift+V",
            },
        };
        spec.Save(dialog.FileName);
        _status.Text = "тема сохранена: " + dialog.FileName;
    }

    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Тема DotShell",
            Filter = "Тема DotShell (*.json)|*.json|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true) return;

        var spec = ThemeSpec.Load(dialog.FileName);
        _theme.ApplyTheme(spec, "import");
        _status.Text = "тема применена: " + spec.Name;
    }

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

    private sealed record ThemeItem(string Id, string Name, string Author, int Version, string? Path, Skin? Skin)
    {
        // UIA-имя пункта списка берётся из ToString: без этого экранный диктор
        // читал бы весь дамп записи вместо названия темы.
        public override string ToString() => Name;
    }
}
