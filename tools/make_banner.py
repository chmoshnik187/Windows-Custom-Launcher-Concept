"""Генератор точечных баннеров для README.

Зачем: баннер-логотип проекта сделан точками, и он выглядит как часть продукта.
Но такой баннер — не только логотип: тем же приёмом можно нарисовать заголовок
раздела или версию, и страница станет читаться как один стиль, а не как текст
с картинками.

Как это работает (тот же конвейер, что в шелле):
  1. текст рисуется в маску шрифтом;
  2. маска нарезается сеткой «ячейка + зазор»;
  3. в ячейке считается яркость, по ней решается «точка есть / нет»;
  4. точки рисуются кружками: часть белые, часть — акцентные.

Запуск:
    python tools/make_banner.py "DOT SHELL" images/banner-x.png --cell 4 --gap 2
    python tools/make_banner.py "MIT" images/banner-license.png --size 120 --accent 0.35
"""

import argparse
import os
import random

from PIL import Image, ImageDraw, ImageFont

BACKGROUND = (11, 11, 14)
INK = (236, 236, 238)
ACCENT = (0xD7, 0x19, 0x20)

FONTS = [
    r"C:\Windows\Fonts\consolab.ttf",
    r"C:\Windows\Fonts\consola.ttf",
    r"C:\Windows\Fonts\bahnschrift.ttf",
]


def pick_font(size):
    for path in FONTS:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit("не нашёл ни одного шрифта из списка")


def render(text, size, cell, gap, accent_share, seed):
    font = pick_font(size)

    # 1. маска текста
    probe = ImageDraw.Draw(Image.new("L", (1, 1)))
    left, top, right, bottom = probe.textbbox((0, 0), text, font=font)
    text_w, text_h = right - left, bottom - top
    if text_w < 1 or text_h < 1:
        raise SystemExit("пустой текст")

    pad = size // 3
    mask = Image.new("L", (text_w + pad * 2, text_h + pad * 2), 0)
    ImageDraw.Draw(mask).text((pad - left, pad - top), text, font=font, fill=255)

    # 2. сетка
    pitch = cell + gap
    cols = mask.width // pitch + 1
    rows = mask.height // pitch + 1

    out_w, out_h = cols * pitch, rows * pitch
    image = Image.new("RGB", (out_w, out_h), BACKGROUND)
    draw = ImageDraw.Draw(image)

    rnd = random.Random(seed)

    # 3-4. яркость ячейки → точка
    for row in range(rows):
        for col in range(cols):
            box = (col * pitch, row * pitch,
                   min(col * pitch + cell, mask.width),
                   min(row * pitch + cell, mask.height))
            if box[2] <= box[0] or box[3] <= box[1]:
                continue

            patch = mask.crop(box)
            # средняя яркость как доля закрашенных пикселей
            values = patch.tobytes()
            level = sum(values) / (255.0 * len(values))
            if level < 0.12:
                continue

            radius = cell / 2.0
            cx, cy = col * pitch + cell / 2.0, row * pitch + cell / 2.0

            color = ACCENT if rnd.random() < accent_share else INK
            draw.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=color)

    return image


def main():
    parser = argparse.ArgumentParser(description="Точечный баннер из текста")
    parser.add_argument("text")
    parser.add_argument("output")
    parser.add_argument("--size", type=int, default=96, help="кегль исходного текста")
    parser.add_argument("--cell", type=int, default=4, help="размер точки, px")
    parser.add_argument("--gap", type=int, default=2, help="зазор между точками, px")
    parser.add_argument("--accent", type=float, default=0.12, help="доля акцентных точек 0..1")
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    image = render(args.text, args.size, args.cell, args.gap, args.accent, args.seed)
    os.makedirs(os.path.dirname(os.path.abspath(args.output)), exist_ok=True)
    image.save(args.output)
    print(f"{args.output}: {image.width}x{image.height}")


if __name__ == "__main__":
    main()
