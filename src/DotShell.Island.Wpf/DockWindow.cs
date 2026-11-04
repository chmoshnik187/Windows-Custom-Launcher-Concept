namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Launcher;

/// <summary>
/// Док слева — то, чего не хватало из Linux-видео владельца: вертикальная полоса
/// с закреплёнными программами у левой кромки.
///
/// Зачем отдельное окно, а не панель внутри острова: док висит на кромке весь день,
/// а остров живёт сверху и сжимается. Общая у них только геометрия точки и акцент.
/// </summary>
public sealed class DockWindow : Window
{
    private static DockWindow? _open;

    private readonly StackPanel _items = new();
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<string, Border> _marks = new();

    public static void Toggle()
    {
        if (_open is not null)
        {
            ShellServices.Instance.Settings.Update(s => s.DockEnabled = false);
            _open.Close();
            return;
        }

        ShellServices.Instance.Settings.Update(s => s.DockEnabled = true);
        _open = new DockWindow();
        _open.Show();
    }

    private DockWindow()
    {
        Title = "DotShell — док";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 64;

        var root = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x0B, 0x0E)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 10, 6, 10),
            Child = _items,
        };

        root.SetResourceReference(Border.BorderBrushProperty, "DsAccentSoft");
        Content = root;

        BuildItems();

        var area = SystemParameters.WorkArea;
        Left = area.Left + 10;

        Loaded += (_, _) =>
        {
            Top = area.Top + (area.Height - ActualHeight) / 2;
            FadeIn();
        };

        // Полоска «запущено» обновляется редко: раз в 3 секунды достаточно.
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tick.Tick += (_, _) => RefreshRunning();
        _tick.Start();

        Closed += (_, _) =>
        {
            _tick.Stop();
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    private void BuildItems()
    {
        _items.Children.Clear();

        var apps = ShellServices.Instance.Pinned.Groups
            .SelectMany(group => group.Items)
            .Where(app => app.Exists)
            .Take(12)
            .ToList();

        if (apps.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "нет\nзакреплённых",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 8,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66)),
                Margin = new Thickness(0, 8, 0, 8),
            });
            return;
        }

        foreach (var app in apps)
        {
            _items.Children.Add(Item(app));
        }

        RefreshRunning();
    }

    private UIElement Item(PinnedApp app)
    {
        var icon = new Image
        {
            Width = 26,
            Height = 26,
            Source = LoadIcon(app),
            Stretch = Stretch.Uniform,
        };

        var art = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)),
            Child = icon,
        };

        // Точка «запущено» под иконкой: 5×5 как в плитках острова.
        var mark = new Border
        {
            Width = app.Target.Length > 0 ? 5 : 0,
            Height = 5,
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        _marks[app.Target] = mark;

        var column = new StackPanel
        {
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = app.Name,
        };

        column.Children.Add(art);
        column.Children.Add(mark);

        var clickable = new Border
        {
            Child = column,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2, 1, 2, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };

        clickable.MouseEnter += (_, _) => clickable.Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1F));
        clickable.MouseLeave += (_, _) => clickable.Background = Brushes.Transparent;
        clickable.MouseLeftButtonUp += (_, _) => Launch(app);

        return clickable;
    }

    private static void Launch(PinnedApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo(app.Target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Info($"dock: не смог запустить {app.Name}: {ex.Message}");
        }
    }

    private void RefreshRunning()
    {
        var accent = ColorPalette.Accent;

        foreach (var (target, mark) in _marks)
        {
            bool running = ProcessProbe.IsRunning(new PinnedApp { Target = target, Name = target });

            mark.Background = new SolidColorBrush(running ? accent : Color.FromRgb(0x3A, 0x3A, 0x42));
        }
    }

    private static BitmapSource? LoadIcon(PinnedApp app)
    {
        try
        {
            // Иконку берём через наш кэш: он умеет доставать её из самой программы,
            // а не из ярлыка (иначе все значки — одинаковые «файлики»).
            string png = IconCache.Extract(app.Shortcut, app.Target);

            if (string.IsNullOrWhiteSpace(png) && System.IO.File.Exists(app.IconPath))
                png = app.IconPath;

            if (!string.IsNullOrWhiteSpace(png) && System.IO.File.Exists(png))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(png);
                image.EndInit();
                image.Freeze();
                return image;
            }

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(app.Target);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private void FadeIn()
    {
        Opacity = 0;
        BeginAnimation(OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(200)));
    }
}
