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
    private void RefreshToolLabels()
    {
        if (ToolTheme is null) return;
        ToolTheme.Content = DotShell.Core.Native.WindowsTheme.IsDarkApps() ? "СВЕТЛАЯ ТЕМА" : "ТЁМАЯ ТЕМА";
        ToolDesk.Content = _vm.HideDesktopIcons ? "ПОКАЗАТЬ СТОЛ" : "СПРЯТАТЬ СТОЛ";
        ToolFocus.Content = _vm.FocusMode ? "ФОКУС ВКЛ" : "ФОКУС";
    }

    private void RebuildSmartRow()
    {
        if (SmartRow is null) return;
        SmartRow.Children.Clear();
        if (_vm.SmartActions.Count == 0)
        {
            SmartRow.Visibility = Visibility.Collapsed;
            return;
        }

        SmartRow.Visibility = Visibility.Visible;
        var hint = new TextBlock
        {
            Text = "АЛХИМИЯ:",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 9,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "DsAccent");
        SmartRow.Children.Add(hint);

        foreach (var row in _vm.SmartActions)
        {
            var btn = new Button { Style = (Style)FindResource("DsButton"), Content = row.Label.ToUpperInvariant() };
            btn.Click += (_, _) => _vm.ExecuteSmartAction(row);
            SmartRow.Children.Add(btn);
        }
    }

    /// <summary>
    /// Перечитать сочетания закреплённых программ и зарегистрировать их заново.
    /// Отдельный менеджер: при любом изменении списка проще снести и создать заново,
    /// чем хранить соответствие «имя → id» и аккуратно снимать по одному.
    /// </summary>
    private List<string> RefreshPinHotkeys()
    {
        var busy = new List<string>();
        _pinHotkeys?.Dispose();
        _pinHotkeys = new HotkeyManager(_services.MessageWindow);

        foreach (var app in _services.Pinned.Items)
        {
            var parsed = KeyCombo.Parse(app.Hotkey);
            if (parsed is null) continue;

            var target = app;
            var ok = _pinHotkeys.Register($"pin.{target.Name}", parsed.Modifiers, parsed.VirtualKey, () =>
            {
                var row = _vm.PinnedItems.FirstOrDefault(r =>
                    string.Equals(r.Target, target.Target, StringComparison.OrdinalIgnoreCase));
                if (row is not null) _vm.LaunchPinned(row);
            });

            if (!ok)
            {
                Log.Info($"pin hotkey busy: {app.Hotkey} для «{app.Name}»");
                busy.Add($"{app.Hotkey} ({app.Name})");
            }
        }

        return busy;
    }

    /// <summary>Окно «Клавиши и сочетания»: справочник и живая проба.</summary>
    private void OpenKeysWindow()
    {
        if (_keysWindow is { IsVisible: true })
        {
            _keysWindow.Activate();
            return;
        }

        _keysWindow = new KeyProbeWindow();
        _keysWindow.Closed += (_, _) => _keysWindow = null;
        _keysWindow.Show();
        _keysWindow.Activate();
    }

    /// <summary>Диалог назначения клавиши на плитку.</summary>
    private void AssignHotkey(IslandViewModel.PinnedRow row)
    {
        var dialog = new HotkeyCaptureWindow(row.Name, ShellServices.Instance.Pinned
            .Items.FirstOrDefault(p => p.Target == row.Target)?.Hotkey, combo =>
        {
            if (combo is null) return;                       // отмена
            ShellServices.Instance.Pinned.SetHotkey(row.Target, combo);
            var busy = RefreshPinHotkeys();
            RebuildPinnedTiles();


            _vm.StatusLine = string.IsNullOrWhiteSpace(combo)
                ? $"«{row.Name}»: сочетание снято"
                : busy.Count > 0
                    ? $"{combo} занято другой программой — выбери другое"
                    : $"{combo} → {row.Name}";
        });

        dialog.ShowDialog();
    }
    /// <summary>Тумблер «цвет заголовка из обложки»: пишем настройку и обновляем плеер.</summary>
    private void OnCoverAccentToggled(object sender, RoutedEventArgs e)
    {
        bool on = CoverAccentCheck.IsChecked == true;
        _services.Settings.Update(s => s.PlayerAccentFromCover = on);

        // Перестроить плеер, чтобы он пересчитал цвет (обложка уже загружена).
        Player.RefreshThemeColors();

        _vm.StatusLine = on ? "цвет заголовка: из обложки трека" : "цвет заголовка: из обоев";
    }
    private void RefreshWidgetsLabel()
    {
        if (ToolWidgets is null) return;
        int n = WidgetsManager.Types.Count(WidgetsManager.Has);
        ToolWidgets.Content = n > 0 ? $"ВИДЖЕТЫ · {n}" : "ВИДЖЕТЫ";
    }

    /// <summary>Сохранить город погоды из настроек и перезагрузить виджеты.</summary>
    private void OnSaveWeatherCity(object sender, RoutedEventArgs e)
    {
        var city = (WeatherCityBox.Text ?? string.Empty).Trim();
        if (city.Length == 0) { _vm.StatusLine = "город не может быть пустым"; return; }

        _services.Settings.Update(s => s.WeatherCity = city);
        DotShell.Core.Weather.WeatherService.ClearCache();
        _vm.StatusLine = $"погода: {city}";

        // Скажем открытым виджетам перерисоваться, не дожидаясь таймера 15 минут.
        DotShell.Island.Wpf.Widgets.WidgetsManager.RefreshAll();
    }
    /// <summary>Тумблер «свой курсор».</summary>
    private void OnCustomCursorToggled(object sender, RoutedEventArgs e)
    {
        if (CursorCheck.IsChecked == true)
        {
            CursorStyler.Enable();
            _vm.StatusLine = CursorStyler.Enabled
                ? "курсор: точка (стрелка, ссылка, текст, прицел, ожидание)"
                : "курсор: не удалось применить";
        }
        else
        {
            CursorStyler.Disable();
            _vm.StatusLine = "курсор: системный";
        }

        CursorCheck.IsChecked = CursorStyler.Enabled;
    }
    /// <summary>Тумблер «акцент Windows = цвет обоев».</summary>
    private void OnWindowsAccentToggled(object sender, RoutedEventArgs e)
    {
        if (WinAccentCheck.IsChecked == true)
        {
            WindowsAccentSync.Enable();
            var windowsHex = DotShell.Core.Native.WindowsAccent.CurrentHex() ?? "?";
            _vm.StatusLine = $"акцент Windows: {windowsHex} (цвет обоев), часть элементов — после перезапуска проводника";
        }
        else
        {
            WindowsAccentSync.Disable();
            _vm.StatusLine = $"акцент Windows: вернул прежний ({DotShell.Core.Native.WindowsAccent.CurrentHex() ?? "?"})";
        }

        WinAccentCheck.IsChecked = WindowsAccentSync.Enabled;
    }
    /// <summary>Тумблер «оформление всей Windows»: тёмные заголовки всех окон.</summary>
    /// <summary>
    /// Пересчитать точки на плитках групп. Опрос, а не события: программы
    /// запускаются и закрываются в чужих процессах, событий оттуда не приходит.
    /// </summary>
    private void RefreshGroupDots()
    {
        foreach (var (dot, group) in _groupDots)
        {
            if (dot.Parent is null) continue;
            bool running = group.Items.Any(DotShell.Core.Launcher.ProcessProbe.IsRunning);
            var want = running ? Visibility.Visible : Visibility.Collapsed;
            if (dot.Visibility != want) dot.Visibility = want;
        }
    }
    private void OnFullStyleToggled(object sender, RoutedEventArgs e)
    {
        bool on = FullStyleCheck.IsChecked == true;
        _services.Settings.Update(s => s.SystemStyleFull = on);

        if (on)
        {
            DotShell.Core.Native.WindowsTheme.SetDark(true);
            DotShell.Core.Native.DarkTitleBars.Start();
            _vm.StatusLine = $"оформление Windows: тёмные заголовки ({DotShell.Core.Native.DarkTitleBars.LastApplied} окон)";
        }
        else
        {
            DotShell.Core.Native.DarkTitleBars.Stop();
            DotShell.Core.Native.DarkTitleBars.RestoreAll();
            _vm.StatusLine = "оформление Windows: выключено, заголовки вернулись как были";
        }

    }
    private void OnToolTheme(object sender, RoutedEventArgs e)
    {
        _vm.SystemDark = !_vm.SystemDark;
        RefreshToolLabels();
    }

    private void OnToolDesk(object sender, RoutedEventArgs e)
    {
        _vm.HideDesktopIcons = !_vm.HideDesktopIcons;
        RefreshToolLabels();
    }

    private void OnToolLock(object sender, RoutedEventArgs e) => Win32.LockWorkStation();

    private void OnToolSleep(object sender, RoutedEventArgs e) => Win32.SetSuspendState(false, true, false);

    private void OnToolFocus(object sender, RoutedEventArgs e)
    {
        _vm.FocusMode = !_vm.FocusMode;
        RefreshToolLabels();
    }

    private void OnToolWidgets(object sender, RoutedEventArgs e) => OpenWidgetGallery();

    /// <summary>Витрина виджетов: карточки, перетаскивание на стол, клик — вкл/выкл.</summary>
    private void OpenWidgetGallery()
    {
        if (_widgetGallery is { IsVisible: true })
        {
            _widgetGallery.Activate();
            return;
        }

        _widgetGallery = new WidgetGalleryWindow();
        _widgetGallery.Closed += (_, _) => _widgetGallery = null;
        _widgetGallery.ShowGallery();
    }
}
