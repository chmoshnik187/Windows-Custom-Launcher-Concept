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
    //  Вкладки
    // ================================================================

    /// <summary>Показать ровно одну вкладку, остальные спрятать.</summary>
    private SettingsWindow? _settingsWindow;
    private string _lastTab = "home";

    /// <summary>
    /// Открыть окно настроек. Вкладка СИСТЕМА превратилась в кнопку-открывашку:
    /// настроек стало больше, чем помещается в полосу острова.
    /// </summary>
    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_vm, _services, this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>Вернуть вкладку, которая была выбрана до нажатия на «СИСТЕМА».</summary>
    private void RestoreLastTab()
    {
        foreach (var child in TabsRow.Children)
        {
            if (child is not System.Windows.Controls.RadioButton button) continue;
            if (button.Tag as string != _lastTab) continue;

            button.IsChecked = true;
            return;
        }
    }
    /// <summary>Вкладка, которая сейчас показана (для кросс-фейда).</summary>
    private FrameworkElement? _visibleTab;

    private void ShowTab(string tag)
    {
        var next = tag switch
        {
            "home" => (FrameworkElement)TabHome,
            "notes" => TabNotes,
            "clipboard" => TabClipboard,
            "system" => TabSystem,
            "theme" => TabTheme,
            "chat" => TabChat,
            "shelf" => TabShelf,
            "cmd" => TabCmd,
            _ => TabHome,
        };

        // Переключение вкладок — кросс-фейд, но с явным состоянием: раньше
        // завершение анимации старой вкладки могло не сработать, и она оставалась
        // висеть поверх новой (таймер заметок «жил» на всех страницах).
        var previous = _visibleTab;

        var all = new FrameworkElement[]
        {
            TabHome, TabNotes, TabClipboard, TabSystem, TabTheme, TabChat, TabShelf, TabCmd,
        };

        foreach (var tab in all)
        {
            if (ReferenceEquals(tab, next)) continue;

            if (ReferenceEquals(tab, previous) && tab.Visibility == Visibility.Visible)
            {
                tab.IsHitTestVisible = false;
                var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(90));
                anim.Completed += (_, _) =>
                {
                    // Если за это время успели переключиться на другую — не трогаем.
                    if (!ReferenceEquals(_visibleTab, tab)) return;

                    tab.Visibility = Visibility.Collapsed;
                    tab.BeginAnimation(OpacityProperty, null);
                    tab.Opacity = 1;
                    tab.IsHitTestVisible = true;
                };
                tab.BeginAnimation(OpacityProperty, anim);
            }
            else
            {
                // Всё, что не участвует в переходе, гасим сразу и окончательно.
                tab.BeginAnimation(OpacityProperty, null);
                tab.Opacity = 1;
                tab.Visibility = Visibility.Collapsed;
                tab.IsHitTestVisible = true;
            }
        }

        string transition = ShellServices.Instance.Settings.Current.TabTransition;

        next.BeginAnimation(OpacityProperty, null);
        next.RenderTransform = null;
        next.Visibility = Visibility.Visible;

        if (transition == "none")
        {
            next.Opacity = 1;
        }
        else if (transition == "slide")
        {
            var shift = new TranslateTransform(38, 0);
            next.RenderTransform = shift;
            next.Opacity = 1;

            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else if (transition == "curtain")
        {
            var scale = new ScaleTransform(1, 0.82);
            next.RenderTransform = scale;
            next.RenderTransformOrigin = new Point(0.5, 0);
            next.Opacity = 1;

            scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(200))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });
        }
        else
        {
            next.Opacity = 0;
            next.BeginAnimation(OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        }

        _visibleTab = next;

        // Guard: 300 ms after a switch everything except the active tab must be
        // hidden. Otherwise a fast switch leaves the previous tab as a translucent
        // ghost above the new one.
        var guard = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        guard.Tick += (_, _) =>
        {
            guard.Stop();

            foreach (var tab in all)
            {
                if (ReferenceEquals(tab, _visibleTab)) continue;
                if (tab.Visibility != Visibility.Visible) continue;

                tab.BeginAnimation(OpacityProperty, null);
                tab.Opacity = 1;
                tab.Visibility = Visibility.Collapsed;
                tab.IsHitTestVisible = true;
            }
        };
        guard.Start();

        if (tag == "chat") _ = CheckOllamaAsync();
        if (tag == "notes") Dispatcher.BeginInvoke(() => UpdateNotesHeight(false));
    }

    /// <summary>
    /// Памятка про Ollama, если её нет. Чат с локальной моделью — единственная часть
    /// острова, которой нужна сторонняя программа; без предупреждения это выглядит
    /// как «ИИ сломался».
    /// </summary>
    private async Task CheckOllamaAsync()
    {
        try
        {
            bool alive = await DotShell.Core.Ai.OllamaClient.IsRunningAsync();
            OllamaNote.Visibility = alive ? Visibility.Collapsed : Visibility.Visible;
        }
        catch { OllamaNote.Visibility = Visibility.Visible; }
    }

    private void OnOpenOllamaSite(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://ollama.com/download")
            {
                UseShellExecute = true,
            });
            ShowAiHint("ставим Ollama: после установки вернись сюда и нажми СКАЧАТЬ");
        }
        catch (Exception ex) { ShowAiHint($"не смог открыть сайт: {ex.Message}"); }
    }

    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;

        // «СИСТЕМА» больше не вкладка, а вход в окно настроек: их стало больше,
        // чем помещается в полосу острова.
        if (tag == "system")
        {
            OpenSettings();
            Dispatcher.BeginInvoke(RestoreLastTab);
            return;
        }

        // Важно: Checked срабатывает ещё во время InitializeComponent (в XAML у
        // первой вкладки стоит IsChecked="True"), когда остальные поля окна ещё
        // не заполнены. Без этой проверки — NullReferenceException на старте.
        if (_vm is null || TabNotes is null) return;

        _vm.ActiveTab = tag;

        // Показ вкладок — одно место на всю программу. Раньше эта логика жила
        // только в OnTabChecked, а он для первой вкладки не срабатывает (она
        // уже IsChecked в XAML) — и заметки висели поверх главной с самого
        // старта. Отсюда и жалоба «места мало»: главную просто закрывало.
        ShowTab(tag);

        if (tag == "cmd") CmdInput.Focus();

        // Каждая вкладка получает свой размер, а не живёт в общем прямоугольнике.
        ApplyTabSize(tag);

        if (tag == "home")
        {
            SyncVolumeUI();
            RefreshToolLabels();
            RefreshWidgetsLabel();
            RebuildPinnedTiles();

        }
        else
        {
            PickerPanel.Visibility = Visibility.Collapsed;
        }

        if (tag == "chat") ScheduleGrowChat();

        if (tag == "chat")
        {
            // Блок настройки чата виден всегда: ключ, провайдер и модель можно
            // поменять в любой момент. Модель синхронизируем вручную (editable-комбобокс)
            // и молча подтягиваем список, если он ещё пуст.
            if (string.IsNullOrWhiteSpace(AiModelBox.Text)) AiModelBox.Text = _vm.AiModel;
            if (AiModelBox.ItemsSource is null) _ = RefreshModelsAsync();
            ChatInput.Focus();
        }

        if (tag == "system")
            StatsLine.Text =
                $"CPU {_services.Stats.Cpu:F0}%   RAM {_services.Stats.UsedMemoryMb}/{_services.Stats.TotalMemoryMb} МБ\n" +
                $"ВЕРСИЯ: {DotShell.Core.Updates.UpdateChecker.CurrentTextWithHint}\n" +
                $"FOCUS: {(_services.WindowWatcher.ForegroundProcessName is { Length: > 0 } p ? p : "—")}\n" +
                $"settings: {AppSettings.SettingsPath}";
    }
}
