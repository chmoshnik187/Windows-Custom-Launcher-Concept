namespace DotShell.Island.Wpf;

using DotShell.Core;
using DotShell.Core.Theming;
using DotShell.Core.Wallpaper;

/// <summary>
/// Сторож расписания обоев: раз в двадцать секунд смотрит, не наступил ли
/// следующий слот. Работает без окна — окно только редактирует расписание.
///
/// Главная осторожность: не переставлять обои каждую минуту. Поэтому в
/// `wallpaper-schedule.json` хранится ключ последнего слота (час + путь), и
/// повторно он применяется только по явной кнопке «Применить сейчас» или когда
/// человек сам сменил картинку в слоте.
///
/// Вторая половина фичи — тема: после смены картинки акцент сразу считается по
/// самому файлу слота, не дожидаясь снимка рабочего стола. Так «утро», «день» и
/// «ночь» отличаются не только обоями, но и цветом острова.
/// </summary>
public sealed class WallpaperScheduleService : IDisposable
{
    private readonly System.Timers.Timer _timer;
    private readonly object _gate = new();
    private readonly ThemeBus _theme;
    private readonly Func<bool> _autoAccent;
    private string _lastFailureKey = string.Empty;

    public WallpaperScheduleService(ThemeBus theme, Func<bool>? autoAccent = null)
    {
        _theme = theme;
        _autoAccent = autoAccent ?? (() => true);
        _timer = new System.Timers.Timer(TimeSpan.FromSeconds(20).TotalMilliseconds)
        {
            AutoReset = true,
        };
        _timer.Elapsed += (_, _) => ApplyDue(force: false);
    }

    public void Start()
    {
        ApplyDue(force: false);
        _timer.Start();
    }

    /// <summary>Проверить расписание сейчас (после правки в окне).</summary>
    public void Refresh() => ApplyDue(force: false);

    /// <summary>Поставить обои текущего слота немедленно, даже если он уже применялся.</summary>
    public (bool Ok, string Message) ApplyNow() => ApplyDue(force: true);

    private (bool Ok, string Message) ApplyDue(bool force)
    {
        lock (_gate)
        {
            try
            {
                var schedule = WallpaperScheduleStore.Load();
                if (!schedule.Enabled)
                    return (false, "расписание выключено");

                var slot = schedule.CurrentSlot(DateTime.Now);
                if (string.IsNullOrWhiteSpace(slot.Path))
                    return (false, $"у слота «{slot.Name}» не выбрана картинка");

                var key = WallpaperSchedule.KeyFor(slot);
                if (!force && string.Equals(key, schedule.LastAppliedKey, StringComparison.OrdinalIgnoreCase))
                    return (true, $"уже стоят обои слота «{slot.Name}»");

                // Даже если расписание включили правкой файла, а не через окно,
                // исходные обои должны быть запомнены ДО первой подмены.
                if (!schedule.OriginalCaptured)
                {
                    WallpaperApi.CaptureOriginal(schedule);
                    WallpaperScheduleStore.Save(schedule);
                }

                if (!WallpaperApi.TryApply(slot.Path, out var error))
                {
                    // Таймер тикает каждые 20 секунд: без этого битый путь
                    // завалил бы лог одинаковыми строками.
                    if (force || !string.Equals(key, _lastFailureKey, StringComparison.OrdinalIgnoreCase))
                        Log.Info($"wallpaper schedule: слот «{slot.Name}» не применён: {error} ({slot.Path})");
                    _lastFailureKey = key;
                    return (false, $"не удалось: {error}");
                }

                _lastFailureKey = string.Empty;
                schedule.LastAppliedKey = key;
                WallpaperScheduleStore.Save(schedule);
                Log.Info($"wallpaper schedule: применён слот «{slot.Name}» ({slot.Path})");
                NudgeAccent(slot.Path);
                return (true, $"обои слота «{slot.Name}» применены");
            }
            catch (Exception ex)
            {
                Log.Info($"wallpaper schedule: ошибка: {ex.Message}");
                return (false, $"ошибка: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Пересчитать акцент по файлу слота. Тот же экстрактор, что у AccentSampler,
    /// но без снимка экрана: окно настроек или игра поверх стола не помешают.
    /// </summary>
    private void NudgeAccent(string path)
    {
        try
        {
            if (!_autoAccent()) return;

            var (pixels, width, height) = AccentSampler.LoadImageAsBgra(path, 96);
            if (pixels is null || width <= 0 || height <= 0) return;

            var result = ColorExtractor.FromBgra(pixels, width, height);
            if (result.Confidence < 0.06) return;

            _theme.EaseToward(result.Accent);
        }
        catch (Exception ex)
        {
            Log.Info($"wallpaper schedule: акцент не пересчитан: {ex.Message}");
        }
    }

    public void Dispose() => _timer.Dispose();
}
