namespace DotShell.Island.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotShell.Core;
using DotShell.Core.Clipboard;
using DotShell.Core.Settings;
using DotShell.Core.Media;
using DotShell.Core.Theming;

/// <summary>
/// Состояние острова: всё, к чему привязан XAML.
///
/// Здесь намеренно нет никакой логики Win32 и никакой работы с окном —
/// только данные. Это позволяет потом заменить остров на лаунчер или панель,
/// не переписывая ядро: они будут читать ту же ViewModel.
/// </summary>
public sealed class IslandViewModel : INotifyPropertyChanged
{
    private readonly ShellServices _services;

    private bool _isExpanded;
    private string _activeTab = "notes";
    private bool _isFullscreenNow;
    private string _noteText = string.Empty;
    private string _statusLine = "готов";

    public MediaBridge Media => _services.Media;
    public SystemStats Stats => _services.Stats;
    public ThemeBus Theme => _services.Theme;
    public AppSettingsSnapshot Settings => _snapshot;

    /// <summary>Записи буфера обмена для списка (готовый к привязке, отсортированный).</summary>
    public ObservableCollection<ClipboardItem> ClipboardItems { get; } = new();

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    public string ActiveTab { get => _activeTab; set => Set(ref _activeTab, value); }

    /// <summary>Что-то сейчас открыто на весь экран (игра, видео).</summary>
    public bool IsFullscreenNow { get => _isFullscreenNow; set => Set(ref _isFullscreenNow, value); }

    public string NoteText { get => _noteText; set => Set(ref _noteText, value); }

    /// <summary>Строка состояния для нижней части острова — мелочь, а отладку спасает.</summary>
    public string StatusLine { get => _statusLine; set => Set(ref _statusLine, value); }

    /// <summary>Одна строка "что играет" для свёрнутого острова.</summary>
    public string MediaLine
    {
        get
        {
            var title = Media.Title;
            if (string.IsNullOrWhiteSpace(title)) return "НИЧЕГО НЕ ИГРАЕТ";
            return title.Length > 34 ? title[..34].ToUpperInvariant() + "…" : title.ToUpperInvariant();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly AppSettingsSnapshot _snapshot;

    public IslandViewModel(ShellServices services)
    {
        _services = services;
        _snapshot = new AppSettingsSnapshot(services);

        services.ClipboardStore.Changed += RebuildClipboard;
        services.WindowWatcher.FullscreenChanged += fs =>
            RunOnUiThread(() => IsFullscreenNow = fs);

        RebuildClipboard();
        NoteText = NotesStore.Load();
    }

    private void RebuildClipboard()
    {
        RunOnUiThread(() =>
        {
            var items = _services.ClipboardStore.Items;
            ClipboardItems.Clear();
            foreach (var item in items.Take(80)) ClipboardItems.Add(item);
            StatusLine = $"буфер: {items.Count} записей";
        });
    }

    /// <summary>Вставить выбранную запись обратно в буфер (потом приложение получит Ctrl+V).</summary>
    public void UseClipboardItem(ClipboardItem item)
    {
        _services.Clipboard.PushToClipboard(item);
        StatusLine = $"скопировано: {item.Preview}";
    }

    public void TogglePin(ClipboardItem item) =>
        _services.ClipboardStore.SetPinned(item.Id, !item.Pinned);

    public void ClearClipboard() => _services.ClipboardStore.ClearUnpinned();

    public void SaveNote() => NotesStore.Save(NoteText);

    public void SearchClipboard(string query)
    {
        RunOnUiThread(() =>
        {
            var found = _services.ClipboardStore.Search(query, 80);
            ClipboardItems.Clear();
            foreach (var item in found) ClipboardItems.Add(item);
        });
    }

    private void RunOnUiThread(Action action)
    {
        var dispatcher = Microsoft.UI.Xaml.Application.Current?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess) action();
        else dispatcher.TryEnqueue(() => action());
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Заметки = обычный markdown-файл.
/// Специально не база данных: файл можно открыть в любом редакторе,
/// синхронизировать через OneDrive/git и не потерять при переустановке.
/// </summary>
public static class NotesStore
{
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotShell", "notes.md");

    public static string Load()
    {
        try { return File.Exists(Path) ? File.ReadAllText(Path) : string.Empty; }
        catch { return string.Empty; }
    }

    public static void Save(string text)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, text);
        }
        catch { }
    }
}

/// <summary>
/// Обёртка над настройками, чтобы XAML мог привязаться к ним напрямую
/// и получать уведомления об изменениях.
/// </summary>
public sealed class AppSettingsSnapshot : INotifyPropertyChanged
{
    private readonly ShellServices _services;
    public AppSettingsSnapshot(ShellServices services)
    {
        _services = services;
        _services.Settings.Changed += _ =>
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        };
    }

    public bool AutoAccent
    {
        get => _services.Settings.Current.AutoAccent;
        set { _services.Settings.Update(s => s.AutoAccent = value); }
    }

    public bool HideInFullscreen
    {
        get => _services.Settings.Current.HideInFullscreen;
        set { _services.Settings.Update(s => s.HideInFullscreen = value); }
    }

    public bool ShowGhostDots
    {
        get => _services.Settings.Current.ShowGhostDots;
        set { _services.Settings.Update(s => s.ShowGhostDots = value); }
    }

    public bool RunAtStartup
    {
        get => StartupManager.IsEnabled();
        set
        {
            if (StartupManager.SetEnabled(value))
                _services.Settings.Update(s => s.RunAtStartup = value);
        }
    }

    public int CellSize
    {
        get => _services.Settings.Current.CellSize;
        set { _services.Settings.Update(s => s.CellSize = Math.Clamp(value, 2, 12)); }
    }

    public int Gap
    {
        get => _services.Settings.Current.Gap;
        set { _services.Settings.Update(s => s.Gap = Math.Clamp(value, 0, 8)); }
    }

    public string AccentHex
    {
        get => _services.Settings.Current.AccentHex;
        set { _services.Settings.Update(s => s.AccentHex = value); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
