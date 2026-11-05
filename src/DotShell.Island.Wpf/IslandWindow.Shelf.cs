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
    //  Полка файлов: приём дропа и drag-out
    // ================================================================

    private void OnIslandDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Файлы, брошенные на остров в любом состоянии, уезжают на полку.</summary>
    private void OnIslandDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        _services.Shelf.Add(files);
        ExpandAndGoToTab("shelf");
        _vm.StatusLine = $"на полке: +{files.Length}";
        e.Handled = true;
    }

    private Point _shelfDragStart;
    private IslandViewModel.ShelfRow? _shelfDragRow;

    private void OnShelfDragCandidate(object sender, MouseButtonEventArgs e)
    {
        _shelfDragStart = e.GetPosition(null);
        _shelfDragRow = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext
            as IslandViewModel.ShelfRow;
    }

    private void OnShelfDragMove(object sender, MouseEventArgs e)
    {
        if (_shelfDragRow is null || e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _shelfDragStart.X) < 8 && Math.Abs(pos.Y - _shelfDragStart.Y) < 8) return;

        var row = _shelfDragRow;
        _shelfDragRow = null;

        // Настоящий OLE drag-and-drop: файл прилетает в целевое окно
        // как обычное перетаскивание из проводника.
        var data = new DataObject();
        data.SetFileDropList(new System.Collections.Specialized.StringCollection { row.Path });
        var effect = DragDrop.DoDragDrop(ShelfList, data, DragDropEffects.Copy);

        // Утащили успешно — файл сделал своё дело на полке и покидает её.
        if (effect != DragDropEffects.None) _vm.ShelfRemove(row);
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null and not T)
            node = VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node);
        return node as T;
    }

    private void OnShelfOpen(object sender, MouseButtonEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is IslandViewModel.ShelfRow row)
        {
            try
            {
                Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
                _services.Shelf.Remove(row.File);
            }
            catch (Exception ex) { Log.Info($"shelf open failed: {ex.Message}"); }
        }
    }

    private void OnShelfRemove(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is IslandViewModel.ShelfRow row) _vm.ShelfRemove(row);
    }

    private void OnShelfClear(object sender, RoutedEventArgs e) => _vm.ShelfClear();
}
