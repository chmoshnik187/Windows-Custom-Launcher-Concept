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
    //  Полноэкранный режим
    // ================================================================

    private void OnFullscreenChanged(bool fullscreen)
    {
        Dispatcher.BeginInvoke(() =>
        {
            var hide = fullscreen && _vm.HideInFullscreen;
            _hiddenByFullscreen = hide;

            if (hide)
            {
                Log.Info("hidden: fullscreen app in foreground");
                _tickTimer.Stop();
                Units.SetPaused(true);
                MiniEq.IsRunning = false;
                ShowNoActivate(false);
            }
            else
            {
                ShowNoActivate(true);
                Units.SetPaused(false);
                _tickTimer.Start();
                ReassertTopmost();
                UpdateMiniEq();
                _vm.Tick();
            }
        });
    }

    /// <summary>
    /// Показать/спрятать окно НЕ активируя его. Обычный Show() украл бы фокус
    /// у игры или видео в момент выхода из фуллскрина — а это ровно то,
    /// за что пользователь возненавидит шелл.
    /// </summary>
    private void ShowNoActivate(bool show)
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
            Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE |
            (show ? Win32.SWP_SHOWWINDOW : 0x0080 /* SWP_HIDEWINDOW */));
    }
}
