# Декомпозиция D0–D1: задачи, PR-срезы, статусы

Дата: 2026-11-15  
База: `dotshell/docs/DESKTOP-TRANSITION-PLAN.md` (coexistence по умолчанию, остров — хаб), текущий код и аудиты.  
Конфигурация проверки: Windows, 3440x1440 @ ~125% DPI, 1 монитор.

**Легенда:** `[x]` выполнено/подтверждено в доступной среде, `[ ]` не выполнено. Ограничения среды помечены `blocked-by-env`.

## Итог прохода (15.11.2026): D0–D1 закрыты (в рамках доступной среды)

- **D0.1 `[x]`:** `ConfigVersion` v2, миграции + бэкап настроек, shell/desktop feature flags (безопасные дефолты: `ShellMode=island`, `DesktopHostEnabled=false`, `PerMonitorLayouts=false` и др.), per-monitor поля island/dock. Обновлён `docs/SETTINGS.md`.
- **D0.2 `[x]` (с оговоркой `blocked-by-env`):**
  - Единый monitor/DPI контекст: `MonitorHelper`, расширенный `Monitors` (`DeviceNameForWindow/Point`, `WorkAreaForDevice`, `All()`), anchor/work-area/virtual-screen helper’ы.
  - Monitor-aware геометрия/кламп/fallback: `IslandWindow.Geometry`, `DockWindow`, `WidgetWindow/WidgetsManager`, `DashboardWindow`, `ControlCenterWindow`.
  - Monitor-aware anchor-позиционирование (Manual + Loaded + clamp): `Settings`, `Launcher`, `ThemePicker`, `WallpaperSchedule`, `TextPromptWindow`, `HotkeyCaptureWindow`, `GroupWindow`, `UpdateWindow`.
  - Per-monitor save/restore layout при `PerMonitorLayouts` (с fallback на legacy/virtual screen): island (`IslandLayoutMonitor/LayoutX/LayoutY`), dock (`DockLayoutMonitor/DockLayoutY`), виджеты (`LayoutMonitor/LayoutX/LayoutY`).
  - Display-change/hot-plug устойчивость: пересоздание/обновление конфигурации host-слоя, clamp/save виджетов, fallback-позиционирование поверхностей.
  - **Ограничение:** полная multi-monitor/mixed-DPI/hot-plug матрица не подтверждена (`blocked-by-env`).
- **D0.3 `[x]` (локально):** `scripts/audit-all.ps1` (build→smoke→ui-audit→widget-audit→dock-audit, логи/`summary.txt`); усилены `ui-audit.ps1` и `widget-audit.ps1` (таймауты/polling). CI workflow (`.github/workflows/smoke.yml`) совместим с текущими скриптами/сборкой.
- **D0.4 `[x]`:** `DesktopHostWindow` — per-monitor host-слой под `DesktopHostEnabled` (default off): без фокуса/ввода, `TOOLWINDOW|NOACTIVATE`, display/DPI реакции, periodic reassert, best-effort Progman/WorkerW attach (`0x052C` + `SetParent`) + fallback `HWND_BOTTOM`, пересоздание набора при изменении конфигурации. Синхронизация из `IslandWindow` на старте и при `Settings.Changed`. Win32: `SendMessageTimeout/SetParent/GetParent`.
- **D1.1 `[x]`:** `widgets.json` v2 (`{version,items}`) + чтение legacy v1 и нормализация, `WidgetSpec.LayoutMonitor/LayoutX/LayoutY`, `WidgetStore.UpdateLayout/Snapshot/ReplaceAll`. Профили/наборы раскладок: `Core/Widgets/WidgetLayoutProfiles.cs` (`widget-layout-profiles.json`), `WidgetsManager.SaveLayoutProfile/ApplyLayoutProfile/ListLayoutProfiles/DeleteLayoutProfile`, CLI `--save-widget-layout=`, `--load-widget-layout=`, `--list-widget-layouts`, `--delete-widget-layout=`.
- **D1.2 `[x]`:** desktop-layer хостинг/контракт: `DesktopHostWindow.ReassertBottom()`, `WidgetWindow` desktop-mode при `DesktopWidgets || DesktopHostEnabled` (no-activate, нижний слой, согласованный порядок host→widget), динамический `ApplyDesktopMode()`, `WidgetsManager` как единый layout/управляющий контур (open/restore/move/resize/save, per-monitor restore, display-change clamp/save, apply desktop mode).
- **D1.3 `[x]` (в рамках доступной среды):** desktop-host прозрачен и не конфликтует с глобальным применением темы/акцента (`ThemeBus`) и обоев/расписания (`WallpaperScheduleService`); виджеты используют общие ресурсы темы. Регрессионное покрытие зелёное (`audit-all`). Специфичные multi-monitor/desktop-host сценарии сверх текущей матрицы — `blocked-by-env`.

## Проверки (контрольные прогоны)

- **`dotnet build` (Debug, x64):** 0 ошибок / 0 предупреждений.
- **`scripts/audit-all.ps1` — PASS:**
  - build PASS, smoke PASS (v1.14.0, selftest/version-probe, без FATAL)
  - ui-audit -Quick PASS (5/5)
  - widget-audit -Quick PASS (5/5)
  - dock-audit PASS
- **Артефакты:** `%LOCALAPPDATA%\DotShell\audit-artifacts\20261115-211433` (финальный), также `20261115-194059`, `20261115-193310`, `20261115-190121`.
- **Матрица дисплеев/DPI:** `docs/MULTIMONITOR-DPI-CHECKLIST.md` (`M1-125` verified локально/ограниченно; остальные `blocked-by-env`).

## Ключевые файлы

- Core: `Core/Settings/AppSettings.cs`, `Core/Settings/SettingsStore.cs`, `Core/Widgets/WidgetStore.cs`, `Core/Widgets/WidgetLayoutProfiles.cs`, `Core/Windowing/Monitors.cs`, `Core/Native/Win32.cs`
- WPF: `IslandWindow.Geometry.cs`, `IslandWindow.xaml.cs`, `MonitorHelper.cs`, `DesktopHostWindow.cs`, `DockWindow.cs`, `Widgets/WidgetWindow.cs`, `Widgets/WidgetsManager.cs`, `DashboardWindow.xaml.cs`, `ControlCenterWindow.cs`, `SettingsWindow.xaml(.cs)`, `LauncherWindow.xaml(.cs)`, `ThemePickerWindow.cs`, `WallpaperScheduleWindow.cs`, `TextPromptWindow.cs`, `HotkeyCaptureWindow.cs`, `GroupWindow.cs`, `UpdateWindow.cs`, `Program.cs`
- Scripts/Docs: `scripts/audit-all.ps1`, `scripts/ui-audit.ps1`, `scripts/widget-audit.ps1`, `docs/SETTINGS.md`, `docs/DESKTOP-TRANSITION-PLAN.md`, `docs/DESKTOP-D0-D1-TASKS.md`, `docs/MULTIMONITOR-DPI-CHECKLIST.md`

## Ограничения и внешние действия

- **`blocked-by-env`:** полная multi-monitor/mixed-DPI/hot-plug валидация требует стенда с несколькими мониторами/масштабами.
- **Релиз/публикация:** рабочее дерево не является git-репозиторием в текущем корне (`рабочая зона`), коммиты/пуш/GitHub Release не выполнялись. Локальные проверки зелёные; публикация v1.14.0 требует репозитория/remote, синхронизации с `main` и токена для `scripts/release.ps1`.
- Дальнейшие продуктовые итерации (D2+ по плану: shell bar/start/window manager/уведомления и т.д.) — вне закрытого D0–D1 среза.
