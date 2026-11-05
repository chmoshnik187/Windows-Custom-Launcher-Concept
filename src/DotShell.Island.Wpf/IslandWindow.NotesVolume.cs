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
    //  Таймер и напоминания
    // ================================================================

    // ================================================================
    //  Заметки: задачи и напоминания
    // ================================================================

    private void OnTaskInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        AddTaskFromInput();
    }

    private void OnAddTask(object sender, RoutedEventArgs e) => AddTaskFromInput();

    private void AddTaskFromInput()
    {
        var text = TaskInput.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        _vm.AddTask(text);
        TaskInput.Clear();
        TaskInput.Focus();
    }

    private void OnTaskToggle(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DotShell.Core.Tasks.TaskItem item) return;
        _vm.ToggleTask(item);
        e.Handled = true;
    }

    private void OnTaskRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not DotShell.Core.Tasks.TaskItem item) return;
        _vm.RemoveTask(item);
    }

    private void OnReminderInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        OnAddReminder(sender, e);
    }
    private void OnTimerStart(object sender, RoutedEventArgs e)
    {
        var text = TimerInput.Text.Replace(',', '.');
        if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes))
            _vm.StartTimer(minutes);
        else
            _vm.StatusLine = "таймер: впиши минуты числом";
    }

    private void OnTimerStop(object sender, RoutedEventArgs e) => _vm.StopTimer();

    private void OnAddReminder(object sender, RoutedEventArgs e)
    {
        if (_vm.AddReminder(ReminderInput.Text)) ReminderInput.Clear();
    }

    private void OnRemoveReminder(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Reminder r) _vm.RemoveReminder(r);
    }

    // ================================================================
    //  Командная строка
    // ================================================================

    private async void OnCmdRun(object sender, RoutedEventArgs e) => await RunCommand();

    private async void OnCmdKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await RunCommand();
    }

    private async Task RunCommand()
    {
        var cmd = CmdInput.Text;
        if (string.IsNullOrWhiteSpace(cmd)) return;
        CmdInput.Clear();
        await _vm.RunCmdAsync(cmd);
    }
    // ================================================================
    //  Главная: громкость
    // ================================================================

    private void SyncVolumeUI()
    {
        if (VolSlider is null) return;
        var vol = _services.Volume;
        if (!vol.Available)
        {
            VolSlider.IsEnabled = false;
            VolMute.IsEnabled = false;
            VolPercent.Text = "—";
            return;
        }

        _volSync = true;
        VolSlider.Value = Math.Round(vol.Level * 100);
        _volSync = false;
        VolPercent.Text = vol.Muted ? "ТИШИНА" : $"{vol.Level * 100:F0}%";
        VolMute.Content = vol.Muted ? "\uE74F" : "\uE767"; // Fluent: звук выкл / вкл
    }

    private void OnVolSlider(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // XAML выставляет Minimum/Maximum слайдера ещё до конца InitializeComponent,
        // и WPF честно дёргает ValueChanged прямо оттуда. В этот момент _services
        // ещё null (он приезжает из Program.cs уже после конструктора окна) —
        // раньше это давало NullReferenceException и окно не открывалось вовсе.
        if (_volSync || _services is null) return;
        _services.Volume.SetLevel((float)(VolSlider.Value / 100));
    }

    private void OnVolMute(object sender, RoutedEventArgs e) => _services.Volume.ToggleMute();

    /// <summary>Колесо мыши над свёрнутой пилюлей = громкость, как в плеерах.</summary>
    private void OnPillWheel(object sender, MouseWheelEventArgs e)
    {
        var vol = _services.Volume;
        if (!vol.Available) return;
        vol.SetLevel(vol.Level + (e.Delta > 0 ? 0.03f : -0.03f));
        VolChipText.Text = vol.Muted ? "ТИШИНА" : $"VOL {vol.Level * 100:F0}%";
        VolChip.Visibility = Visibility.Visible;
        _volChipTimer.Stop();
        _volChipTimer.Start();
        e.Handled = true;
    }

    // ================================================================
    //  Главная: инструменты
    // ================================================================
}
