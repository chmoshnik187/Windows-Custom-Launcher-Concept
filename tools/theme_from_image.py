#!/usr/bin/env python3
"""
theme_from_image.py — сделать тему DotShell из любой картинки.

Зачем этот скрипт на Python, если программа на C#:
это твой "песочница". Тот же алгоритм подбора цвета, что работает внутри DotShell,
только в 80 строках, которые можно запустить, изменить и посмотреть результат
за две секунды. Потом переносишь удачную идею в C#.

Использование:
    python theme_from_image.py обои.png
    python theme_from_image.py обои.jpg -o themes/my-theme.json

Нужен Pillow:
    pip install pillow
"""

from __future__ import annotations

import argparse
import colorsys
import json
import struct
import zlib
from collections import Counter
from pathlib import Path

try:
    from PIL import Image
    _HAS_PIL = True
except ImportError:          # скрипт обязан работать и без Pillow — для PNG есть свой декодер
    Image = None
    _HAS_PIL = False


# ---------------------------------------------------------------------------
# Загрузка картинки.
# Pillow нужен только для jpg/webp. PNG читаем сами: формат простой,
# а зависимость в 3 МБ ради одного теста — лишняя.
# ---------------------------------------------------------------------------

def load_pixels(path: Path) -> tuple[int, int, list[tuple[int, int, int]]]:
    if _HAS_PIL:
        img = Image.open(path).convert("RGB").resize((64, 64), Image.BILINEAR)
        return img.width, img.height, list(img.getdata())
    if path.suffix.lower() != ".png":
        raise SystemExit("Без Pillow поддерживается только PNG. Поставь Pillow: pip install pillow")
    return _decode_png(path)


def _decode_png(path: Path, target: int = 64) -> tuple[int, int, list[tuple[int, int, int]]]:
    data = path.read_bytes()
    if not data.startswith(b"\x89PNG\r\n\x1a\n"):
        raise SystemExit("Это не PNG")

    pos = 8
    width = height = bit_depth = color_type = 0
    idat = bytearray()
    palette: list[tuple[int, int, int]] = []

    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length

        if kind == b"IHDR":
            width, height, bit_depth, color_type = struct.unpack(">IIBB", body[:10])
        elif kind == b"PLTE":
            palette = [tuple(body[i:i + 3]) for i in range(0, len(body), 3)]
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            break

    if bit_depth != 8 or color_type not in (0, 2, 3, 6):
        raise SystemExit(f"Неподдерживаемый PNG (depth={bit_depth}, type={color_type})")

    channels = {0: 1, 2: 3, 3: 1, 6: 4}[color_type]
    stride = width * channels
    raw = zlib.decompress(bytes(idat))

    # Обратная операция PNG-фильтрации по строкам.
    out = bytearray(height * stride)
    prev = bytearray(stride)
    p = 0
    for y in range(height):
        filt = raw[p]
        line = bytearray(raw[p + 1:p + 1 + stride])
        p += 1 + stride
        for x in range(stride):
            a = line[x - channels] if x >= channels else 0
            b = prev[x]
            c = prev[x - channels] if x >= channels else 0
            if filt == 1:
                line[x] = (line[x] + a) & 0xFF
            elif filt == 2:
                line[x] = (line[x] + b) & 0xFF
            elif filt == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 0xFF
            elif filt == 4:
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 0xFF
        out[y * stride:(y + 1) * stride] = line
        prev = line

    # Приводим к RGB и сразу уменьшаем до target×target (ближайший сосед).
    step_x = max(1, width // target)
    step_y = max(1, height // target)
    cols = len(range(0, width, step_x))
    rows = len(range(0, height, step_y))

    pixels: list[tuple[int, int, int]] = []
    for y in range(0, height, step_y):
        for x in range(0, width, step_x):
            i = y * stride + x * channels
            if color_type in (2, 6):
                pixels.append((out[i], out[i + 1], out[i + 2]))
            elif color_type == 0:
                pixels.append((out[i], out[i], out[i]))
            else:  # палитра
                pixels.append(palette[out[i]])
    return cols, rows, pixels


def dominant_accent(pixels: list[tuple[int, int, int]]) -> tuple[tuple[int, int, int], float]:
    """
    Возвращает (RGB, уверенность).

    Алгоритм — ровно тот же, что в DotShell.Core/Theming/ColorExtractor.cs:
      1. детали не важны, картинка уже уменьшена;
      2. выбрасываем серое/чёрное/белое — из них акцент не получается;
      3. раскладываем по корзинам оттенка шагом 10 градусов;
      4. побеждает корзина с наибольшим весом = количество * средняя насыщенность;
      5. доводим победителя: насыщеннее и не слишком тёмный.
    """
    buckets: dict[int, list[float]] = {}
    considered = 0

    for r, g, b in pixels:
        h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
        if s < 0.16 or v < 0.10 or v > 0.97:
            continue
        considered += 1
        key = int(h * 360 // 10) % 36
        bucket = buckets.setdefault(key, [0.0, 0.0, 0.0])
        bucket[0] += 1
        bucket[1] += s
        bucket[2] += r * 65536 + g * 256 + b

    if not buckets:
        common = Counter(pixels).most_common(1)[0][0]
        return common, 0.05

    best_key = max(buckets, key=lambda k: buckets[k][0] * (buckets[k][1] / buckets[k][0]))
    count, sum_s, packed = buckets[best_key]

    avg = packed / count
    r = int(avg // 65536) % 256
    g = int(avg // 256) % 256
    b = int(avg) % 256

    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    s = min(max(s * 1.25, 0.35), 0.95)
    v = min(max(v, 0.55), 0.98)
    r, g, b = colorsys.hsv_to_rgb(h, s, v)

    confidence = min(considered / 400.0, 1.0)
    return (int(r * 255), int(g * 255), int(b * 255)), confidence


def build_palette(pixels: list[tuple[int, int, int]], size: int = 5) -> list[str]:
    """Пять самых частых цветов — пригодятся для вторичных элементов темы."""
    counts = Counter(pixels)
    return ["#%02X%02X%02X" % color for color, _ in counts.most_common(size)]


def luminance(hex_color: str) -> float:
    hex_color = hex_color.lstrip("#")
    r, g, b = (int(hex_color[i:i + 2], 16) for i in (0, 2, 4))
    return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255


def build_theme(image_path: Path, name: str) -> dict:
    _, _, pixels = load_pixels(image_path)
    (r, g, b), confidence = dominant_accent(pixels)
    accent = "#%02X%02X%02X" % (r, g, b)
    colors = build_palette(pixels)

    # Если обои тёмные — поверхность делаем чуть темнее, если светлые — чуть светлее.
    # Это та самая "сборка всего под обои", ради которой авто-цвет и затевался.
    base_dark = luminance(accent) < 0.55

    return {
        "name": name,
        "author": "theme_from_image.py",
        "version": 1,
        "colors": {
            "accent": accent,
            "background": "#000000" if base_dark else "#F5F5F6",
            "surface": "#121214" if base_dark else "#FFFFFF",
            "surface_alt": "#17171A" if base_dark else "#F0F0F2",
            "border": "#26262B" if base_dark else "#DCDCE0",
            "text": "#ECECEE" if base_dark else "#141416",
            "text_muted": "#8A8A93" if base_dark else "#6C6C74",
            "ghost_dot": "#14FFFFFF" if base_dark else "#14000000",
        },
        "dot": {"cell": 4, "gap": 2, "fill": 0.86, "min_radius": 0.16, "threshold": 0.07},
        "motion": {"island_expand_ms": 420, "easing": "cubic-bezier(0.2, 0.9, 0.2, 1)", "panel_fade_ms": 220},
        "widgets": {
            "collapsed": ["clock", "media", "cpu", "battery"],
            "tabs": ["notes", "clipboard", "eq", "system", "theme"],
            "launcher_hotkey": "Ctrl+Space",
            "clipboard_hotkey": "Ctrl+Shift+V",
        },
        "_meta": {
            "source_image": str(image_path),
            "confidence": round(confidence, 3),
            "palette": colors,
        },
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Сделать тему DotShell из картинки")
    parser.add_argument("image", type=Path, help="путь к обоям (png/jpg/webp)")
    parser.add_argument("-o", "--output", type=Path, help="куда сохранить theme.json")
    parser.add_argument("-n", "--name", help="имя темы (по умолчанию — имя файла)")
    args = parser.parse_args()

    if not args.image.exists():
        raise SystemExit(f"Файл не найден: {args.image}")

    name = args.name or args.image.stem.replace("_", " ").replace("-", " ").title()
    theme = build_theme(args.image, name)

    output = args.output or Path(f"theme-{args.image.stem}.json")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(theme, indent=2, ensure_ascii=False), encoding="utf-8")

    accent = theme["colors"]["accent"]
    confidence = theme["_meta"]["confidence"]
    print(f"акцент      : {accent}  (уверенность {confidence:.2f})")
    print(f"палитра     : {' '.join(theme['_meta']['palette'])}")
    print(f"сохранено   : {output}")
    if confidence < 0.15:
        print("! картинка почти однотонная/серая — акцент подобран слабо, лучше задать вручную")


if __name__ == "__main__":
    main()
