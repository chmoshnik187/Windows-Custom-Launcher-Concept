namespace DotShell.Island.Wpf;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Своё окно уведомлений вместо системных тостов.
///
/// Зачем: тост Windows живёт в стиле системы — шрифт, скругления, цвет — и рядом с
/// островом выглядит как чужой. Здесь то же самое, но своим языком: тёмная карточка,
/// акцентная черта слева, точечная микро-подпись и рисунок острова на фоне.
///
/// Окно не забирает фокус (ShowActivated=false, топмаст) и не мешает работать:
/// появилось справа внизу, повисело и ушло. Клик — закрыть сразу.
/// Три штуки максимум: очередь длиннее превращается в заслонку экрана.
/// </summary>
public static class NotificationHub
{
    private const int MaxVisible = 3;
    private const double CardWidth = 330;
    private const double Gap = 10;
    private const double Edge = 18;

    private static readonly List<Window> Live = new();

    /// <summary>Выключить, если владелец предпочитает системные тосты.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// Показать уведомление. Возвращает false, если хаб выключен — тогда решение
    /// (тост Windows или ничего) принимает вызывающий код.
    /// </summary>
    public static bool Show(string title, string message, Action? onClick = null)
    {
        if (!Enabled) return false;

        var island = IslandWindow.Current;
        if (island is not null)
        {
            try { if (island.ShowNotification(title, message)) return true; }
            catch { }
        }

        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return false;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(() => Show(title, message, onClick));
                return true;
            }

            while (Live.Count >= MaxVisible)
            {
                var oldest = Live[0];
                Live.RemoveAt(0);
                try { oldest.Close(); } catch { }
            }

            var window = Build(title, message, onClick);
            Live.Add(window);
            window.Show();
            Reposition();
            return true;
        }
        catch { return false; }
    }

    private static Window Build(string title, string message, Action? onClick)
    {
        var card = new Border
        {
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        card.SetResourceReference(Border.BackgroundProperty, "DsBackground");
        card.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Рисунок острова, чтобы карточка не выглядела плоской заливкой.
        var backdrop = new IslandBackdrop
        {
            Style = ShellServices.Instance?.Settings.Current.BackdropStyle ?? "dots",
            Strength = Math.Clamp((ShellServices.Instance?.Settings.Current.BackdropOpacity ?? 60) / 100.0, 0, 1),
            Corner = 14,
        };
        layout.Children.Add(backdrop);

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(16, 12, 16, 0),
        };
        var bar = new Border
        {
            Width = 2,
            Height = 12,
            CornerRadius = new CornerRadius(1),
            Margin = new Thickness(0, 2, 10, 0),
        };
        bar.SetResourceReference(Border.BackgroundProperty, "DsAccent");
        header.Children.Add(bar);

        var titleText = new TrackedText
        {
            Text = (title ?? string.Empty).ToUpperInvariant(),
            FontSize = 8,
            Tracking = 0.30,
        };
        titleText.SetResourceReference(TrackedText.BrushProperty, "DsAccent");
        header.Children.Add(titleText);

        var body = new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11.5,
            LineHeight = 16,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 6, 16, 13),
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, "DsText");

        Grid.SetRow(header, 0);
        Grid.SetRow(body, 1);
        layout.Children.Add(header);
        layout.Children.Add(body);

        card.Child = layout;

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = CardWidth,
            SizeToContent = SizeToContent.Height,
            Content = card,
        };

        card.MouseLeftButtonUp += (_, _) =>
        {
            Live.Remove(window);
            try { window.Close(); } catch { }
            onClick?.Invoke();
            Reposition();
        };

        // Появление: подъезжает справа и проявляется — без «выскакивания».
        window.Opacity = 0;
        window.Loaded += (_, _) =>
        {
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
            window.BeginAnimation(Window.OpacityProperty, fade);
        };

        var life = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        life.Tick += (_, _) =>
        {
            life.Stop();
            Live.Remove(window);
            try
            {
                var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
                fade.Completed += (_, _) => { try { window.Close(); } catch { } Reposition(); };
                window.BeginAnimation(Window.OpacityProperty, fade);
            }
            catch { }
        };
        life.Start();

        return window;
    }

    /// <summary>Расставить карточки стопкой снизу справа, над панелью задач.</summary>
    private static void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - Edge;

        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var window = Live[i];
            try
            {
                window.Left = area.Right - CardWidth - Edge;
                window.Top = Math.Max(area.Top + Edge, bottom - window.ActualHeight);
                bottom -= window.ActualHeight + Gap;
            }
            catch { }
        }
    }
}
