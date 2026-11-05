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
    //  Буфер обмена
    // ================================================================

    private void OnClipboardSearch(object sender, TextChangedEventArgs e) =>
        _vm.SearchClipboard(ClipSearch.Text);

    private void OnClipboardItemUse(object sender, MouseButtonEventArgs e)
    {
        if (ClipList.SelectedItem is not ClipboardItem item) return;
        UseAndPaste(item);
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is ClipboardItem item) _vm.TogglePin(item);
    }

    private void OnClearClipboard(object sender, RoutedEventArgs e) => _vm.ClearClipboard();

    /// <summary>
    /// Главный фокус файла: вставить запись обратно в приложение, из которого
    /// пользователь пришёл. Остров — окно активируемое (иначе заметки и поиск
    /// по буферу не смогли бы принимать ввод с клавиатуры), поэтому:
    /// 1) кладём запись в буфер, 2) сворачиваем остров,
    /// 3) возвращаем фокус предыдущему окну (запомнено в WM_MOUSEACTIVATE),
    /// 4) через паузу шлём Ctrl+V — он прилетит уже в нужное приложение.
    /// </summary>
    private void UseAndPaste(ClipboardItem item)
    {
        _vm.UseClipboardItem(item);
        Collapse();

        var target = _lastForeground;
        if (target == IntPtr.Zero || target == _hwnd) return; // вставлять некуда

        var pasteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        pasteTimer.Tick += (_, _) =>
        {
            pasteTimer.Stop();
            try
            {
                Win32.SetForegroundWindow(target);
                SendKeys.Paste();
            }
            catch (Exception ex) { Log.Info($"paste-back failed: {ex.Message}"); }
        };
        pasteTimer.Start();
    }
}
