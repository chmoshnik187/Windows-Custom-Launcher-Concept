namespace DotShell.Island.Wpf;

using System.Diagnostics;
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
using DotShell.Core.Launcher;
using DotShell.Core.Settings;
using DotShell.Core.Native;
using DotShell.Core.Windowing;
using Microsoft.Win32;

/// <summary>
/// Док: вертикальная полоса закреплённых программ у края экрана.
///
/// Раньше это была «мёртвая» панель: без управления, без скрытия и с жёстким лимитом.
/// Теперь док умеет прятаться за край (как остров сверху), выезжать по наведению,
/// прикрепляться к левому или правому краю и пополняться перетаскиванием ярлыка
/// прямо на панель.
/// </summary>
public sealed class DockWindow : Window
{
    private static DockWindow? _open;

    private readonly StackPanel _items = new();
    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _hideTimer;
    private readonly Dictionary<string, Border> _marks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Border _root;
    private readonly Border _handle;
    private readonly Border _handleBar;
    private readonly Grid _layout;

    private ScrollViewer? _scroll;

    private bool _hiddenByEdge;
    private bool _peeked;
    private bool _onRight;
    private double _panelWidth = 64;
    private bool _dragging;
    private Point _dragStartScreen;
    private double _dragStartTop;
    private double _dragStartLeft;
    private double _dragScaleX = 1;
    private double _dragScaleY = 1;
    private bool _dragMoved;

    public static void Toggle()
    {
        var settings = ShellServices.Instance.Settings;
        bool enable = _open is null;
        settings.Update(s => s.DockEnabled = enable);
        Sync();
    }

    /// <summary>Привести открытое окно дока в соответствие с настройкой DockEnabled.</summary>
    public static void Sync()
    {
        bool want = ShellServices.Instance.Settings.Current.DockEnabled;
        if (want)
        {
            if (_open is null)
            {
                _open = new DockWindow();
                _open.Show();
            }
        }
        else
        {
            _open?.Close();
        }
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
        AllowDrop = true;

        _items.Margin = new Thickness(0);

        _root = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x0B, 0x0B, 0x0E)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 10, 8, 10),
            Child = _items,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        _root.SetResourceReference(Border.BorderBrushProperty, "DsAccentSoft");

        // Тонкая ручка: видна, когда док спрятан за краем. Хит-тест по альфе требует
        // не полностью прозрачный цвет.
        _handleBar = new Border
        {
            Width = 3,
            Height = 48,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
        };

        _handle = new Border
        {
            Width = Math.Max(6, ShellServices.Instance.Settings.Current.DockHandleWidth),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)),
            CornerRadius = new CornerRadius(0, 14, 14, 0),
            Cursor = Cursors.SizeWE,
            Visibility = Visibility.Collapsed,
            ToolTip = "Потяните за панель, чтобы переставить док",
            Child = _handleBar,
        };

        _layout = new Grid();
        _layout.Children.Add(_root);
        _layout.Children.Add(_handle);
        Content = _layout;

        ContextMenu = BuildContextMenu();

        // Перетаскивание по свободной области/ручке, скрытие по уходу курсора.
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMoveWindow;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseLeave += OnMouseLeaveWindow;
        _handle.MouseEnter += (_, _) => { if (_hiddenByEdge && !_peeked) PeekIn(); };
        _handle.MouseLeftButtonDown += (_, e) =>
        {
            // Если панель ещё спрятана — ручка сначала выдвигает док.
            // Когда он уже выехал, за ручку можно тянуть как за обычную панель.
            if (_hiddenByEdge && !_peeked)
            {
                PeekIn();
                e.Handled = true;
            }
        };

        DragOver += OnDragOver;
        DragLeave += (_, _) => ResetRootBorder();
        Drop += OnDrop;

        BuildItems();

        var area = SystemParameters.WorkArea;
        int margin = Math.Clamp(ShellServices.Instance.Settings.Current.DockMargin, 0, 40);
        _onRight = IsRightSide();
        Left = _onRight ? area.Right - Width - margin : area.Left + margin;
        Top = area.Top + 80;

        Loaded += (_, _) =>
        {
            _panelWidth = Math.Max(ActualWidth, Width);
            ApplySideVisuals();
            RestoreVerticalPosition();
            ApplyDockState(instant: true);
            FadeIn();
        };

        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _tick.Tick += (_, _) =>
        {
            try { RefreshRunning(); }
            catch (Exception ex) { Log.Info($"dock: tick failed — {ex.Message}"); }
        };
        _tick.Start();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_hiddenByEdge || !_peeked) return;

            // Меню живёт отдельным окном: курсор уходит с дока, но прятать
            // панель из-под открытых пунктов нельзя.
            if (ContextMenu?.IsOpen == true) return;

            if (IsMouseOver) return;

            // После выезда панель встаёт с отступом от края, и курсор, который
            // был на ручке, оказывается в «щели». Это не повод закрываться:
            // пока курсор в сторожевой зоне у края, док остаётся открытым.
            if (IsCursorNearEdge())
            {
                _hideTimer.Start();
                return;
            }

            PeekOut();
        };

        ShellServices.Instance.Pinned.Changed += OnPinnedChanged;
        ShellServices.Instance.Theme.SettingsChanged += OnSettingsChanged;

        Closed += (_, _) =>
        {
            _tick.Stop();
            _hideTimer.Stop();
            ShellServices.Instance.Pinned.Changed -= OnPinnedChanged;
            ShellServices.Instance.Theme.SettingsChanged -= OnSettingsChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            if (ReferenceEquals(_open, this)) _open = null;

            // Закрытие окна любым способом (например, Alt+F4) не должно оставлять
            // настройку «док включён» врать при следующем старте.
            if (ShellServices.Instance.Settings.Current.DockEnabled)
                ShellServices.Instance.Settings.Update(s => s.DockEnabled = false);
        };
    }

    private static bool IsRightSide() =>
        string.Equals(ShellServices.Instance.Settings.Current.DockSide, "right", StringComparison.OrdinalIgnoreCase);

    private void OnPinnedChanged() => Dispatcher.BeginInvoke(() =>
    {
        BuildItems();
        ApplyDockState(instant: true);
    });

    private void OnSettingsChanged(AppSettings _) => Dispatcher.BeginInvoke(() =>
    {
        _handle.Width = Math.Max(6, ShellServices.Instance.Settings.Current.DockHandleWidth);
        ApplyDockState(instant: false);
    });

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var add = new MenuItem { Header = "Добавить программу…" };
        add.Click += (_, _) => AddProgram();
        menu.Items.Add(add);

        var refresh = new MenuItem { Header = "Обновить список" };
        refresh.Click += (_, _) => { BuildItems(); ApplyDockState(instant: true); };
        menu.Items.Add(refresh);

        menu.Items.Add(new Separator());

        var left = new MenuItem { Header = "Прикрепить слева", IsCheckable = true };
        left.Click += (_, _) => SetSide("left");
        menu.Items.Add(left);

        var right = new MenuItem { Header = "Прикрепить справа", IsCheckable = true };
        right.Click += (_, _) => SetSide("right");
        menu.Items.Add(right);

        menu.Items.Add(new Separator());

        var openIsland = new MenuItem { Header = "Открыть остров" };
        openIsland.Click += (_, _) => { try { IslandWindow.Current?.OpenFromExternal("home"); } catch { } };
        menu.Items.Add(openIsland);

        var autoHide = new MenuItem { Header = "Прятать за край", IsCheckable = true };
        autoHide.Click += (_, _) =>
        {
            ShellServices.Instance.Settings.Update(s => s.DockAutoHide = autoHide.IsChecked);
            ApplyDockState(instant: false);
        };
        menu.Items.Add(autoHide);

        menu.Items.Add(new Separator());

        var hide = new MenuItem { Header = "Скрыть док" };
        hide.Click += (_, _) => HideDock();
        menu.Items.Add(hide);

        // Пока меню открыто, док не должен убегать за край: курсор уходит на меню,
        // Window.MouseLeave срабатывает, и панель закрывалась прямо под пунктами.
        menu.Opened += (_, _) =>
        {
            _hideTimer.Stop();
            left.IsChecked = !IsRightSide();
            right.IsChecked = IsRightSide();
            autoHide.IsChecked = ShellServices.Instance.Settings.Current.DockAutoHide;
            if (_hiddenByEdge && !_peeked) PeekIn();
        };
        menu.Closed += (_, _) =>
        {
            if (_hiddenByEdge && !IsMouseOver) _hideTimer.Start();
        };

        return menu;
    }

    private void HideDock()
    {
        ShellServices.Instance.Settings.Update(s => s.DockEnabled = false);
        Close();
    }

    private void SetSide(string side)
    {
        ShellServices.Instance.Settings.Update(s => s.DockSide = side);
        ApplySideVisuals();
        ApplyDockState(instant: false);
    }

    private void BuildItems()
    {
        _items.Children.Clear();
        _marks.Clear();
        _scroll = null;

        _items.Children.Add(AddTile());

        var apps = ShellServices.Instance.Pinned.Groups
            .SelectMany(group => group.Items)
            .ToList();

        if (apps.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "перетащи\nярлык\nсюда",
                FontFamily = new FontFamily("Bahnschrift"),
                FontSize = 8,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x66)),
                Margin = new Thickness(0, 12, 0, 8),
                ToolTip = "Добавить можно кнопкой «+», меню или перетаскиванием .exe/.lnk",
            });
            return;
        }

        // Список может быть длинным: ограничиваем высоту видимой областью экрана
        // и даём прокрутку колесом. Скроллбар в 64-пиксельной панели шириной
        // почти с иконку, поэтому он скрыт: места он забирал больше, чем нёс пользы.
        var stack = new StackPanel();
        foreach (var app in apps)
            stack.Children.Add(Item(app));

        var scroll = new ScrollViewer
        {
            Content = stack,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0),
        };
        _scroll = scroll;
        _items.Children.Add(scroll);

        UpdateScrollLimit();
        RefreshRunning();
    }

    private UIElement AddTile()
    {
        var accent = ColorPalette.Accent;
        var plus = new TextBlock
        {
            Text = "+",
            FontFamily = new FontFamily("Bahnschrift"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0),
        };

        var tile = new Border
        {
            Width = 40,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Добавить программу в док\nМожно также перетащить ярлык из проводника",
            Child = plus,
            Margin = new Thickness(0, 0, 0, 10),
        };

        tile.MouseEnter += (_, _) =>
        {
            tile.Background = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1D));
            tile.BorderBrush = new SolidColorBrush(Color.FromArgb(0xA0, accent.R, accent.G, accent.B));
            plus.Foreground = new SolidColorBrush(accent);
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14));
            tile.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2E));
            plus.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x94));
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragMoved) return;
            AddProgram();
            e.Handled = true;
        };

        return tile;
    }

    private void AddProgram()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Добавить программу в док",
            Filter = "Программы|*.lnk;*.exe;*.bat;*.cmd;*.url;*.msc|Все файлы|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        int added = 0;
        foreach (var path in dialog.FileNames)
            if (ShellServices.Instance.Pinned.TryAddFile(path)) added++;

        if (added == 0) return;

        BuildItems();
        ApplyDockState(instant: true);
    }

    private UIElement Item(PinnedApp app)
    {
        bool exists = app.Exists;

        var icon = new Image
        {
            Width = 26,
            Height = 26,
            Source = LoadIcon(app),
            Stretch = Stretch.Uniform,
            Opacity = exists ? 1 : 0.28,
        };

        var art = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x12, 0x12, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Child = icon,
        };

        var mark = new Border
        {
            Width = 4,
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            Margin = new Thickness(0, 5, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = exists ? Visibility.Visible : Visibility.Collapsed,
        };

        _marks[app.Target] = mark;

        string tip = exists
            ? string.IsNullOrWhiteSpace(app.Hotkey) ? app.Name : $"{app.Name} · {app.Hotkey}"
            : $"{app.Name} — программа не найдена";

        var column = new StackPanel
        {
            Margin = new Thickness(0, 3, 0, 3),
            ToolTip = tip,
        };
        column.Children.Add(art);
        column.Children.Add(mark);

        var clickable = new Border
        {
            Child = column,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2, 2, 2, 2),
            Background = Brushes.Transparent,
            Cursor = exists ? Cursors.Hand : Cursors.Arrow,
        };

        clickable.MouseEnter += (_, _) => clickable.Background = new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
        clickable.MouseLeave += (_, _) => clickable.Background = Brushes.Transparent;

        if (exists)
        {
            clickable.MouseLeftButtonUp += (_, e) =>
            {
                if (_dragMoved) return;

                // Кнопка дока ведёт себя как кнопка панели задач: уже открытое
                // приложение — показать/свернуть, закрытое — запустить.
                if (!WindowActions.Toggle(app.Target))
                    Launch(app);
                e.Handled = true;
            };

            // Средняя кнопка — новое окно (как в Familiar Desktop).
            clickable.MouseDown += (_, e) =>
            {
                if (e.ChangedButton != MouseButton.Middle) return;
                Launch(app);
                e.Handled = true;
            };

            // Колесо по иконке — перебор открытых окон этой программы.
            clickable.MouseWheel += (_, e) =>
            {
                if (WindowActions.CycleWindows(app.Target, e.Delta > 0 ? 1 : -1))
                    e.Handled = true;
            };
        }

        var itemMenu = new ContextMenu();

        var launch = new MenuItem { Header = "Запустить", IsEnabled = exists };
        launch.Click += (_, _) => Launch(app);
        itemMenu.Items.Add(launch);

        var windowsItem = new MenuItem { Header = "Окна" };
        itemMenu.Items.Add(windowsItem);

        var show = new MenuItem { Header = "Показать окно" };
        show.Click += (_, _) =>
        {
            var hwnd = WindowActions.FindWindow(app.Target);
            if (hwnd != IntPtr.Zero) WindowActions.Activate(hwnd);
        };
        itemMenu.Items.Add(show);

        var minimize = new MenuItem { Header = "Свернуть окно" };
        minimize.Click += (_, _) =>
        {
            var hwnd = WindowActions.FindWindow(app.Target);
            if (hwnd != IntPtr.Zero) WindowActions.Minimize(hwnd);
        };
        itemMenu.Items.Add(minimize);

        var closeWindow = new MenuItem { Header = "Закрыть окно" };
        closeWindow.Click += (_, _) =>
        {
            var hwnd = WindowActions.FindWindow(app.Target);
            if (hwnd != IntPtr.Zero) WindowActions.Close(hwnd);
        };
        itemMenu.Items.Add(closeWindow);

        var newWindow = new MenuItem { Header = "Новое окно" };
        newWindow.Click += (_, _) => Launch(app);
        itemMenu.Items.Add(newWindow);

        itemMenu.Items.Add(new Separator());

        var locate = new MenuItem { Header = "Показать в проводнике" };
        locate.Click += (_, _) => Locate(app);
        itemMenu.Items.Add(locate);

        itemMenu.Items.Add(new Separator());

        var remove = new MenuItem { Header = "Убрать из дока" };
        remove.Click += (_, _) => ShellServices.Instance.Pinned.Remove(app);
        itemMenu.Items.Add(remove);

        itemMenu.Opened += (_, _) =>
        {
            var windows = WindowActions.GetWindows(app.Target);
            var hwnd = windows.Count > 0 ? windows[0].Hwnd : WindowActions.FindWindow(app.Target);
            bool hasWindow = hwnd != IntPtr.Zero;
            show.IsEnabled = hasWindow;
            minimize.IsEnabled = hasWindow && !WindowActions.IsMinimized(hwnd);
            closeWindow.IsEnabled = hasWindow;

            // Живой список окон программы: клик — показать конкретное окно.
            windowsItem.Items.Clear();
            windowsItem.IsEnabled = windows.Count > 0;
            windowsItem.Header = windows.Count > 0 ? $"Окна ({windows.Count})" : "Окна";
            foreach (var window in windows)
            {
                string title = string.IsNullOrWhiteSpace(window.Title) ? window.ProcessName : window.Title;
                var item = new MenuItem
                {
                    Header = title.Length > 48 ? title[..45] + "…" : title,
                    IsCheckable = true,
                    IsChecked = window.Hwnd == Win32.GetForegroundWindow(),
                };
                item.Click += (_, _) => WindowActions.Activate(window.Hwnd);
                windowsItem.Items.Add(item);
            }

            if (windows.Count > 1)
            {
                windowsItem.Items.Add(new Separator());
                var cycle = new MenuItem { Header = "Следующее окно" };
                cycle.Click += (_, _) => WindowActions.CycleWindows(app.Target, 1);
                windowsItem.Items.Add(cycle);
            }
        };

        clickable.ContextMenu = itemMenu;
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

    private static void Locate(PinnedApp app)
    {
        try
        {
            string path = !string.IsNullOrWhiteSpace(app.Shortcut) && System.IO.File.Exists(app.Shortcut)
                ? app.Shortcut
                : app.Target;

            if (System.IO.File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else
                Process.Start(new ProcessStartInfo(System.IO.Path.GetDirectoryName(path) ?? path)
                {
                    UseShellExecute = true,
                });
        }
        catch (Exception ex)
        {
            Log.Info($"dock: не смог открыть расположение {app.Name}: {ex.Message}");
        }
    }

    private void RefreshRunning()
    {
        var accent = ColorPalette.Accent;
        foreach (var (target, mark) in _marks.ToArray())
        {
            bool running = ProcessProbe.IsRunning(new PinnedApp { Target = target, Name = target });
            mark.Background = new SolidColorBrush(running ? accent : Color.FromRgb(0x3A, 0x3A, 0x42));
            mark.Opacity = running ? 1 : 0.7;
        }
    }

    private static BitmapSource? LoadIcon(PinnedApp app)
    {
        try
        {
            string png = DotShell.Core.Launcher.IconCache.Extract(app.Shortcut, app.Target);

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

    // --- Edge-dock / auto-hide / peek ---

    /// <summary>
    /// Рабочая область монитора, на котором стоит док, в DIP.
    /// Единый helper (Win32 physical → DIP), чтобы поведение совпадало с островом/виджетами.
    /// </summary>
    private Rect WorkArea() => MonitorHelper.GetWorkAreaDip(this);

    private void ApplyDockState(bool instant)
    {
        var settings = ShellServices.Instance.Settings.Current;
        _hiddenByEdge = settings.DockAutoHide;
        _handle.Visibility = _hiddenByEdge ? Visibility.Visible : Visibility.Collapsed;

        // Ширину окна нужно знать до анимации: она зависит от содержимого.
        UpdateLayout();
        _panelWidth = Math.Max(ActualWidth, Width);
        ApplySideVisuals();
        UpdateScrollLimit();

        var area = WorkArea();
        int ms = instant ? 0 : Motion.Ms(Motion.Normal);

        if (!_hiddenByEdge)
        {
            _peeked = false;
            AnimateLeft(VisibleLeft(area), ms);
        }
        else if (_peeked || IsMouseOver)
        {
            AnimateLeft(VisibleLeft(area), ms);
        }
        else
        {
            AnimateLeft(HiddenLeft(area), ms);
        }

        ClampTop(area);
    }

    private void ApplySideVisuals()
    {
        _onRight = IsRightSide();

        // Ручка живёт на внутреннем крае: слева док — ручка справа, справа — слева.
        _handle.HorizontalAlignment = _onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        _handle.CornerRadius = _onRight ? new CornerRadius(14, 0, 0, 14) : new CornerRadius(0, 14, 14, 0);
        _handleBar.Margin = _onRight ? new Thickness(1, 0, 0, 0) : new Thickness(0, 0, 1, 0);

        // Симметричный внутренний отступ, чтобы иконки читались одинаково у обоих краёв.
        _root.Padding = new Thickness(8, 10, 8, 10);

        // Ручка — акцентная метка, но мягкая: в точечном языке это «штамп», а не фонарь.
        var accent = ColorPalette.Accent;
        _handleBar.Background = new SolidColorBrush(Color.FromArgb(0xC8, accent.R, accent.G, accent.B));
    }

    private double VisibleLeft(Rect area)
    {
        int margin = Math.Clamp(ShellServices.Instance.Settings.Current.DockMargin, 0, 40);
        return _onRight ? area.Right - _panelWidth - margin : area.Left + margin;
    }

    private double HiddenLeft(Rect area)
    {
        double handle = Math.Max(6, ShellServices.Instance.Settings.Current.DockHandleWidth);
        // Оставляем ручку видимой у выбранного края рабочей области.
        return _onRight ? area.Right - handle : area.Left - (_panelWidth - handle);
    }

    private void UpdateScrollLimit()
    {
        if (_scroll is null) return;
        _scroll.MaxHeight = Math.Max(200, WorkArea().Height - 96);
    }

    private void PeekIn()
    {
        if (!_hiddenByEdge) return;
        _peeked = true;
        _hideTimer.Stop();
        AnimateLeft(VisibleLeft(WorkArea()), Motion.Ms(Motion.Normal));
    }

    private void PeekOut()
    {
        if (!_hiddenByEdge) return;
        _peeked = false;
        AnimateLeft(HiddenLeft(WorkArea()), Motion.Ms(Motion.Normal));
    }

    private void OnMouseLeaveWindow(object sender, MouseEventArgs e)
    {
        if (!_hiddenByEdge || !_peeked) return;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    /// <summary>
    /// Курсор в сторожевой зоне у края, к которому прикреплён док.
    /// Нужна из-за отступа: ручка спрятана у самого края, а выехавшая панель
    /// начинается на `DockMargin` дальше — иначе док закрывался бы сразу
    /// после выезда, пока курсор ещё стоит на месте.
    /// </summary>
    private bool IsCursorNearEdge()
    {
        try
        {
            if (!DotShell.Core.Native.Win32.GetCursorPos(out var pt)) return false;

            // Физические координаты курсора → логические координаты окна.
            var local = PointFromScreen(new Point(pt.X, pt.Y));
            double x = Left + local.X;
            double y = Top + local.Y;

            var area = WorkArea();
            double edge = _onRight ? area.Right : area.Left;

            int handle = Math.Max(6, ShellServices.Instance.Settings.Current.DockHandleWidth);
            int margin = Math.Clamp(ShellServices.Instance.Settings.Current.DockMargin, 0, 40);
            double trigger = Math.Max(24, handle + margin + 10);

            return Math.Abs(x - edge) <= trigger
                   && y >= Top - 24
                   && y <= Top + Math.Max(ActualHeight, 80) + 24;
        }
        catch
        {
            return false;
        }
    }

    private void AnimateLeft(double to, int ms)
    {
        if (ms <= 0)
        {
            BeginAnimation(LeftProperty, null);
            Left = to;
            return;
        }

        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = Motion.Ease(),
        };
        anim.Completed += (_, _) =>
        {
            BeginAnimation(LeftProperty, null);
            Left = to;
        };
        BeginAnimation(LeftProperty, anim);
    }

    private void ClampTop(Rect area)
    {
        if (ActualHeight <= 0) return;

        double min = area.Top + 4;
        double max = Math.Max(min, area.Bottom - ActualHeight - 4);
        Top = Math.Clamp(Top, min, max);
    }

    private void RestoreVerticalPosition()
    {
        var area = WorkArea();
        var s = ShellServices.Instance.Settings.Current;
        bool restored = false;

        if (s.PerMonitorLayouts && !string.IsNullOrWhiteSpace(s.DockLayoutMonitor) && s.DockLayoutY != int.MinValue)
        {
            var physical = DotShell.Core.Windowing.Monitors.WorkAreaForDevice(s.DockLayoutMonitor);
            if (physical is { } wa)
            {
                Top = wa.Top + s.DockLayoutY;
                restored = true;
            }
        }

        if (!restored)
        {
            int saved = s.DockTop;
            if (saved == -32000)
                Top = area.Top + Math.Max(8, (area.Height - ActualHeight) / 2);
            else
                Top = saved;
        }

        ClampTop(WorkArea());
    }

    private void SaveVerticalPosition()
    {
        int top = (int)Math.Round(Top);
        ShellServices.Instance.Settings.Update(s =>
        {
            s.DockTop = top;

            if (!s.PerMonitorLayouts)
            {
                s.DockLayoutMonitor = string.Empty;
                s.DockLayoutY = int.MinValue;
                return;
            }

            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                string monitor = DotShell.Core.Windowing.Monitors.DeviceNameForWindow(hwnd) ?? string.Empty;
                var physical = hwnd != IntPtr.Zero ? DotShell.Core.Windowing.Monitors.ForWindow(hwnd) : null;
                if (!string.IsNullOrEmpty(monitor) && physical is { } wa)
                {
                    s.DockLayoutMonitor = monitor;
                    s.DockLayoutY = top - wa.Top;
                    return;
                }
            }
            catch { }

            s.DockLayoutMonitor = string.Empty;
            s.DockLayoutY = int.MinValue;
        });
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            UpdateLayout();
            _panelWidth = Math.Max(ActualWidth, Width);
            ApplySideVisuals();
            RestoreVerticalPosition();
            ApplyDockState(instant: true);
        });
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DISPLAYCHANGE || msg == WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLayout();
                _panelWidth = Math.Max(ActualWidth, Width);
                ApplySideVisuals();
                RestoreVerticalPosition();
                ApplyDockState(instant: true);
            });
        }
        return IntPtr.Zero;
    }

    // --- Drag: свободное перемещение, на отпускании — прилипание к краю ---

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // Клик по элементу запуска — не перетаскивание.
        if (e.OriginalSource is DependencyObject d && FindAncestor<ButtonBase>(d) is not null) return;

        _dragging = true;
        _dragMoved = false;
        _dragStartScreen = CursorScreenPoint();
        _dragStartTop = Top;
        _dragStartLeft = Left;

        // PointToScreen отдаёт физические пиксели, а Top/Left — логические.
        // Без деления на масштаб на 125% док уезжал бы быстрее курсора.
        var dpi = VisualTreeHelper.GetDpi(this);
        _dragScaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
        _dragScaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

        CaptureMouse();
    }

    private void OnMouseMoveWindow(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        var p = CursorScreenPoint();
        var dx = (p.X - _dragStartScreen.X) / _dragScaleX;
        var dy = (p.Y - _dragStartScreen.Y) / _dragScaleY;

        if (!_dragMoved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
            _dragMoved = true;

        if (!_dragMoved) return;

        // Тянем свободно, чтобы док можно было перенести на другой монитор.
        Top = _dragStartTop + dy;
        Left = _dragStartLeft + dx;
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();

        if (!_dragMoved) return;

        // Куда бросили — к тому краю и прилипаем. Так док переезжает на второй
        // монитор и на правую сторону без отдельного режима «свободного окна».
        var area = WorkArea();
        bool right = Left + _panelWidth / 2 > area.Left + area.Width / 2;
        ShellServices.Instance.Settings.Update(s => s.DockSide = right ? "right" : "left");

        ApplySideVisuals();
        ClampTop(area);
        SaveVerticalPosition();
        ApplyDockState(instant: false);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    /// <summary>
    /// Курсор в физических координатах экрана. Нельзя брать `PointToScreen`:
    /// окно в этот момент двигается вместе с курсором, и позиция мыши
    /// относительно окна почти не меняется — drag получает обратную связь
    /// и проезжает в разы меньше, чем курсор.
    /// </summary>
    private static Point CursorScreenPoint()
    {
        return DotShell.Core.Native.Win32.GetCursorPos(out var pt)
            ? new Point(pt.X, pt.Y)
            : new Point(0, 0);
    }

    // --- Drag & drop ярлыков из проводника ---

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;

        if (files) _root.BorderBrush = new SolidColorBrush(ColorPalette.Accent);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        ResetRootBorder();

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        int added = 0;
        foreach (var path in files)
            if (ShellServices.Instance.Pinned.TryAddFile(path)) added++;

        if (added == 0) return;

        BuildItems();
        ApplyDockState(instant: true);
    }

    private void ResetRootBorder() => _root.SetResourceReference(Border.BorderBrushProperty, "DsAccentSoft");

    private void FadeIn()
    {
        Opacity = 0;
        var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(Motion.Ms(Motion.Normal)))
        {
            EasingFunction = Motion.Ease(),
        };
        BeginAnimation(OpacityProperty, fade);
    }
}
