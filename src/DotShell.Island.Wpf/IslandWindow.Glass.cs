namespace DotShell.Island.Wpf;

using DotShell.Island.Wpf.Desktop;

/// <summary>
/// «Жидкое стекло» раскрытой панели: обои десктопа за панелью, размытые
/// и притемнённые. Опция IslandGlass, по умолчанию выключена.
/// </summary>
public partial class IslandWindow
{
    private void UpdateGlassLayer()
    {
        if (GlassPanel is null || GlassWallpaper is null) return;

        // В режиме панели под панелью острова видны чужие окна — им нужен
        // плотный фон, иначе текст просвечивает.
        if (ExpandedBackdrop is not null)
        {
            bool dense = _isExpanded && IsTaskbarEdge(ShellServices.Instance.Settings.Current);
            ExpandedBackdrop.Visibility = dense
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        bool wanted = _isExpanded && ShellServices.Instance.Settings.Current.IslandGlass;
        if (!wanted)
        {
            GlassPanel.Visibility = System.Windows.Visibility.Collapsed;
            GlassWallpaper.Source = null;
            return;
        }

        var wallpaper = WallpaperSample.Load();
        if (wallpaper is null)
        {
            // Сплошной цвет/Spotlight или файл пропал — не выдумываем стекло.
            GlassPanel.Visibility = System.Windows.Visibility.Collapsed;
            GlassWallpaper.Source = null;
            return;
        }

        GlassWallpaper.Source = wallpaper;
        GlassPanel.Visibility = System.Windows.Visibility.Visible;
    }
}
