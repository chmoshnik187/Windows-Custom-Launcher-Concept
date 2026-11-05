namespace DotShell.Core.Media;

using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Windows.Media.Control;
using Windows.Storage.Streams;

/// <summary>
/// Мост к тому, что сейчас играет в системе.
///
/// Используется SMTC (System Media Transport Controls) — единый Windows-механизм,
/// через который приложения "докладывают" системе о воспроизведении.
/// Работает со Spotify, YouTube в браузере, Apple Music, foobar2000, VLC,
/// Яндекс.Музыкой и всем остальным, что зарегистрировано в SMTC.
///
/// Отсюда мы берём:
///   • название, артиста, альбом;
///   • обложку (как поток байт — дальше её можно пикселизировать в точки);
///   • статус (играет/пауза) и позицию;
///   • команды: play, pause, next, prev, seek.
///
/// Класс реализует INotifyPropertyChanged, поэтому его можно просто привязать
/// в XAML через {Binding} — UI обновится сам.
/// </summary>
public sealed class MediaBridge : INotifyPropertyChanged, IAsyncDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private string _sourceApp = string.Empty;
    private bool _isPlaying;
    private bool _hasSession;
    private TimeSpan _position;
    // SMTC присылает позицию редко (некоторые плееры — раз в секунду, некоторые
    // только при смене трека). Поэтому храним «якорь» и считаем позицию сами:
    // иначе ползунок стоит на месте, а музыка играет.
    private TimeSpan _anchorPosition;
    private TimeSpan _lastReported;
    private DateTime _anchorStamp = DateTime.MinValue;
    private TimeSpan _duration;
    private byte[]? _artworkBytes;

    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Artist { get => _artist; private set => Set(ref _artist, value); }
    public string Album { get => _album; private set => Set(ref _album, value); }

    /// <summary>Имя процесса-источника (например, Spotify.exe) — пригодится для whitelist'ов.</summary>
    public string SourceApp { get => _sourceApp; private set => Set(ref _sourceApp, value); }

    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    /// <summary>Есть ли вообще активная медиа-сессия. Если false — остров показывает только часы.</summary>
    public bool HasSession { get => _hasSession; private set => Set(ref _hasSession, value); }

    public TimeSpan Position { get => _position; private set => Set(ref _position, value); }

    /// <summary>
    /// Позиция с учётом времени, прошедшего с последнего ответа плеера. Это то,
    /// что должно показывать ползунок и часы: настоящее «сейчас», а не снимок.
    /// </summary>
    public TimeSpan LivePosition
    {
        get
        {
            if (!IsPlaying || _anchorStamp == DateTime.MinValue) return _position;

            var position = _anchorPosition + (DateTime.UtcNow - _anchorStamp);
            if (position < TimeSpan.Zero) position = TimeSpan.Zero;
            if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
            return position;
        }
    }
    public TimeSpan Duration { get => _duration; private set => Set(ref _duration, value); }

    /// <summary>Сырые байты обложки (обычно JPEG/PNG). null, если обложки нет.</summary>
    public byte[]? ArtworkBytes { get => _artworkBytes; private set => Set(ref _artworkBytes, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Поднять мост. Безопасно вызывать повторно.</summary>

    /// <summary>
    /// AUMID вида `com.dotify.app-siw` → «Dotify». В интерфейсе это имя источника,
    /// и сырая строка там выглядит как мусор.
    /// </summary>
    private static string PrettySource(string? aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return string.Empty;

        string text = aumid;

        int bang = text.IndexOf('!');
        if (bang > 0) text = text[..bang];

        if (text.StartsWith("com.", StringComparison.OrdinalIgnoreCase)) text = text[4..];
        if (text.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) text = text["Microsoft.".Length..];

        int dot = text.IndexOf('.');
        if (dot > 0) text = text[..dot];

        if (text.Length == 0) return aumid;
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    public async Task StartAsync()
    {
        if (_manager is null)
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += OnSessionsChanged;
        }
        Attach(_manager.GetCurrentSession());
    }

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        Attach(sender.GetCurrentSession());
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        // Пока включена фикстура, настоящие сессии игнорируем: иначе пустое
        // окружение сразу затрёт тестовый трек.
        if (_fixture) return;

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }

        _session = session;
        HasSession = session is not null;

        if (session is null)
        {
            Title = Artist = Album = string.Empty;
            IsPlaying = false;
            ArtworkBytes = null;
            Position = Duration = TimeSpan.Zero;
            return;
        }

        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;

        _ = RefreshAsync();
        RefreshPlaybackInfo();
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => RefreshPlaybackInfo();

    private void RefreshPlaybackInfo()
    {
        if (_session is null) return;
        var info = _session.GetPlaybackInfo();
        IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        var timeline = _session.GetTimelineProperties();
        Anchor(timeline.Position);
        Duration = timeline.EndTime;
    }

    private async Task RefreshAsync()
    {
        if (_session is null) return;
        try
        {
            var props = await _session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            Title = props.Title ?? string.Empty;
            Artist = props.Artist ?? string.Empty;
            Album = props.AlbumTitle ?? string.Empty;
            SourceApp = PrettySource(_session.SourceAppUserModelId);

            ArtworkBytes = await ReadThumbnailAsync(props.Thumbnail);

            var timeline = _session.GetTimelineProperties();
            Anchor(timeline.Position);
            Duration = timeline.EndTime;
            IsPlaying = _session.GetPlaybackInfo()?.PlaybackStatus ==
                        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            // Приложение могло закрыться между получением сессии и запросом свойств.
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null) return null;
        try
        {
            using var stream = await reference.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            return ms.Length > 0 ? ms.ToArray() : null;
        }
        catch
        {
            return null;
        }
    }

    // ---------- команды ----------

    public async Task TogglePlayAsync()
    {
        if (_session is null) return;
        try { await _session.TryTogglePlayPauseAsync(); } catch { }
        RefreshPlaybackInfo();
    }

    public async Task NextAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        if (_session is null) return;
        try { await _session.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekAsync(TimeSpan to)
    {
        if (_session is null) return;
        try { await _session.TryChangePlaybackPositionAsync(to.Ticks); } catch { }
        Anchor(to);
    }

    private bool _fixture;

    /// <summary>
    /// Подменить реальную медиа-сессию тестовым треком.
    ///
    /// Нужно для проверки плеера без живого Spotify/YouTube: включается флагом
    /// `--player-fixture`, дальше весь UI (остров, виджеты, обзор) работает с этим
    /// треком как с настоящим. Позиция едет сама: якорь + прошедшее время.
    /// </summary>
    public void ApplyFixture()
    {
        _fixture = true;

        HasSession = true;
        Title = "Ночной дождь";
        Artist = "DotShell · фикстура";
        Album = "Проверка плеера";
        SourceApp = "Fixture";
        IsPlaying = true;
        Duration = TimeSpan.FromSeconds(212);
        ArtworkBytes = null;

        Anchor(TimeSpan.Zero);
    }

    /// <summary>Таймер в UI может дёргать это раз в секунду, чтобы ползунок ехал.</summary>
    public void Tick()
    {
        if (_session is null || !IsPlaying || Duration == TimeSpan.Zero) return;

        var timeline = _session.GetTimelineProperties();

        // Часть плееров (например, Dotify) отдаёт позицию 0 всё время, пока играет.
        // Сравнивать с расчётной позицией нельзя: тогда мы будем обнулять счётчик
        // каждую секунду и ползунок будет вечно стоять на нуле. Поэтому реагируем
        // только на ИЗМЕНЕНИЕ того, что прислал плеер.
        if ((timeline.Position - _lastReported).Duration() > TimeSpan.FromMilliseconds(500))
            Anchor(timeline.Position);
    }

    private void Anchor(TimeSpan position)
    {
        _anchorPosition = position;
        _lastReported = position;
        _anchorStamp = DateTime.UtcNow;
        Position = position;
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        if (_manager is not null)
        {
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }
        await Task.CompletedTask;
    }
}
