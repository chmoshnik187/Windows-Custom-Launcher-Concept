#!/usr/bin/env python3
"""Генерирует docs/SETTINGS.md из src/DotShell.Core/Settings/AppSettings.cs.

Зачем: справочник настроек должен совпадать с кодом. Ручная правка дрейфует,
поэтому таблица полей собирается из имён, типов, значений по умолчанию и
XML-комментариев <summary>.

Запуск:
  python tools/sync_settings_docs.py          # перезаписать docs/SETTINGS.md
  python tools/sync_settings_docs.py --check  # только проверить, код 1 если отличается
"""

from __future__ import annotations

import argparse
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
APPS = ROOT / "src/DotShell.Core/Settings/AppSettings.cs"
OUT = ROOT / "docs/SETTINGS.md"


def normalize_summary(lines: list[str]) -> str:
    text = " ".join(s.strip() for s in lines if s.strip())
    text = re.sub(r"\s+", " ", text).strip()
    # На случай, если теги случайно попали в собранные строки.
    text = re.sub(r"</?summary>", "", text, flags=re.IGNORECASE)
    text = re.sub(r"\s+", " ", text).strip()
    # В markdown-таблице нельзя оставлять голые переводы строк и пайпы.
    text = text.replace("|", "\\|")
    return text


def normalize_default(expr: str) -> str:
    expr = expr.strip().rstrip(";")
    # Убираем лишние квалификаторы/кавычки для читаемости, но не ломаем смысл.
    expr = expr.replace("System.Text.Json.JsonSerializerOptions", "JsonSerializerOptions")
    expr = expr.replace("Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)", "%LOCALAPPDATA%")
    # Булевы false без инициализатора в C# — но у нас все bool с явным значением или без;
    # свойство без '= ...' попадёт сюда пустым, обработаем отдельно.
    return expr


def parse_properties(text: str):
    props = []
    lines = text.splitlines()
    i = 0
    summary: list[str] = []
    in_summary = False
    prop_re = re.compile(r"^\s*public\s+(?!static|sealed|class)([\w<>\[\],\? ]+?)\s+(\w+)\s*\{\s*get;\s*set;\s*\}(?:\s*=\s*(.*?);)?\s*$")

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        # Однострочный summary: /// <summary>текст</summary>
        msum_one = re.match(r"^\s*///\s*<summary>\s*(.*?)\s*</summary>\s*$", line)
        if msum_one:
            summary = [msum_one.group(1)]
            i += 1
            continue

        msum = re.match(r"^\s*///\s*<summary>\s*$", line)
        if msum:
            in_summary = True
            summary = []
            i += 1
            continue
        if in_summary:
            mend = re.match(r"^\s*///\s*</summary>\s*$", line)
            if mend:
                in_summary = False
                i += 1
                continue
            mline = re.match(r"^\s*///\s?(.*)$", line)
            if mline:
                summary.append(mline.group(1))
            i += 1
            continue

        # Игнорируем [JsonIgnore] и другие атрибуты — summary уже собран выше.
        if stripped.startswith("["):
            i += 1
            continue

        m = prop_re.match(line)
        if m:
            ctype = m.group(1).strip()
            name = m.group(2).strip()
            default = m.group(3)
            if default is None:
                # bool/int без инициализатора -> false/0; для bool по умолчанию false.
                default = "false" if ctype == "bool" else "—"
            else:
                default = normalize_default(default)
            # target-typed new() -> укажем тип явно для читаемости в доке.
            if default == "new()":
                default = f"new {ctype}()"
            if default.startswith("new List"):
                default = default.replace(" ", "")
                default = default.replace("newList", "new List")
            props.append({
                "name": name,
                "type": ctype,
                "default": default,
                "summary": normalize_summary(summary),
            })
            summary = []
            i += 1
            continue

        # Объявления типов/методов сбрасывают "висящий" summary (например, summary
        # самого класса не должен прилипнуть к первому свойству).
        if re.match(r"^\s*(public|internal|protected|private)?\s*(sealed\s+|static\s+|partial\s+|virtual\s+|override\s+)*(class|record|struct|interface|enum|namespace)\b", line):
            summary = []
            i += 1
            continue
        # Статические методы/прочие члены с сигнатурой тоже не должны наследовать summary свойства.
        if re.match(r"^\s*public\s+static\b", line):
            summary = []
            i += 1
            continue

        # Пустые строки между summary и свойством допустимы — не сбрасываем.
        if stripped == "" or stripped.startswith("//"):
            i += 1
            continue

        # Строки /// вне блока <summary> здесь быть не должны (блок обработан выше),
        # но на всякий случай не даём им ломать состояние.
        if stripped.startswith("///"):
            i += 1
            continue

        # Любая другая значимая строка — сброс висящего summary.
        summary = []
        i += 1

    return props


def render(props):
    lines = []
    lines.append("# Настройки DotShell")
    lines.append("")
    lines.append("Справочник по полям `%LOCALAPPDATA%\\DotShell\\settings.json`: что значит, тип и значение")
    lines.append("по умолчанию. Файл можно править руками; большинство полей применяется на лету.")
    lines.append("")
    lines.append("> Сгенерировано из `AppSettings.cs` (`python tools/sync_settings_docs.py`).")
    lines.append("> При добавлении поля запусти генератор — ручная правка таблицы перетрётся.")
    lines.append("")
    lines.append("| Настройка | Тип | По умолчанию | Что делает |")
    lines.append("|---|---|---|---|")
    for p in props:
        lines.append(f"| `{p['name']}` | `{p['type']}` | `{p['default']}` | {p['summary']} |")
    lines.append("")
    lines.append("---")
    lines.append("")
    lines.append("## Если настройка не применилась")
    lines.append("")
    lines.append("1. Закрой остров (трей → «Выйти») — иначе он сохранит своё состояние поверх файла.")
    lines.append("2. Правь `settings.json`, запусти `DotShell.exe`.")
    lines.append("3. Смотри `%LOCALAPPDATA%\\DotShell\\crash.log`.")
    lines.append("")
    return "\n".join(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--check", action="store_true", help="не писать, только проверить актуальность")
    args = ap.parse_args()

    src = APPS.read_text(encoding="utf-8")
    props = parse_properties(src)
    if not props:
        print("не найдено ни одного свойства", file=sys.stderr)
        return 2

    content = render(props)

    if args.check:
        if not OUT.exists():
            print(f"нет файла: {OUT}")
            return 1
        current = OUT.read_text(encoding="utf-8")
        if current == content:
            print(f"ok: {OUT.name} актуален ({len(props)} полей)")
            return 0
        print(f"drift: {OUT.name} отличается от AppSettings ({len(props)} полей)")
        return 1

    OUT.write_text(content, encoding="utf-8", newline="\n")
    print(f"{OUT}: {len(props)} полей")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
