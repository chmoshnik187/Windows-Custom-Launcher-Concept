namespace DotShell.Island.Wpf;

using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotShell.Core.Launcher;

/// <summary>
/// Точечный лаунчер: окно-поиск приложений в стиле острова.
///
/// Поведение как у всех оверлеев шелла: topmost, без рамки, прячется при потере
/// фокуса. Запрос дублируется точечной матрицей сверху — тот же кирпич DotGrid,
/// что и часы в острове, поэтому лаунчер читается как часть семьи.
///
/// Индекс приложений строит ядро (AppIndex): ярлыки меню "Пуск", кеш в
/// apps.json, частые программы всплывают выше по использованию.
/// </summary>
public partial class LauncherWindow : Window
{
    private readonly AppIndex _index;
    private readonly DotShell.Core.Launcher.FileIndex _files = new();

    public LauncherWindow()
    {
        InitializeComponent();

        // Меню «Закрепить» объявлено в стиле пункта, поэтому обработчик подключаем
        // здесь: XAML не связывает Click внутри Style.Setter.
        foreach (var setter in Results.ItemContainerStyle.Setters.OfType<Setter>())
        {
            if (setter.Property != System.Windows.FrameworkElement.ContextMenuProperty) continue;
            if (setter.Value is not System.Windows.Controls.ContextMenu menu) continue;
            menu.AddHandler(System.Windows.Controls.MenuItem.ClickEvent, new RoutedEventHandler(OnPinToIsland));
        }

        _index = ShellServices.Instance.Launcher;
        _index.Changed += () => Dispatcher.BeginInvoke(Refresh);

        ApplyDotPalette();
        ColorPalette.PaletteChanged += () => Dispatcher.BeginInvoke(ApplyDotPalette);
        DotStyle.Changed += () => Dispatcher.BeginInvoke(ApplyDotPalette);

        Loaded += (_, _) => SearchBox.Focus();
    }

    /// <summary>Показать и сразу отдать клавиатуру: лаунчер без фокуса бесполезен.</summary>
    public void ShowAndFocus()
    {
        Show();
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        _index.EnsureReady();
        Refresh();
    }

    private void ApplyDotPalette()
    {
        QueryDots.DotColor = ColorPalette.Accent;
        QueryDots.GhostColor = ColorPalette.GhostDotColor;
        QueryDots.CellSize = DotStyle.Cell;
        QueryDots.Gap = DotStyle.Gap;
        QueryDots.RequestRender();
    }

    private void OnSearchChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var q = SearchBox.Text;
        QueryDots.Text = string.IsNullOrWhiteSpace(q) ? "DOTSHELL" : q.ToUpperInvariant();
        Refresh();
    }

    private void Refresh()
    {
        if (_index.IsIndexing)
        {
            StatusLine.Text = "индексирую ярлыки меню «Пуск»…";
            Results.ItemsSource = null;
            return;
        }

        _files.EnsureFresh();

        var rows = _index.Search(SearchBox.Text, 6)
            .Select(e => new LauncherRow(e))
            .ToList();

        // Файлы идут после программ: сначала то, что запускают, потом то, что открывают.
        foreach (var file in _files.Search(SearchBox.Text, 6))
            rows.Add(new LauncherRow(file));

        Results.ItemsSource = rows;
        if (rows.Count > 0) Results.SelectedIndex = 0;

        int apps = rows.Count(r => !r.IsFile);
        int files = rows.Count - apps;

        Log.Info($"launcher: запрос «{SearchBox.Text}» → {apps} программ, {files} файлов");

        StatusLine.Text = rows.Count == 0
            ? "ничего не найдено · Enter — запустить · ↑↓ — выбор · Alt+Enter — папка · Esc — закрыть"
            : $"{apps} программ · {files} файлов · Enter — открыть · Alt+Enter — показать в папке · Esc — закрыть";
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (Results.SelectedIndex < (Results.Items.Count - 1)) Results.SelectedIndex++;
                e.Handled = true;
                break;
            case Key.Up:
                if (Results.SelectedIndex > 0) Results.SelectedIndex--;
                e.Handled = true;
                break;
            case Key.Enter:
                if (Results.SelectedItem is LauncherRow row)
                    Launch(row, openFolder: Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                e.Handled = true;
                break;
            case Key.Escape:
                Hide();
                e.Handled = true;
                break;
        }
    }

    /// <summary>Правая кнопка по результату — закрепить программу плиткой на острове.</summary>
    private void OnPinToIsland(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        LauncherRow? row = mi.DataContext as LauncherRow;
        if (row is null && mi.Parent is System.Windows.Controls.ContextMenu menu &&
            menu.PlacementTarget is System.Windows.Controls.ListBoxItem lbi)
            row = lbi.DataContext as LauncherRow;
        if (row is null) return;
        ShellServices.Instance.Pinned.Add(row.Name, row.Target, row.Entry.Shortcut);
        StatusLine.Text = $"закреплено на острове: {row.Name}";
    }

    private void OnResultDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is LauncherRow row) Launch(row, openFolder: false);
    }

    private void Launch(LauncherRow row, bool openFolder)
    {
        try
        {
            if (openFolder)
            {
                Process.Start(new ProcessStartInfo("explorer.exe",
                    $"/select,\"{row.Target}\"") { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(row.Target)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(row.Target) ?? string.Empty,
                });
                if (row.Entry is not null) _index.Bump(row.Entry);
            }
            Hide();
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"не удалось запустить: {ex.Message}";
            Log.Info($"launch failed for {row.Target}: {ex.Message}");
        }
    }

    /// <summary>Фокус ушёл — лаунчер прячется, как и остров: не висим поверх чужой работы.</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        Hide();
    }
}

/// <summary>Строка результата: данные + иконка, извлечённая из exe (кешируется).</summary>
public sealed class LauncherRow
{
    private static readonly Dictionary<string, BitmapSource?> IconCache = new();

    public AppEntry? Entry { get; }
    public DotShell.Core.Launcher.FileEntry? File { get; }

    /// <summary>Файл или папка, а не программа.</summary>
    public bool IsFile => File is not null;

    public string Name => File?.Name ?? Entry!.Name;
    public string Target => File?.Path ?? Entry!.Target;

    /// <summary>Подпись под результатом: у файла — папка и тип, у программы — путь к exe.</summary>
    public string Detail => File is null ? Entry!.Target : File.Folder + " · " + File.Kind;

    public BitmapSource? Icon { get; }

    public LauncherRow(AppEntry entry)
    {
        Entry = entry;
        Icon = GetIcon(entry.Target);
    }

    public LauncherRow(DotShell.Core.Launcher.FileEntry file)
    {
        File = file;
        Icon = GetIcon(file.Path);
    }

    private static BitmapSource? GetIcon(string target)
    {
        if (IconCache.TryGetValue(target, out var cached)) return cached;

        BitmapSource? bmp = null;
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(target);
            if (icon is not null)
            {
                using var bitmap = icon.ToBitmap();
                bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap.GetHbitmap(), IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
            }
        }
        catch { bmp = null; }

        if (IconCache.Count > 512) IconCache.Clear();
        IconCache[target] = bmp;
        return bmp;
    }
}

/// <summary>Одиночное окно лаунчера + горячая клавиша из любого места системы.</summary>
public static class LauncherHost
{
    /// <summary>--launcher: открыть поиск сразу после старта.</summary>
    public static bool OpenOnce { get; set; }
    private static LauncherWindow? _window;

    public static void Toggle()
    {
        if (_window is null) _window = new LauncherWindow();

        if (_window.IsVisible) _window.Hide();
        else _window.ShowAndFocus();
    }
}