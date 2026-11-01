namespace DotShell.Core.Settings;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Все настройки программы в одном классе.
/// Файл лежит в %LOCALAPPDATA%\DotShell\settings.json и редактируется обычным блокнотом —
/// это специально: "райсинг" начинается с того, что конфиг можно править руками.
/// </summary>
public sealed class AppSettings
{
    // ---- Внешний вид (точечная сетка) ----
    /// <summary>Размер ячейки точки в логических пикселях.</summary>
    public int CellSize { get; set; } = 4;

    /// <summary>Зазор между точками.</summary>
    public int Gap { get; set; } = 2;

    /// <summary>Основной акцентный цвет в формате #RRGGBB. Используется, если AutoAccent = false.</summary>
    public string AccentHex { get; set; } = "#D71920";

    /// <summary>true = цвет сам подбирается из обоев.</summary>
    public bool AutoAccent { get; set; } = true;

    /// <summary>Показывать ли "погасшие" точки сетки (фирменный вид Nothing).</summary>
    public bool ShowGhostDots { get; set; } = true;

    /// <summary>
    /// Рисунок внутри капсулы: none / waves / topo / rays / aurora / grid.
    /// Процедурный, в цвете акцента — поэтому сам подстраивается под обои.
    /// </summary>
    public string BackdropStyle { get; set; } = "dots";

    /// <summary>
    /// Устройство вывода для эквалайзера. Пусто — системное по умолчанию.
    /// </summary>
    public string AudioDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Показывать напоминания тостами Windows (иначе только пузырь трея).
    /// </summary>
    public bool ToastEnabled { get; set; } = true;

    /// <summary>
    /// Запускать свёрнутым: только капсула, без раскрытия.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>
    /// Проверять обновления при старте (окно появляется, только если версия новее).
    /// </summary>
    public bool UpdateCheckOnStart { get; set; } = true;

    /// <summary>
    /// Как часто проверять обновления, часы. 0 — только при старте.
    /// </summary>
    public int UpdateCheckHours { get; set; } = 6;

    /// <summary>
    /// Погода в Фаренгейтах вместо Цельсия.
    /// </summary>
    public bool WeatherFahrenheit { get; set; }

    /// <summary>
    /// Режим «не мешать»: когда на переднем плане развёрнутое чужое окно, остров
    /// сжимается в тонкую полоску и разворачивается по наведению.
    /// </summary>
    public bool QuietOnMaximized { get; set; } = true;

    /// <summary>
    /// Город для виджета погоды. Пусто — Москва.
    /// </summary>
    public string WeatherCity { get; set; } = "Москва";

    /// <summary>
    /// Вторая валюта в виджете курса (код ЦБ). По умолчанию юань.
    /// </summary>
    public string CurrencySecond { get; set; } = "R01375";

    /// <summary>
    /// Виджет трея: прятать служебные процессы (прокси, webhelper).
    /// </summary>
    public bool TrayHideHelpers { get; set; } = true;

    /// <summary>
    /// Виджет трея: какие значки человек скрыл вручную (имена процессов).
    /// </summary>
    public List<string> TrayHidden { get; set; } = new();

    /// <summary>
    /// Виджет трея: закреплённые программы (пути к exe) — видны всегда.
    /// </summary>
    public List<string> TrayPinned { get; set; } = new();

    /// <summary>
    /// Свои курсоры в стиле точек вместо системных.
    /// </summary>
    public bool CustomCursor { get; set; }

    /// <summary>
    /// Пути курсоров до нашей подмены (JSON снимка) — для отката.
    /// </summary>
    public string CursorOriginal { get; set; } = string.Empty;

    /// <summary>
    /// Держать акцент Windows равным акценту шелла (цвет из обоев).
    /// </summary>
    public bool WindowsAccentSync { get; set; }

    /// <summary>
    /// Акцент Windows до первого включения синхронизации (JSON снимка) —
    /// чтобы отключение вернуло ровно то, что было у человека.
    /// </summary>
    public string WindowsAccentOriginal { get; set; } = string.Empty;

    /// <summary>
    /// Открывать папки вкладкой в уже открытом проводнике (Windows 11 22H2+).
    /// Выключено — как в системе: новое окно на каждую папку.
    /// </summary>
    public bool ExplorerTabs { get; set; } = true;

    /// <summary>
    /// Полное оформление Windows: тёмные заголовки всех окон, а не только остров.
    /// Выключено — шелл живёт сам по себе и не трогает систему.
    /// </summary>
    public bool SystemStyleFull { get; set; }

    /// <summary>
    /// Версия обновления, про которую человек сказал «не напоминать».
    /// Пусто — напоминаем про любую новую.
    /// </summary>
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    /// <summary>Выбранный скин (nothing / cmf / phosphor / violet / amber).</summary>
    public string Skin { get; set; } = "nothing";

    /// <summary>Исполнитель в плеере: dots (крупные точки) / fine (мелкие, плотнее) / text.</summary>
    public string ArtistStyle { get; set; } = "fine";

    /// <summary>Раскладка плеера в острове: cover (обложка) / dots (точки) / row (строка).</summary>
    public string PlayerStyle { get; set; } = "cover";

    /// <summary>
    /// Брать цвет заголовка трека из обложки, а не из обоев.
    /// Общий акцент остаётся от обоев — иначе интерфейс перекрашивался бы
    /// при каждой смене песни.
    /// </summary>
    public bool PlayerAccentFromCover { get; set; } = true;

    /// <summary>Плотность рисунка, 0..100 %. Влияет только на рисунок, не на текст.</summary>
    public int BackdropOpacity { get; set; } = 45;

    // ---- Остров ----
    /// <summary>Отступ острова от верхнего края монитора, px.</summary>
    public int IslandTopOffset { get; set; } = 10;

    /// <summary>Последнее положение окна острова (-32000 = "не задано, центрировать").</summary>
    public int IslandLeft { get; set; } = -32000;

    /// <summary>Последнее положение окна острова (-32000 = "не задано, центрировать").</summary>
    public int IslandTop { get; set; } = -32000;

    /// <summary>Ширина "мёртвой зоны" слева/справа от камеры ноутбука, px (чтобы остров её не закрывал).</summary>
    public int NotchAvoidWidth { get; set; } = 0;

    /// <summary>Интервал опроса курсора для "умного" клика по острову, мс. 0 = отключить.</summary>
    public int HitTestPollMs { get; set; } = 40;

    // ---- Поведение ----
    /// <summary>Скрывать остров, когда какое-то окно развернуто на весь экран (игры, видео).</summary>
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Сколько записей хранить в истории буфера обмена (0 = до перезагрузки, без лимита).</summary>
    public int ClipboardMaxItems { get; set; } = 500;

    /// <summary>Хранить буфер обмена между перезагрузками.</summary>
    public bool ClipboardPersist { get; set; } = false;

    /// <summary>Запускать вместе с Windows.</summary>
    public bool RunAtStartup { get; set; } = false;

    /// <summary>Прятать иконки рабочего стола (программы живут в острове и на виджетах).</summary>
    public bool HideDesktopIcons { get; set; } = false;

    /// <summary>Показывать подсказки хоткеев активной программы при зажатых Ctrl/Alt/Win.</summary>
    public bool HotkeyHudEnabled { get; set; } = false;

    // ---- Обои / авто-цвет ----
    /// <summary>Как часто брать кадр рабочего стола для подбора цвета, мс.</summary>
    public int AccentSampleIntervalMs { get; set; } = 8000;

    /// <summary>Насколько "лениво" цвет догоняет новый (0..1, меньше = плавнее).</summary>
    public double AccentSmoothing { get; set; } = 0.25;

    /// <summary>
    /// Прозрачность ЧЁРНОГО фона острова в процентах (30..100).
    /// Влияет только на подложку: акцент, текст и точки остаются плотными.
    /// </summary>
    public int IslandOpacity { get; set; } = 92;

    /// <summary>Провайдер быстрого чата на вкладке ИИ: имя из списка пресетов или "Custom".</summary>
    public string AiProvider { get; set; } = "Ollama";

    /// <summary>Адрес OpenAI-совместимого API (для Custom; у пресетов подставляется сам).</summary>
    public string AiBaseUrl { get; set; } = "http://localhost:11434/v1";

    /// <summary>Модель для быстрого чата.</summary>
    public string AiModel { get; set; } = "";

    /// <summary>
    /// Ключ API для чата. Хранится локально в settings.json и уходит
    /// только на указанный адрес API. Для локальной Ollama не нужен.
    /// </summary>
    public string AiApiKey { get; set; } = string.Empty;

    [JsonIgnore]
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotShell");

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>
    /// Переключить режим оформления до старта сервисов (вызывает установщик
    /// флагом `--style=full|island`). Читает и пишет settings.json напрямую.
    /// </summary>
    public static void SetSystemStyle(bool full)
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);

            AppSettings settings;
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
            }

            settings.SystemStyleFull = full;

            File.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(settings,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}




