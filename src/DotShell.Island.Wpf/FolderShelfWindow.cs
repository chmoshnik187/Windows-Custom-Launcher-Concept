namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using DotShell.Core;
using DotShell.Core.Native;

/// <summary>
/// Полка-«большая папка» на рабочем столе: плитка с содержимым выбранной папки
/// (3×3, лишнее — «+N»). Механика из LiquidDesk, но в нашем языке.
///
/// Окно живёт отдельно от острова: его можно перетащить, оно помнит позицию,
/// клик по элементу открывает его проводником/приложением, правый клик по
/// шапке — «Открыть папку / Обновить / Убрать полку». Содержимое обновляется
/// по FileSystemWatcher.
/// </summary>
public sealed class FolderShelfWindow : Window
{
    private static FolderShelfWindow? _open;

    private readonly string _folder;
    private readonly WrapPanel _tiles = new();
    private readonly TextBlock _title = new();
    private readonly TextBlock _count = new();
    private readonly DispatcherTimer _saveTimer;
    private FileSystemWatcher? _watch;

    public static void Sync()
    {
        var settings = ShellServices.Instance.Settings.Current;
        bool want = settings.ShelfEnabled
                    && !string.IsNullOrWhiteSpace(settings.ShelfFolder)
                    && Directory.Exists(settings.ShelfFolder);

        Log.Info($"shelf: sync want={want} folder={settings.ShelfFolder} exists={Directory.Exists(settings.ShelfFolder)}");

        if (!want)
        {
            _open?.Close();
            return;
        }

        if (_open is not null && string.Equals(_open._folder, settings.ShelfFolder, StringComparison.OrdinalIgnoreCase))
        {
            _open.Refresh();
            return;
        }

        _open?.Close();
        _open = new FolderShelfWindow(settings.ShelfFolder);
        _open.Show();
        Log.Info("shelf: окно открыто");
    }

    private FolderShelfWindow(string folder)
    {
        _folder = folder;

        Title = "DotShell — полка";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 330;
        SizeToContent = SizeToContent.Height;

        var settings = ShellServices.Instance.Settings.Current;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        _title.Text = new DirectoryInfo(folder).Name.ToUpperInvariant();
        _title.FontFamily = new FontFamily("Bahnschrift");
        _title.FontSize = 11;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xF2));
        _count.FontFamily = new FontFamily("Bahnschrift");
        _count.FontSize = 9.5;
        _count.Margin = new Thickness(8, 1, 0, 0);
        _count.Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x7E, 0x8A));
        titleRow.Children.Add(_title);
        titleRow.Children.Add(_count);
        header.Children.Add(titleRow);

        var dot = new Border
        {
            Width = 5,
            Height = 5,
            CornerRadius = new CornerRadius(2.5),
            Background = new SolidColorBrush(ColorPalette.Accent),
            Margin = new Thickness(0, 4, 2, 0),
        };
        Grid.SetColumn(dot, 1);
        header.Children.Add(dot);

        _tiles.Margin = new Thickness(0);
        _tiles.ItemWidth = 96;
        _tiles.ItemHeight = 92;

        var content = new StackPanel { Margin = new Thickness(14, 12, 14, 14) };
        content.Children.Add(header);
        content.Children.Add(_tiles);

        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0xF4, 0x0B, 0x0B, 0x0E)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x2C, 0xFF, 0xFF, 0xFF)),
            Child = content,
        };
        root.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            try { DragMove(); } catch { }
        };

        Content = root;

        var menu = new ContextMenu();
        var openFolder = new MenuItem { Header = "Открыть папку" };
        openFolder.Click += (_, _) => Open(folder);
        menu.Items.Add(openFolder);
        var refresh = new MenuItem { Header = "Обновить" };
        refresh.Click += (_, _) => Refresh();
        menu.Items.Add(refresh);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Убрать полку" };
        remove.Click += (_, _) =>
        {
            ShellServices.Instance.Settings.Update(s => s.ShelfEnabled = false);
            Close();
        };
        menu.Items.Add(remove);
        root.ContextMenu = menu;

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();
            try
            {
                double left = Left, top = Top;
                ShellServices.Instance.Settings.Update(s =>
                {
                    s.ShelfLeft = left;
                    s.ShelfTop = top;
                });
            }
            catch { }
        };
        LocationChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };

        Loaded += (_, _) =>
        {
            ApplyTaskbarlessWindowStyle();
            RestorePosition(settings);
            Refresh();
            StartWatcher();
        };

        Closed += (_, _) =>
        {
            _saveTimer.Stop();
            _watch?.Dispose();
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    private void RestorePosition(DotShell.Core.Settings.AppSettings settings)
    {
        var area = SystemParameters.WorkArea;
        if (settings.ShelfLeft > -30000 && settings.ShelfTop > -30000)
        {
            Left = Math.Clamp(settings.ShelfLeft, area.Left, area.Right - Width);
            Top = Math.Clamp(settings.ShelfTop, area.Top, area.Bottom - 200);
        }
        else
        {
            Left = area.Right - Width - 32;
            Top = area.Top + 120;
        }
    }

    private void ApplyTaskbarlessWindowStyle()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = (long)Win32.GetWindowLongPtr(hwnd, -20);
            ex |= 0x00000080; // WS_EX_TOOLWINDOW — не мусорить в Alt+Tab
            Win32.SetWindowLongPtr(hwnd, -20, (IntPtr)ex);
        }
        catch { }
    }

    private void StartWatcher()
    {
        try
        {
            _watch = new FileSystemWatcher(_folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            _watch.Created += (_, _) => Dispatcher.BeginInvoke(() => Refresh());
            _watch.Deleted += (_, _) => Dispatcher.BeginInvoke(() => Refresh());
            _watch.Renamed += (_, _) => Dispatcher.BeginInvoke(() => Refresh());
        }
        catch { }
    }

    private void Refresh()
    {
        try
        {
            if (!Directory.Exists(_folder)) { Close(); return; }

            var entries = new DirectoryInfo(_folder)
                .GetFileSystemInfos()
                .Where(info => (info.Attributes & FileAttributes.Hidden) == 0)
                .OrderByDescending(info => info is DirectoryInfo)
                .ThenBy(info => info.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            _count.Text = entries.Count.ToString();
            _tiles.Children.Clear();

            const int max = 8;
            foreach (var entry in entries.Take(max))
                _tiles.Children.Add(Tile(entry));

            if (entries.Count > max)
            {
                var more = new Border
                {
                    Width = 88,
                    Height = 86,
                    Margin = new Thickness(4),
                    CornerRadius = new CornerRadius(10),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = "+" + (entries.Count - max),
                        FontFamily = new FontFamily("Bahnschrift"),
                        FontSize = 16,
                        Foreground = new SolidColorBrush(Color.FromRgb(0xAE, 0xAE, 0xBA)),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                more.MouseLeftButtonUp += (_, _) => Open(_folder);
                _tiles.Children.Add(more);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"shelf: refresh failed: {ex.Message}");
        }
    }

    private UIElement Tile(FileSystemInfo entry)
    {
        var icon = new Image
        {
            Width = 34,
            Height = 34,
            Stretch = Stretch.Uniform,
            Source = LoadIcon(entry.FullName),
        };

        var art = new Border
        {
            Width = 56,
            Height = 56,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x13, 0x17)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x16, 0xFF, 0xFF, 0xFF)),
            Child = icon,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var name = new TextBlock
        {
            Text = entry.Name,
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 9.5,
            Margin = new Thickness(2, 6, 2, 0),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 88,
            Foreground = new SolidColorBrush(Color.FromRgb(0xA6, 0xA6, 0xB0)),
        };

        var stack = new StackPanel();
        stack.Children.Add(art);
        stack.Children.Add(name);

        var tile = new Border
        {
            Width = 88,
            Height = 86,
            Margin = new Thickness(4),
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack,
            ToolTip = entry.FullName,
        };
        tile.MouseEnter += (_, _) => tile.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
        tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        tile.MouseLeftButtonUp += (_, e) =>
        {
            Open(entry.FullName);
            e.Handled = true;
        };
        return tile;
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Info($"shelf: не смог открыть {path}: {ex.Message}");
        }
    }

    private static ImageSource? LoadIcon(string path)
    {
        try
        {
            string png = DotShell.Core.Launcher.IconCache.Extract(path, path);
            if (!string.IsNullOrWhiteSpace(png) && File.Exists(png))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(png);
                image.EndInit();
                image.Freeze();
                return image;
            }
        }
        catch { }

        // Папки и часть файлов IconCache не отдаёт — берём системную иконку shell.
        try
        {
            var info = new SHFILEINFO();
            IntPtr result = SHGetFileInfo(path, 0, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            var source = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            DestroyIcon(info.hIcon);
            return source;
        }
        catch
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref SHFILEINFO info, uint cbFileInfo, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);
}
