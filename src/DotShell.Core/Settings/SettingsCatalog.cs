namespace DotShell.Core.Settings;

using DotShell.Core.Clipboard; // FuzzyScore — тот же нечёткий поиск, что в буфере и лаунчере

/// <summary>Одна точная настройка Windows: название, слова-ключи, куда открыть.</summary>
public sealed class SettingsEntry
{
    public string Title { get; init; } = string.Empty;
    public string Keywords { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;
}

/// <summary>
/// «Объединитель настроек»: боль Windows — две параллельные вселенные настроек
/// (старая «Панель управления» и новые «Параметры») и пять уровней вложенности
/// до нужной галочки. Здесь — кураторский список ТОЧНЫХ страниц: пишешь
/// по-человечески («спящий режим», «где автозагрузка», «второй экран») и
/// попадаешь прямо в нужное окошко.
///
/// Всё открывается через ms-settings:/cpl/msc — это документированные адреса
/// самой Windows, админ-прав не требуют (кроме страниц, где Windows сама спросит UAC).
/// </summary>
public static class SettingsCatalog
{
    public static readonly List<SettingsEntry> Entries = new()
    {
        // Сеть
        new() { Title = "Wi-Fi", Keywords = "вайфай wifi беспроводная сеть интернет", Uri = "ms-settings:network-wifi" },
        new() { Title = "Bluetooth", Keywords = "блютус bluetooth наушники колонка сопряжение", Uri = "ms-settings:bluetooth" },
        new() { Title = "VPN", Keywords = "впн туннель рабочая сеть", Uri = "ms-settings:network-vpn" },
        new() { Title = "Сетевые подключения (адаптеры)", Keywords = "ip ethernet адаптер сменить адрес ncpa", Uri = "control.exe ncpa.cpl" },
        new() { Title = "Режим «в самолёте»", Keywords = "самолет airplane авиарежим", Uri = "ms-settings:network-airplanemode" },
        new() { Title = "Общий доступ по сети", Keywords = "расшарить папку сеть доступ discovery", Uri = "ms-settings:network-sharing" },

        // Экран и звук
        new() { Title = "Экран: разрешение и масштаб", Keywords = "монитор разрешение dpi масштаб второй экран", Uri = "ms-settings:display" },
        new() { Title = "Ночной свет", Keywords = "ночной свет blue light синий фильтр вечером", Uri = "ms-settings:nightlight" },
        new() { Title = "Проецирование (несколько мониторов)", Keywords = "второй экран дублировать расширить проектор", Uri = "ms-settings:display" },
        new() { Title = "Звук: устройства и громкость", Keywords = "звук колонки наушники микрофон громкость вывод", Uri = "ms-settings:sound" },
        new() { Title = "Тёмная или светлая тема", Keywords = "тема цвет темная светлая оформление", Uri = "ms-settings:colors" },

        // Устройства
        new() { Title = "Принтеры и сканеры", Keywords = "принтер сканер печать очередь", Uri = "ms-settings:printers" },
        new() { Title = "Мышь", Keywords = "мышь курсор скорость прокрутка колеса", Uri = "ms-settings:mouse" },
        new() { Title = "Клавиатура", Keywords = "клавиатура залипание раскладка скорость повтора", Uri = "ms-settings:keyboard" },
        new() { Title = "Сенсорная панель", Keywords = "тачпад жесты ноутбук", Uri = "ms-settings:devices-touchpad" },
        new() { Title = "Камера", Keywords = "камера вебка доступ приложения", Uri = "ms-settings:privacy-webcam" },
        new() { Title = "Микрофон: доступ приложений", Keywords = "микрофон доступ запись приложения", Uri = "ms-settings:privacy-microphone" },

        // Система и питание
        new() { Title = "Питание и спящий режим", Keywords = "сон спящий режим гибернация экран выключается через", Uri = "ms-settings:powersleep" },
        new() { Title = "Отключить гибернацию / файл сна", Keywords = "гибернация hiberfil отключить файл сна", Uri = "control.exe powercfg.cpl" },
        new() { Title = "Батарея", Keywords = "батарея заряд экономия износа", Uri = "ms-settings:batterysaver" },
        new() { Title = "О системе", Keywords = "система версия windows процессор ram оперативка", Uri = "ms-settings:about" },
        new() { Title = "Многозадачность и прилипание окон", Keywords = "snap прилипание окон развернуть половину виртуальные столы", Uri = "ms-settings:multitasking" },
        new() { Title = "Автозагрузка программ", Keywords = "автозагрузка startup вместе с windows", Uri = "ms-settings:startupapps" },
        new() { Title = "Диспетчер задач-сосед: службы", Keywords = "службы services процессы фон", Uri = "services.msc" },

        // Хранилище
        new() { Title = "Память и контроль памяти", Keywords = "диск место очистка storage sense временные файлы", Uri = "ms-settings:storagesense" },
        new() { Title = "Установка приложений и диск по умолчанию", Keywords = "куда ставить программы диск по умолчанию установка", Uri = "ms-settings:storagepolicies" },
        new() { Title = "Управление дисками", Keywords = "разделы диск том форматировать diskmgmt", Uri = "diskmgmt.msc" },
        new() { Title = "Очистка диска (системная)", Keywords = "очистить диск мусор cleanmgr системные файлы", Uri = "cleanmgr.exe" },

        // Приложения
        new() { Title = "Удаление программ", Keywords = "удалить программу uninstall appwiz", Uri = "control.exe appwiz.cpl" },
        new() { Title = "Приложения по умолчанию", Keywords = "открыть с помощью браузер по умолчанию pdf проигрыватель", Uri = "ms-settings:defaultapps" },
        new() { Title = "Компоненты Windows (включить/выключить)", Keywords = "компоненты optional features wsl net framework", Uri = "ms-settings:optionalfeatures" },
        new() { Title = "Псевдонимы приложений", Keywords = "псевдонимы alias исполняемые файлы приложения", Uri = "ms-settings:apps-executionaliases" },

        // Приватность и обновления
        new() { Title = "Центр обновления Windows", Keywords = "обновления update апдейт перезагрузка", Uri = "ms-settings:windowsupdate" },
        new() { Title = "Приостановить обновления", Keywords = "пауза обновлений заморозить отложить update options", Uri = "ms-settings:windowsupdate-options" },
        new() { Title = "Журнал обновлений и удаление патча", Keywords = "история обновлений удалить обновление патч сломал", Uri = "ms-settings:windowsupdate-history" },
        new() { Title = "Общие параметры приватности", Keywords = "приватность слежка телеметрия реклама id", Uri = "ms-settings:privacy" },
        new() { Title = "Уведомления", Keywords = "уведомления баннеры звуки приложений фокус", Uri = "ms-settings:notifications" },
        new() { Title = "Буфер обмена и журнал", Keywords = "буфер clipboard история win+v", Uri = "ms-settings:clipboard" },

        // Персонализация и удобство
        new() { Title = "Панель задач", Keywords = "таскбар панель задач значки трей скрыть", Uri = "ms-settings:taskbar" },
        new() { Title = "Меню «Пуск»", Keywords = "пуск плитки папки меню", Uri = "ms-settings:start" },
        new() { Title = "Экран блокировки и заставка", Keywords = "заставка screensaver блокировка фон", Uri = "control.exe desk.cpl screensaver" },
        new() { Title = "Шрифты", Keywords = "шрифты установить удалить fonts", Uri = "ms-settings:fonts" },
        new() { Title = "Язык и раскладка", Keywords = "язык раскладка английский русский переключение", Uri = "ms-settings:language" },
        new() { Title = "Дата и время", Keywords = "время дата часовой пояс синхронизация", Uri = "ms-settings:dateandtime" },
        new() { Title = "Специальные возможности", Keywords = "лупа контраст озвучкаNarrator специальные возможности", Uri = "ms-settings:easeofaccess" },

        // Админ-инструменты (Windows сама спросит UAC, где нужно)
        new() { Title = "Диспетчер устройств", Keywords = "драйвер устройство откат devmgmt видеокарта", Uri = "devmgmt.msc" },
        new() { Title = "Просмотр событий", Keywords = "логи журнал ошибки события eventvwr", Uri = "eventvwr.msc" },
        new() { Title = "Брандмауэр (фаервол)", Keywords = "фаервол firewall порт заблокировать сеть", Uri = "control.exe firewall.cpl" },
        new() { Title = "Безопасность Windows", Keywords = "защитник антивирус defender изоляция", Uri = "windowsdefender:" },
        new() { Title = "Индексирование поиска", Keywords = "индекс поиск индексация медленный search", Uri = "control.exe /name Microsoft.IndexingOptions" },
        new() { Title = "Свойства системы (доп. параметры)", Keywords = "переменные среды environment path дополнительно", Uri = "SystemPropertiesAdvanced" },
        new() { Title = "Восстановление и сброс", Keywords = "сброс reset вернуть точку восстановления откат системы", Uri = "ms-settings:recovery" },
        new() { Title = "Реестр", Keywords = "реестр regedit ключи", Uri = "regedit.exe" },
    };

    /// <summary>Нечёткий поиск по названию и ключевым словам; пустой запрос = частые первыми.</summary>
    public static List<SettingsEntry> Search(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Entries.Take(limit).ToList();

        return Entries
            .Select(e => (Entry: e, Score: Math.Max(
                InMemoryClipboardHistory.FuzzyScore(query, e.Title),
                InMemoryClipboardHistory.FuzzyScore(query, e.Keywords) / 2)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .Select(x => x.Entry)
            .ToList();
    }
}
