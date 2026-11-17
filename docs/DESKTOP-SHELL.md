# Десктоп в духе Familiar Desktop и Liquid Desktop

> Что именно мы разобрали «чистой комнатой», какие механики взяли и что из этого
> уже реализовано в DotShell. Это не про копирование картинки, а про то, **как
> оно работает**.

## Familiar Desktop (Omarchy, Linux)

- Это плагин Omarchy (Arch + Hyprland), а не программа для Windows; автор
  Tom Ballard, проект закрыт 13.10.2026 после переноса идей в ядро Omarchy.
- Архитектура: Rust-бэкенд (оконные действия, заголовки, темы) + QML-интерфейс,
  общение с композитором через Hyprland IPC, настройки в JSON, установка готовых
  бинарников с проверкой контрольных сумм, обратимые правки конфигов с журналом
  восстановления.
- Функции: док (пины, индикаторы, бейджи, контекстное меню окон: go to / bring
  here / new window / pin / minimize / close, прокрутка колёсиком для перебора
  окон), оконные кнопки, рабочие пространства, жесты, настройка «Windows mode»
  (полноширинная панель задач).

**Что взято в DotShell:** док с действиями окон (показать/свернуть/закрыть/новое
окно), «Окна (N)» со списком окон программы и переключением кликом, колесо по
иконке — перебор окон, кликабельные цифры рабочих столов, автоскрытие/peek дока.

## Liquid Desktop / LiquidDesk

- LiquidDesk (LIG) — Windows-бьютификатор: Python/PySide6-оболочка, десктопные
  плитки и виджеты в отдельном перезапускаемом процессе; «стекло» — GPU-захват
  экрана → blur/tint/refraction с шестью уровнями и fallback; иконки через
  `.lnk`/Shell Icons/HKCU с манифестом и откатом; своя панель задач с watchdog.
- PrismGlass Desktop (MIT) — WinForms + WebView2/WebGL: SDF-бевел, преломление
  по RGB (IOR 1.46), Fresnel, rim, grain; кадры WGC → shared memory → WebGL;
  контур панели из шейдера возвращается в натив для клипа и hit-test.
- macOS Liquid Desktop — Metal: симуляция воды в ping-pong текстурах, отдельный
  слой между обоями и иконками, глобальный монитор мыши, hit-test по окнам.

**Что взято в DotShell:**

- «Стеклянная панель» (`IslandGlass`, по умолчанию выкл.) — обои из файла
  (`HKCU\Control Panel\Desktop\WallPaper`) + мягкий blur и притемнение; без
  постоянного захвата экрана, при сплошном цвете/Spotlight — плоский фон;
- «Полка-большая папка» — 3×3 иконок, «+N», FileSystemWatcher, память позиции,
  контекстное меню, системные иконки shell;
- рябь точками по обоям при клике на пустой рабочий стол (окно живёт ~900 мс,
  клики проходят сквозь него);
- док/остров с живыми окнами и виртуальными столами.

## Что осталось за кадром (осознанно)

- «Под-иконная» рябь на 24H2+ требует отдельного D3D11 + DirectComposition
  helper — WPF так не умеет; текущая рябь рисуется над обоями и пропускает клики.
- Постоянное screen-capture стекло (как у LiquidDesk) не делаем: приватность и
  нагрузка; обои из файла дают 90% вида без этого.
- `.lgpack`-иконки и импорт `.hwt/.mtz/.apk` — следующий этап: пайплайн описан в
  `research/liquid-desktop/TECH-WPF-FEASIBILITY.md`.
- Оконные кнопки поверх чужих заголовков (Familiar) — опциональны и хрупче
  всего; отложены, чтобы не трогать чужие окна без спроса.

## Источники

- Familiar Desktop — https://github.com/tcballard/omarchy-plugin-familiar-desktop
- LiquidDesk — https://ligdesign.win/liquiddesk
- PrismGlass Desktop — https://github.com/… (MIT, `src/LiquidDesktop/`)
- macOS Liquid Desktop — https://apps.apple.com/app/id594236646
- Deskband11Lib — https://github.com/airtaxi/Deskband11Lib
- Полные заметки: `research/familiar-desktop/RESEARCH.md`,
  `familiar-desktop-architecture-study.md`,
  `research/liquid-desktop/TECH-WPF-FEASIBILITY.md`,
  `windows-shell-mechanics-research.md`.
