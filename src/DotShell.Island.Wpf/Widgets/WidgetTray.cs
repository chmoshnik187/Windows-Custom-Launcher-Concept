namespace DotShell.Island.Wpf.Widgets;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DotShell.Core.Windowing;
using DotShell.Island.Wpf.Controls;

/// <summary>
/// Виджет «Трей»: значки приложений из области уведомлений, своими руками.
///
/// Состав берём из процессов, которые держат трей-значок (см. <see cref="TrayApps"/>),
/// служебные вроде прокси и webhelper прячем. Правый клик — настройка: какие значки
/// показывать и какие закрепить навсегда.
///
/// Плитки заполняют всю площадь виджета: потянул за угол — выросли и значки
/// (<see cref="TileFillPanel"/>).
/// </summary>
public sealed partial class WidgetWindow
{
    private UIElement BuildTray()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var head = new Grid { Margin = new Thickness(8, 6, 8, 2) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var caption = new TrackedText
        {
            Text = "ТРЕЙ",
            FontSize = 8,
            Tracking = 0.30,
            VerticalAlignment = VerticalAlignment.Center,
        };
        caption.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        head.Children.Add(caption);
        Grid.SetColumn(caption, 0);

        var counter = new TrackedText
        {
            Text = "—",
            FontSize = 8,
            Tracking = 0.20,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        counter.SetResourceReference(TrackedText.BrushProperty, "DsTextMuted");
        head.Children.Add(counter);
        Grid.SetColumn(counter, 1);

        root.Children.Add(head);
        Grid.SetRow(head, 0);

        var tiles = new TileFillPanel { Margin = new Thickness(6, 2, 6, 6) };
        root.Children.Add(tiles);
        Grid.SetRow(tiles, 1);

        ContextMenu? menu = null;

        void Refresh()
        {
            var settings = ShellServices.Instance.Settings;

            var options = new TrayApps.Options
            {
                HideHelpers = settings.Current.TrayHideHelpers,
                Hidden = new HashSet<string>(settings.Current.TrayHidden, StringComparer.OrdinalIgnoreCase),
                Pinned = new HashSet<string>(settings.Current.TrayPinned, StringComparer.OrdinalIgnoreCase),
            };

            var apps = TrayApps.Scan(options);
            counter.Text = apps.Count == 0 ? "ПУСТО" : $"{apps.Count}";

            tiles.Children.Clear();
            foreach (var app in apps.Take(40)) tiles.Children.Add(TrayTile(app, menu));

            if (apps.Count == 0)
            {
                var empty = new TextBlock
                {
                    Text = "ни одного значка",
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 9,
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "DsTextMuted");
                tiles.Children.Add(empty);
            }
        }

        menu = BuildTrayMenu(() => Refresh());
        Refresh();
        Timer(4000, Refresh);

        // Правый клик по любому месту виджета — меню. Открываем сами: обычный
        // ContextMenu в этом окне перехватывало меню окна («Убрать виджет»).
        root.MouseRightButtonUp += (_, e) => OpenMenu(menu, root, e);

        return root;
    }

    private static void OpenMenu(ContextMenu? menu, FrameworkElement target, MouseButtonEventArgs e)
    {
        if (menu is null) return;
        e.Handled = true;

        // Открываем отложенно: если открыть меню прямо в обработчике отпускания
        // правой кнопки, тот же клик его сразу и закрывает.
        target.Dispatcher.BeginInvoke(() =>
        {
            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>
    /// Меню виджета: спрятать служебные, скрыть или закрепить каждый значок.
    /// Закреплённый остаётся на месте, даже когда программа закрыта — так пропажа
    /// значка не выглядит поломкой.
    /// </summary>
    private ContextMenu BuildTrayMenu(Action refresh)
    {
        var menu = new ContextMenu();
        menu.Opened += (_, _) => Log.Info($"tray menu: открыто, пунктов {menu.Items.Count}");

        var settings = ShellServices.Instance.Settings;

        var helpers = new MenuItem
        {
            Header = "Скрывать служебные (прокси, webhelper)",
            IsCheckable = true,
            IsChecked = settings.Current.TrayHideHelpers,
        };
        helpers.Click += (_, _) =>
        {
            bool next = !settings.Current.TrayHideHelpers;
            settings.Update(s => s.TrayHideHelpers = next);
            refresh();
        };
        menu.Items.Add(helpers);
        menu.Items.Add(new Separator());

        foreach (var app in TrayApps.Scan(new TrayApps.Options
                 {
                     HideHelpers = false,
                     Hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                     Pinned = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                 }))
        {
            bool helper = TrayApps.LooksLikeHelper(app.ProcessName);
            bool hiddenByFilter = helper && settings.Current.TrayHideHelpers;
            bool visible = !settings.Current.TrayHidden.Contains(app.ProcessName, StringComparer.OrdinalIgnoreCase)
                           && !hiddenByFilter;

            var item = new MenuItem
            {
                Header = helper ? $"{app.ProcessName} · служебный" : app.ProcessName,
                IsCheckable = true,
                IsChecked = visible,
            };

            var name = app.ProcessName;
            item.Click += (_, _) =>
            {
                // Служебный при включённом фильтре: включаем его, значит фильтр надо снять,
                // иначе галочка «включил», а значок не появился — хуже, чем не показывать.
                if (helper && settings.Current.TrayHideHelpers)
                {
                    settings.Update(s => s.TrayHideHelpers = false);
                }
                else
                {
                    bool nowVisible = !settings.Current.TrayHidden.Contains(name, StringComparer.OrdinalIgnoreCase);
                    settings.Update(s =>
                    {
                        s.TrayHidden.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                        if (nowVisible) s.TrayHidden.Add(name);
                    });
                }

                refresh();
            };

            if (app.IconPath is { } path)
            {
                var pin = new MenuItem
                {
                    Header = "закрепить навсегда",
                    IsCheckable = true,
                    IsChecked = settings.Current.TrayPinned.Contains(path, StringComparer.OrdinalIgnoreCase),
                };
                pin.Click += (_, _) =>
                {
                    bool pinned = settings.Current.TrayPinned.Contains(path, StringComparer.OrdinalIgnoreCase);
                    settings.Update(s =>
                    {
                        s.TrayPinned.RemoveAll(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase));
                        if (!pinned) s.TrayPinned.Add(path);
                    });
                    refresh();
                };
                item.Items.Add(pin);
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(RemoveWidgetItem());

        return menu;
    }

    private UIElement TrayTile(TrayApps.TrayApp app, ContextMenu? menu)
    {
        // Размер не задаём: его назначит панель, растягивая клетку на всю площадь.
        var frame = new Border
        {
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            ToolTip = app.OwnerClass == "pinned"
                ? $"{app.ProcessName}\nзакрыта · клик — запустить"
                : $"{app.ProcessName}\nклик — показать окно",
            Opacity = app.OwnerClass == "pinned" ? 0.42 : 1.0,
        };
        frame.SetResourceReference(Border.BorderBrushProperty, "DsBorder");

        if (app.IconPath is not null && IconFor(app.IconPath) is { } source)
        {
            frame.Child = new Image { Source = source, Stretch = Stretch.Uniform };
        }
        else
        {
            frame.Child = new TextBlock
            {
                Text = app.ProcessName.Length > 0 ? app.ProcessName[..1].ToUpperInvariant() : "?",
                FontFamily = new FontFamily("Bahnschrift"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(ColorPalette.Accent),
            };
        }

        frame.MouseLeftButtonUp += (_, e) =>
        {
            TrayApps.Activate(app);
            e.Handled = true;
        };

        // Правый клик по плитке: она точно под мышью, поэтому меню открываем здесь,
        // а не на корневой сетке (до неё клик иногда не доходил).
        frame.MouseRightButtonUp += (_, e) => OpenMenu(menu, frame, e);

        return frame;
    }
}
