namespace DotShell.Core.Clipboard;

using DotShell.Core.Native;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

/// <summary>
/// Следит за системным буфером обмена и складывает всё в историю.
///
/// Как это устроено:
///  1. Создаём невидимое "почтовое" окно и подписываем его на WM_CLIPBOARDUPDATE.
///     Windows сама присылает сообщение каждый раз, когда кто-то что-то скопировал.
///     Это НЕ polling — ничего не опрашиваем по кругу, CPU не грузим.
///  2. В обработчике читаем содержимое через WinRT Clipboard (он умеет и текст, и картинки, и файлы).
///  3. Картинки сохраняем как .png в %LOCALAPPDATA%\DotShell\clipboard\img —
///     держать мегабайты битмапов в памяти нельзя, а путь в истории хранить можно.
///  4. Кладём запись в IClipboardHistoryStore.
///
/// Тонкость: когда мы САМИ вставляем запись из истории обратно в буфер,
/// Windows снова пришлёт нам уведомление. Чтобы история не "закольцевалась",
/// перед своей записью поднимаем флаг _suppressNext.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private readonly NativeMessageWindow _window;
    private readonly IClipboardHistoryStore _store;
    private readonly string _imageDirectory;
    private bool _suppressNext;
    private string? _lastHash;
    private DateTime _lastChangeUtc = DateTime.MinValue;

    public ClipboardWatcher(IClipboardHistoryStore store, NativeMessageWindow window)
    {
        _store = store;
        _window = window;
        _imageDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DotShell", "clipboard");
        Directory.CreateDirectory(_imageDirectory);

        _window.MessageReceived += OnMessage;
        if (!Win32.AddClipboardFormatListener(_window.Handle))
            throw new InvalidOperationException(
                $"AddClipboardFormatListener failed: {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
    }

    private void OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != Win32.WM_CLIPBOARDUPDATE) return;

        if (_suppressNext) { _suppressNext = false; return; }

        // Защита от "пулемёта": некоторые приложения пишут в буфер сериями.
        if ((DateTime.UtcNow - _lastChangeUtc).TotalMilliseconds < 60) return;
        _lastChangeUtc = DateTime.UtcNow;

        _ = CaptureAsync();
    }

    private async Task CaptureAsync()
    {
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (view is null) return;

            // ---- 1. Файлы (проверяем раньше текста: при копировании файлов в буфере тоже есть текст) ----
            if (view.Contains(StandardDataFormats.StorageItems))
            {
                var items = await view.GetStorageItemsAsync();
                if (items.Count > 0)
                {
                    var text = string.Join("\n", items.Select(i => i.Path));
                    Add(new ClipboardItem { Kind = ClipboardKind.Files, Text = text });
                    return;
                }
            }

            // ---- 2. Картинка ----
            if (view.Contains(StandardDataFormats.Bitmap))
            {
                var reference = await view.GetBitmapAsync();
                var path = await SavePngAsync(reference);
                if (path is not null)
                {
                    Add(new ClipboardItem
                    {
                        Kind = ClipboardKind.Image,
                        Text = Path.GetFileName(path),
                        ImagePath = path
                    });
                    return;
                }
            }

            // ---- 3. Текст / ссылка ----
            if (view.Contains(StandardDataFormats.Text))
            {
                var text = await view.GetTextAsync();
                if (!string.IsNullOrEmpty(text))
                {
                    var kind = LooksLikeLink(text) ? ClipboardKind.Link : ClipboardKind.Text;
                    Add(new ClipboardItem { Kind = kind, Text = text });
                    return;
                }
            }

            // ---- 4. Ссылка как отдельный формат ----
            if (view.Contains(StandardDataFormats.WebLink))
            {
                var uri = await view.GetWebLinkAsync();
                Add(new ClipboardItem { Kind = ClipboardKind.Link, Text = uri.ToString() });
            }
        }
        catch
        {
            // Буфер может быть занят другим процессом (COMException). Это нормально — просто пропускаем.
        }
    }

    private void Add(ClipboardItem item)
    {
        // Не пишем одно и то же подряд (например, когда приложение обновляет свой же текст).
        var hash = item.Text.Length + ":" + item.Kind + ":" + (item.Text.Length > 64 ? item.Text[..64] : item.Text);
        if (hash == _lastHash && (DateTime.UtcNow - _lastChangeUtc).TotalSeconds < 2) return;
        _lastHash = hash;

        _store.Add(item);
    }

    private static bool LooksLikeLink(string text)
    {
        var t = text.Trim();
        if (t.Length > 2000 || t.Contains('\n')) return false;
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> SavePngAsync(IRandomAccessStreamReference reference)
    {
        try
        {
            using var source = await reference.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(source);
            var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var name = $"{DateTime.Now:yyyyMMdd-HHmmss-fff}.png";
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(_imageDirectory);
            var file = await folder.CreateFileAsync(name, Windows.Storage.CreationCollisionOption.GenerateUniqueName);

            using var outStream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outStream);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();
            return file.Path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Вернуть запись из истории в системный буфер (потом приложение вызывает Ctrl+V).
    /// </summary>
    public void PushToClipboard(ClipboardItem item)
    {
        _suppressNext = true;
        var package = new DataPackage();
        if (item.Kind == ClipboardKind.Link && Uri.TryCreate(item.Text, UriKind.Absolute, out var uri))
            package.SetWebLink(uri);
        package.SetText(item.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        _store.BumpUsage(item.Id);
    }

    public void Dispose()
    {
        _window.MessageReceived -= OnMessage;
        try { Win32.RemoveClipboardFormatListener(_window.Handle); } catch { }
    }
}
