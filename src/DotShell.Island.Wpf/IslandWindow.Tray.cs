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
    //  Контекстное меню
    // ================================================================

    private void OnMenuToggle(object sender, RoutedEventArgs e) => ToggleExpanded();

    private void OnMenuClipboard(object sender, RoutedEventArgs e) => ExpandAndGoToTab("clipboard");

    private void OnMenuLauncher(object sender, RoutedEventArgs e) => LauncherHost.Toggle();

    private void OnMenuAutostart(object sender, RoutedEventArgs e) =>
        _vm.RunAtStartup = MenuAutostart.IsChecked == true;

    /// <summary>
    /// Перезапуск проводника: часть оформления Windows применяется только при
    /// старте explorer.exe. Панель задач исчезает на секунду-две — предупреждаем.
    /// </summary>
    private void OnMenuRestartExplorer(object sender, RoutedEventArgs e)
    {
        _tray?.Notify("Проводник", "Перезапускаю: панель задач исчезнет на пару секунд.");

        // Дадим уведомлению показаться, прежде чем закрывать оболочку.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            bool ok = DotShell.Core.Native.ExplorerRestart.Restart();
            Log.Info($"explorer restart: {ok}");
            _vm.StatusLine = ok ? "проводник перезапущен" : "не удалось перезапустить проводник";
        };
        timer.Start();
    }
    private void OnMenuSettings(object sender, RoutedEventArgs e) => OpenSettings();

    private void OnMenuOpenFolder(object sender, RoutedEventArgs e) => OpenSettingsFolder();

    private void OpenSettingsFolder()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", AppSettings.SettingsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Info($"open folder failed: {ex.Message}"); }
    }

    // ================================================================
    //  Обновления с GitHub
    // ================================================================

    private DotShell.Core.Updates.UpdateInfo? _available;

    /// <summary>Проверить, не вышла ли новая версия. Тихо, если всё в порядке.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        var info = await DotShell.Core.Updates.UpdateChecker.CheckAsync();

        if (info is null)
        {
            Log.Info("update check: github не ответил");
            if (manual)
            {
                _tray?.Notify("Обновления", "Не удалось проверить: GitHub не ответил. Попробуй позже.");
                _vm.StatusLine = "обновления: не удалось проверить (GitHub не ответил)";
            }
            return;
        }

        var currentVersion = DotShell.Core.Updates.UpdateChecker.Current;
        if (info.Version <= currentVersion)
        {
            _available = null;
            Log.Info($"update check: свежая {DotShell.Core.Updates.UpdateChecker.ActualText}, на GitHub {info.Version.ToString(3)}");
            if (manual)
            {
                _tray?.Notify("Обновления", $"У тебя свежая версия {DotShell.Core.Updates.UpdateChecker.ActualText}.");
                _vm.StatusLine = $"обновления: свежая версия {DotShell.Core.Updates.UpdateChecker.ActualText}";
            }
            return;
        }

        _available = info;
        Log.Info($"update check: доступна {info.Version.ToString(3)} (у нас {DotShell.Core.Updates.UpdateChecker.CurrentText})");


        // Окно вместо шарика: обновление — редкое событие, двухсекундное сообщение
        // трея слишком легко пропустить.
        var skipped = _services.Settings.Current.SkippedUpdateVersion;
        bool muted = string.Equals(skipped, info.Version.ToString(3), StringComparison.OrdinalIgnoreCase);

        if (!muted && !_updateShown)
        {
            _updateShown = true;
            var toShow = info;
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    var window = new UpdateWindow(toShow, skipThis =>
                    {
                        if (!skipThis) return;
                        _services.Settings.Update(s => s.SkippedUpdateVersion = toShow.Version.ToString(3));
                        _vm.StatusLine = $"обновление {toShow.Version.ToString(3)} больше не напоминаю";
                    });
                    window.ShowDialog();
                }
                catch (Exception ex) { Log.Info($"update window failed: {ex.Message}"); }
            });
        }
        // Результат — не только в трее: иначе проверить обновление,
        // не заглядывая в меню трея, было невозможно.
        _vm.StatusLine = $"обновления: есть {info.Version.ToString(3)} — «Скачать обновление» в трее";
        _tray?.Notify(
            $"Есть версия {info.Version.ToString(3)}",
            "Остров — в трее, пункт «Скачать обновление»: откроется страница релиза.");
    }

    /// <summary>Открыть страницу загрузки последнего релиза (или сразу файл).</summary>
    private void OpenUpdatePage()
    {
        var url = _available?.PageUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Info($"open update page failed: {ex.Message}"); }
    }

    /// <summary>Первая проверка — не в момент старта, а когда остров уже обжился.</summary>
    private async Task DelayedUpdateCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            await CheckUpdatesAsync(manual: false);
        }
        catch (Exception ex) { Log.Info($"update check failed: {ex.Message}"); }
    }

    private void OnMenuExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
