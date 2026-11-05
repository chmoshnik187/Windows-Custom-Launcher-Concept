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
    //  Плеер
    // ================================================================

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.TogglePlayAsync());
    private void OnNextClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.NextAsync());
    private void OnPrevClick(object sender, RoutedEventArgs e) => _ = SafeMedia(_vm.Media.PreviousAsync());

    private static async Task SafeMedia(Task t)
    {
        // Медиа-сессия могла закрыться между кликом и вызовом (плеер остановили).
        // Падение из-за "музыка закончилась" — недопустимо для шелла.
        try { await t; }
        catch (Exception ex) { Log.Info($"media command failed: {ex.Message}"); }
    }

}
