namespace DotShell.Core.Audio;

using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

/// <summary>
/// Настоящий анализатор звука: WASAPI loopback-захват системного аудио + FFT.
///
/// Зачем loopback, а не "спросить у плеера": плеер знает только факт
/// воспроизведения, но не спектр. А нам нужны именно полосы частот —
/// тогда эквалайзер и сталактиты двигаются от ТОЙ музыки, которая реально
/// звучит в колонках, будь то Spotify, браузер, игра или системные звуки.
///
/// Приватность: захват живёт только в памяти, ничего не пишется на диск
/// и никуда не отправляется — из сэмплов сразу считаются полосы и сэмплы
/// выбрасываются.
///
/// Если захват недоступен (RDP-сессия, нет устройства, эксклюзивный режим) —
/// IsRunning=false, и визуализации честно падают на имитацию, без падений.
/// </summary>
public sealed class AudioSpectrum : IDisposable
{
    /// <summary>Глобальная точка доступа для UI-контролов (ставится ShellServices).</summary>
    public static AudioSpectrum? Current { get; set; }

    /// <summary>Число полос спектра наружу.</summary>
    public const int BandCount = 40;

    private const int FftSize = 2048;          // ~23 Гц на бин при 48 кГц — хватает для басов
    private const float MinDb = -55f;          // тише этого считаем тишиной
    private const float MaxDb = -5f;

    private WasapiLoopbackCapture? _capture;
    private readonly float[] _mono = new float[FftSize];
    private int _monoFill;
    private float[] _levels = new float[BandCount];
    private readonly float[] _smooth = new float[BandCount];
    private float _gain = 1f;                  // авто-усиление: тихая музыка тоже видна
    private float _silenceHold;
    private volatile bool _hasSignal;

    /// <summary>Переиспользуемый буфер FFT.</summary>
    private readonly Complex[] _fftBuffer = new Complex[FftSize];

    /// <summary>Свежий снимок полос (0..1). Массив подменяется целиком — читать без блокировок.</summary>
    public float[] Levels => _levels;

    public bool IsRunning { get; private set; }

    /// <summary>Когда последний раз пришли реальные сэмплы (Environment.TickCount).</summary>
    private volatile int _lastDataTick;

    /// <summary>Сколько миллисекунд тишина в захвате (для сторожа).</summary>
    public int SilentForMs => IsRunning ? unchecked(Environment.TickCount - _lastDataTick) : 0;

    /// <summary>В системе сейчас что-то звучит (порог по громкости с удержанием).</summary>
    public bool HasSignal => _hasSignal;

    /// <summary>
    /// Нижние полосы (бас и бочка) одной величиной 0..1. По ней дышит акцент:
    /// визуализация в пилюле уже есть, а тема на музыку не реагировала.
    /// </summary>
    public float Bass { get; private set; }

    /// <summary>Почему захват не стартовал (null = всё хорошо).</summary>
    public string? Error { get; private set; }

    public event Action? SignalChanged;

    /// <summary>
    /// Устройство вывода, с которого снимаем звук. null — системное по умолчанию.
    ///
    /// Зачем выбор: loopback снимает только то устройство, которое указано. Если
    /// музыка играет в наушниках, а «по умолчанию» колонки — в захвате тишина,
    /// и эквалайзер выглядит сломанным, хотя он исправно молчит.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>Активные устройства вывода: id и человеческое имя.</summary>
    public static List<(string Id, string Name)> OutputDevices()
    {
        var list = new List<(string, string)>();

        try
        {
            using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(
                         NAudio.CoreAudioApi.DataFlow.Render,
                         NAudio.CoreAudioApi.DeviceState.Active))
            {
                try { list.Add((device.ID, device.FriendlyName)); }
                catch { }
                finally { device.Dispose(); }
            }
        }
        catch { }

        return list;
    }
    /// <summary>Пытается запустить захват. Возвращает false, если устройство недоступно.</summary>
    public bool Start()
    {
        if (IsRunning) return true;
        try
        {
            _capture = CreateCapture();
            _capture.DataAvailable += OnDataAvailable;
            // Устройство вывода сменилось — WASAPI останавливает запись. Раньше это
        // означало «эквалайзер умер до перезапуска приложения».
        _capture.RecordingStopped += (_, _) =>
        {
            IsRunning = false;
            _hasSignal = false;
            Error = "захват остановлен — перезапускаю";
            Task.Run(async () =>
            {
                await Task.Delay(1200);
                try { Start(); } catch { }
            });
        };
            _capture.StartRecording();
            IsRunning = true;
            Error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Нет аудиоустройства, RDP, эксклюзивный режим у игры — всё это норма жизни,
            // а не причина ронять шелл. Визуализации упадут на имитацию.
            Error = ex.Message;
            IsRunning = false;
            _capture?.Dispose();
            _capture = null;
            return false;
        }
    }

    /// <summary>
    /// Сторож: захват живёт на WASAPI-loopback и умирает при смене устройства
    /// вывода (наушники, HDMI, Bluetooth) — а раньше об этом никто не узнавал,
    /// и эквалайзер в пилюле застывал. Здесь перезапускаем его сами.
    /// </summary>
    public void EnsureAlive()
    {
        try
        {
            if (!IsRunning)
            {
                Start();
                return;
            }

            // Данные не приходят давно, а запись «идёт» — поток умер молча.
            if (Environment.TickCount - _lastDataTick > 4000)
            {
                Error = "захват молчит — перезапуск";
                Stop();
                Start();
            }
        }
        catch { }
    }

    /// <summary>Захват на выбранном устройстве (или системном по умолчанию).</summary>
    private WasapiLoopbackCapture CreateCapture()
    {
        try
        {
            if (!string.IsNullOrEmpty(DeviceId))
            {
                using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                var device = enumerator.GetDevice(DeviceId);
                if (device is not null) return new WasapiLoopbackCapture(device);
            }
        }
        catch { }

        return new WasapiLoopbackCapture();
    }

    public void Stop()
    {
        try { _capture?.StopRecording(); } catch { }
        IsRunning = false;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        try
        {
            var wf = _capture?.WaveFormat;
            if (wf is null) return;

            int channels = Math.Max(1, wf.Channels);
            // Тишина — не смерть: WASAPI присылает колбэки и с нулями. Обновляем
            // метку здесь, до разбора, иначе сторож перезапускал бы захват в тишине.
            _lastDataTick = Environment.TickCount;

            int bytesPerSample = wf.BitsPerSample / 8;
            int samples = e.BytesRecorded / bytesPerSample / channels;
            if (samples <= 0) return;

            // Сводим в моно и складываем в кольцо до размера FFT-кадра.
            float peak = 0f;
            for (int i = 0; i < samples && _monoFill < FftSize; i++)
            {
                float v = ReadSample(e.Buffer, i * channels, bytesPerSample, wf, channels);
                peak = Math.Max(peak, Math.Abs(v));
                _mono[_monoFill++] = v;
            }

            UpdateSilence(peak);
            if (_monoFill < FftSize) return;
            _monoFill = 0;

            Analyze();
        }
        catch
        {
            // Поток захвата не должен ронять процесс ни при каких чудесах формата.
        }
    }

    private static float ReadSample(byte[] buf, int sampleIndex, int bytesPerSample, WaveFormat wf, int channels)
    {
        // Усредняем каналы: спектр музыки от раскладки стерео зависеть не должен.
        float acc = 0f;
        for (int ch = 0; ch < channels; ch++)
        {
            int off = (sampleIndex + ch) * bytesPerSample;
            if (off + bytesPerSample > buf.Length) break;
            acc += bytesPerSample == 4
                ? (wf.Encoding == WaveFormatEncoding.IeeeFloat
                    ? BitConverter.ToSingle(buf, off)
                    : BitConverter.ToInt32(buf, off) / 2147483648f)
                : BitConverter.ToInt16(buf, off) / 32768f;
        }
        return acc / channels;
    }

    private void UpdateSilence(float peak)
    {
        // Тишина в loopback = абсолютные нули. Держим сигнал ещё ~0.4 c после
        // последнего звука, чтобы сталактиты не дёргались между треками.
        bool now = peak > 0.002f;
        if (now) _silenceHold = 0.4f;
        else _silenceHold = Math.Max(0f, _silenceHold - 0.02f);

        bool signal = _silenceHold > 0f;
        if (signal != _hasSignal)
        {
            _hasSignal = signal;
            SignalChanged?.Invoke();
        }
    }

    private void Analyze()
    {
        // Окно Ханна: без него спектр размазывается по соседним бинам ("утечка").
        // Буфер переиспользуем: раньше массив на 2048 Complex аллоцировался
        // ~40 раз в секунду, и сборщик мусора дёргался вместе с эквалайзером.
        var complex = _fftBuffer;
        for (int i = 0; i < FftSize; i++)
        {
            float window = 0.5f - 0.5f * (float)Math.Cos(2 * Math.PI * i / (FftSize - 1));
            complex[i].X = _mono[i] * window;
            complex[i].Y = 0;
        }

        FastFourierTransform.FFT(true, (int)Math.Log2(FftSize), complex);

        var wf = _capture!.WaveFormat;
        double binHz = wf.SampleRate / (double)FftSize;

        // 40 логарифмических полос от 40 Гц до 16 кГц: на слух музыка ровно так и устроена.
        var next = new float[BandCount];
        float frameMax = 1e-4f;
        for (int b = 0; b < BandCount; b++)
        {
            double f0 = 40.0 * Math.Pow(16000.0 / 40.0, b / (double)BandCount);
            double f1 = 40.0 * Math.Pow(16000.0 / 40.0, (b + 1) / (double)BandCount);
            int i0 = Math.Max(1, (int)(f0 / binHz));
            int i1 = Math.Min(FftSize / 2 - 1, (int)Math.Ceiling(f1 / binHz));

            float mag = 0f;
            for (int i = i0; i <= i1; i++)
            {
                var m = (float)Math.Sqrt(complex[i].X * complex[i].X + complex[i].Y * complex[i].Y);
                mag = Math.Max(mag, m); // пик в полосе читается лучше среднего
            }

            float db = 20f * (float)Math.Log10(mag + 1e-9f);
            float v = Math.Clamp((db - MinDb) / (MaxDb - MinDb), 0f, 1f);
            next[b] = v;
            frameMax = Math.Max(frameMax, v);
        }

        // Авто-усиление: тихий трек растягивается на всю высоту полос,
        // громкий не упирается в потолок навсегда. Плавное, чтобы не мигало.
        _gain += (Math.Clamp(0.85f / frameMax, 1f, 6f) - _gain) * 0.1f;

        for (int b = 0; b < BandCount; b++)
        {
            float target = Math.Clamp(next[b] * _gain, 0f, 1f);
            // Атака быстрая, спад медленный — так глаз читает ритм.
            _smooth[b] = target > _smooth[b]
                ? target
                : _smooth[b] * 0.82f + target * 0.18f;
        }

        _levels = _smooth; // подмена ссылки: читатели видят согласованный снимок

        float low = 0;
        int lowCount = Math.Min(6, _smooth.Length);
        for (int i = 0; i < lowCount; i++) low += _smooth[i];
        low /= Math.Max(1, lowCount);

        // Быстро вверх, медленно вниз: так «удар» виден, а дрожание не мешает.
        Bass = low > Bass ? low : Bass * 0.86f;
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
        if (Current == this) Current = null;
    }
}