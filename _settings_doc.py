import io, os, re

ROOT = r"C:\Users\maxim\Desktop\рабочая зона\dotshell"
src = os.path.join(ROOT, "src", "DotShell.Core", "Settings", "AppSettings.cs")
text = io.open(src, encoding="utf-8").read()

# Разбор: комментарий-описание + объявление свойства.
pattern = re.compile(
    r"(?:///\s*<summary>(?P<doc>.*?)</summary>\s*)?"
    r"public\s+(?P<type>[\w\?<>\.]+)\s+(?P<name>\w+)\s*\{\s*get;\s*set;\s*\}\s*(?:=\s*(?P<default>[^;]+);)?",
    re.S)

rows = []
for m in pattern.finditer(text):
    name = m.group("name")
    typ = m.group("type")
    default = (m.group("default") or "").strip()
    doc = m.group("doc") or ""
    doc = re.sub(r"\s+", " ", re.sub(r"///", "", doc)).strip()
    if not doc:
        doc = "—"
    if default == "":
        default = "—"
    rows.append((name, typ, default, doc))

header = """# Настройки DotShell

Справочник по всем полям `%LOCALAPPDATA%\\DotShell\\settings.json`: что значит, тип и
значение по умолчанию. Файл можно править руками — программа перечитает его при
следующем запуске, а большинство полей применяется на лету.

Сгенерировано из `AppSettings.cs`; если поле добавили в код — допиши и сюда.

| Настройка | Тип | По умолчанию | Что делает |
|---|---|---|---|
"""

lines = [header]
for name, typ, default, doc in rows:
    doc = doc.replace("|", "\\|")
    default = default.replace("|", "\\|")
    lines.append(f"| `{name}` | {typ} | `{default}` | {doc} |\n")

lines.append("\n---\n\n## Как проверить, что применилось\n\n"
             "1. Меняешь поле в `settings.json` (программа должна быть закрыта — иначе\n"
             "   сохранит своё состояние поверх).\n"
             "2. Запускаешь `DotShell.exe`.\n"
             "3. Если что-то не так — `%LOCALAPPDATA%\\DotShell\\crash.log`.\n")

out = os.path.join(ROOT, "docs", "SETTINGS.md")
os.makedirs(os.path.dirname(out), exist_ok=True)
io.open(out, "w", encoding="utf-8").write("".join(lines))
print("полей:", len(rows), "· файл:", out)
