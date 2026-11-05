# Настройки DotShell

Справочник по полям `%LOCALAPPDATA%\DotShell\settings.json`: что значит, тип и значение
по умолчанию. Файл можно править руками; большинство полей применяется на лету.

Сгенерировано из `AppSettings.cs` — при добавлении поля допиши строку и сюда.

| Настройка | Тип | По умолчанию | Что делает |
|---|---|---|---|
| `CellSize` | int | `4` | Размер ячейки точки в логических пикселях. |
| `Gap` | int | `2` | Зазор между точками. |
| `AccentHex` | string | `"#D71920"` | Основной акцентный цвет в формате #RRGGBB. Используется, если AutoAccent = false. |
| `AutoAccent` | bool | `true` | true = цвет сам подбирается из обоев. |
| `ShowGhostDots` | bool | `true` | Показывать ли "погасшие" точки сетки (фирменный вид Nothing). |
| `BackdropStyle` | string | `"dots"` | Рисунок внутри капсулы: none / waves / topo / rays / aurora / grid. Процедурный, в цвете акцента — поэтому сам подстраивается под обои. |
| `AudioDeviceId` | string | `string.Empty` | Устройство вывода для эквалайзера. Пусто — системное по умолчанию. |
| `ToastEnabled` | bool | `true` | Показывать напоминания тостами Windows (иначе только пузырь трея). |
| `UpdateCheckOnStart` | bool | `true` | Проверять обновления при старте (окно появляется, только если версия новее). |
| `UpdateCheckHours` | int | `6` | Как часто проверять обновления, часы. 0 — только при старте. |
| `WeatherFahrenheit` | bool | `—` | Погода в Фаренгейтах вместо Цельсия. |
| `QuietOnMaximized` | bool | `true` | Режим «не мешать»: когда на переднем плане развёрнутое чужое окно, остров сжимается в тонкую полоску и разворачивается по наведению. |
| `WeatherCity` | string | `"Москва"` | Город для виджета погоды. Пусто — Москва. |
| `CurrencySecond` | string | `"R01375"` | Вторая валюта в виджете курса (код ЦБ). По умолчанию юань. |
| `TrayHideHelpers` | bool | `true` | Виджет трея: прятать служебные процессы (прокси, webhelper). |
| `TrayHidden` | List<string> | `new()` | Виджет трея: какие значки человек скрыл вручную (имена процессов). |
| `TrayPinned` | List<string> | `new()` | Виджет трея: закреплённые программы (пути к exe) — видны всегда. |
| `CustomCursor` | bool | `—` | Свои курсоры в стиле точек вместо системных. |
| `CursorOriginal` | string | `string.Empty` | Пути курсоров до нашей подмены (JSON снимка) — для отката. |
| `WindowsAccentSync` | bool | `—` | Держать акцент Windows равным акценту шелла (цвет из обоев). |
| `WindowsAccentOriginal` | string | `string.Empty` | Акцент Windows до первого включения синхронизации (JSON снимка) — чтобы отключение вернуло ровно то, что было у человека. |
| `ExplorerTabs` | bool | `true` | Открывать папки вкладкой в уже открытом проводнике (Windows 11 22H2+). Выключено — как в системе: новое окно на каждую папку. |
| `SystemStyleFull` | bool | `—` | Полное оформление Windows: тёмные заголовки всех окон, а не только остров. Выключено — шелл живёт сам по себе и не трогает систему. |
| `SkippedUpdateVersion` | string | `string.Empty` | Версия обновления, про которую человек сказал «не напоминать». Пусто — напоминаем про любую новую. |
| `Skin` | string | `"nothing"` | Выбранный скин (nothing / cmf / phosphor / violet / amber). |
| `PlayerStyle` | string | `"cover"` | Раскладка плеера в острове: cover (обложка) / dots (точки) / row (строка). |
| `SeekStyle` | string | `"beads"` | Стиль полосы перемотки: beads, rail, segments, wave, sand, arc, pulse, cassette. Владелец выбирал из витрины (--slider-demo). |
| `BassVisualization` | double | `0.35` | Насколько сильно рисунок фона реагирует на бас: 0 — не реагирует, 1 — как было в первой версии (перекрывало текст). По умолчанию мягко. |
| `SliderStyle` | string | `"capsule"` | Стиль ползунков (громкость, яркость, размер точки): capsule, blocks, dots. |
| `PlayerAccentFromCover` | bool | `true` | Брать цвет заголовка трека из обложки, а не из обоев. Общий акцент остаётся от обоев — иначе интерфейс перекрашивался бы при каждой смене песни. |
| `BackdropOpacity` | int | `45` | Плотность рисунка, 0..100 %. Влияет только на рисунок, не на текст. |
| `IslandTopOffset` | int | `10` | Отступ острова от верхнего края монитора, px. |
| `IslandEdge` | string | `"top"` | Кромка, к которой прилипает остров: top или bottom. |
| `DockEnabled` | bool | `—` | Показывать док слева при старте. |
| `DesktopWidgets` | bool | `—` | Виджеты на обоях: окно уходит под все окна и не забирает фокус. Так они читаются как часть рабочего стола, а не как «ещё одно окно». |
| `IslandRadius` | int | `23` | Радиус капсулы: 0 — прямоугольник, 23 — «таблетка». |
| `CollapsedHeight` | int | `46` | Высота свёрнутой капсулы: 36 — тонкая полоска, 64 — заметная плашка. |
| `AnimationSpeed` | double | `1.0` | Множитель длительности анимаций: 0.5 — вдвое быстрее, 2 — вдвое медленнее. |
| `AnimationCurve` | string | `"out"` | Кривая движения: out, inout, linear, spring. |
| `DotShape` | string | `"circle"` | Форма точки во всей точечной графике: circle, square, diamond, dash. |
| `AccentTemperature` | int | `—` | Сдвиг оттенка акцента: −30 тёплый, +30 холодный. |
| `AccentSaturation` | int | `100` | Насыщенность акцента, 0–100 %. |
| `AccentFocus` | string | `"all"` | Где живёт акцент: all, clock, player, quiet. |
| `AccentSecondHex` | string | `""` | Второй акцент для прогресса и активных элементов. Пусто — выключен. |
| `DashboardTabs` | string | `"overview,media,perf,weather"` | Какие вкладки показывать в панели-обзоре и в каком порядке. |
| `DotTextDensity` | int | `6` | Плотность точечного текста: множитель ячейки, 5–7 «точек на глиф». |
| `DotLiveliness` | string | `"bass"` | Живость точек: off, rare, bass. |
| `NotifyMode` | string | `"capsule"` | Куда показывать уведомления: capsule, card, toast, dot. |
| `OpenDirection` | string | `"center"` | Откуда раскрывается остров: center, left, right, down. |
| `TabTransition` | string | `"fade"` | Переход между вкладками: fade, slide, curtain, none. |
| `AutoHide` | string | `"never"` | Автоскрытие: never, fullscreen, timer. |
| `QuietThreshold` | string | `"maximized"` | Порог режима «не мешать»: maximized, any, off. |
| `ClickAction` | string | `"expand"` | Что делает клик по капсуле: expand, launcher, dashboard. |
| `WidgetGrid` | int | `—` | Прилипание виджетов: 0 — выкл, иначе шаг в пикселях (8 или 16). |
| `CoverShape` | string | `"rounded"` | Форма обложки: square, rounded, circle. |
| `NotifySeconds` | int | `6` | Сколько секунд висит уведомление. |
| `NotifySound` | string | `"none"` | Звук уведомления: none, tick, system. |
| `MirrorNotifications` | bool | `true` | Показывать в острове уведомления других программ (системные тосты Windows: Telegram, Steam, Discord). Читаются через `UserNotificationListener`; если Windows запретила доступ — просто не зеркалятся. |
| `DotStagger` | bool | `—` | Шахматный сдвиг строк в точечной сетке. |
| `IslandLeft` | int | `-32000` | Последнее положение окна острова (-32000 = "не задано, центрировать"). |
| `IslandTop` | int | `-32000` | Последнее положение окна острова (-32000 = "не задано, центрировать"). |
| `HideInFullscreen` | bool | `true` | Скрывать остров, когда какое-то окно развернуто на весь экран (игры, видео). |
| `ClipboardMaxItems` | int | `500` | Сколько записей хранить в истории буфера обмена (0 = до перезагрузки, без лимита). |
| `ClipboardPersist` | bool | `false` | Хранить буфер обмена между перезагрузками. |
| `RunAtStartup` | bool | `false` | Запускать вместе с Windows. |
| `HideDesktopIcons` | bool | `false` | Прятать иконки рабочего стола (программы живут в острове и на виджетах). |
| `HotkeyHudEnabled` | bool | `false` | Показывать подсказки хоткеев активной программы при зажатых Ctrl/Alt/Win. |
| `AccentSampleIntervalMs` | int | `8000` | Как часто брать кадр рабочего стола для подбора цвета, мс. |
| `AccentSmoothing` | double | `0.25` | Насколько "лениво" цвет догоняет новый (0..1, меньше = плавнее). |
| `AiProvider` | string | `"Ollama"` | Прозрачность ЧЁРНОГО фона острова в процентах (30..100). Влияет только на подложку: акцент, текст и точки остаются плотными. // 97 вместо 92: при 92 % строки текста из окна позади просвечивали сквозь // капсулу и читались как «горизонтальные выцветшие полосы». public int IslandOpacity { get; set; } = 97; Провайдер быстрого чата на вкладке ИИ: имя из списка пресетов или "Custom". |
| `AiBaseUrl` | string | `"http://localhost:11434/v1"` | Адрес OpenAI-совместимого API (для Custom; у пресетов подставляется сам). |
| `AiModel` | string | `""` | Модель для быстрого чата. |
| `AiApiKey` | string | `string.Empty` | Ключ API для чата. Хранится локально в settings.json и уходит только на указанный адрес API. Для локальной Ollama не нужен. |

---

## Если настройка не применилась

1. Закрой остров (трей → «Выйти») — иначе он сохранит своё состояние поверх файла.
2. Правь `settings.json`, запусти `DotShell.exe`.
3. Смотри `%LOCALAPPDATA%\DotShell\crash.log`.
