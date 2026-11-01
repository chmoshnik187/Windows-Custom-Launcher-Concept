#!/usr/bin/env python3
"""
make_icon.py — нарисовать иконку DotShell (сетка точек) и сохранить как .ico.

Никаких зависимостей: .ico с встроенным 32-битным BMP пишется вручную.
Формат простой: заголовок ICONDIR + ICONDIRENTRY + BITMAPINFOHEADER + пиксели BGRA + AND-маска.

Запуск:
    python tools/make_icon.py src/DotShell.Island/Assets/dotshell.ico
"""

from __future__ import annotations

import math
import struct
import sys
from pathlib import Path

SIZES = [16, 32, 48, 256]
ACCENT = (0xD7, 0x19, 0x20)   # красный Nothing
WHITE = (0xEC, 0xEC, 0xEE)


def render(size: int) -> bytes:
    """
    Рисуем сетку точек 5x5: центральная и угловые — акцентные, остальные белые.
    Возвращает пиксели BGRA, строки СВЕРХУ ВНИЗ (как требует BMP внутри ICO).
    """
    grid = 5
    pitch = size / grid
    radius = pitch * 0.30
    center = size / 2.0

    pixels = bytearray()
    # BMP хранит строки снизу вверх, поэтому идём по y от size-1 к 0.
    for y in range(size - 1, -1, -1):
        for x in range(size):
            # координаты центра ячейки, в которую попал пиксель
            cx = (int(x / pitch) + 0.5) * pitch
            cy = (int(y / pitch) + 0.5) * pitch
            dist = math.hypot(x + 0.5 - cx, y + 0.5 - cy)

            alpha = max(0.0, min(1.0, radius - dist + 0.5))
            if alpha <= 0.004:
                pixels += bytes((0, 0, 0, 0))
                continue

            col = int(x / pitch)
            row = int(y / pitch)
            is_accent = (row, col) in {(2, 2), (0, 0), (0, 4), (4, 0), (4, 4)}
            r, g, b = ACCENT if is_accent else WHITE

            a = int(alpha * 255)
            # предварительное умножение альфы (premultiplied), как ждёт Windows
            pixels += bytes((b * a // 255, g * a // 255, r * a // 255, a))

    return bytes(pixels)


def make_ico(path: Path) -> None:
    images = []
    for size in SIZES:
        pixels = render(size)
        # AND-маска: 1 бит на пиксель, строки выровнены по 4 байта, все нули (альфа уже есть).
        row_bytes = ((size + 31) // 32) * 4
        mask = bytes(row_bytes * size)

        header = struct.pack(
            "<IiiHHIIiiII",
            40,          # размер BITMAPINFOHEADER
            size,        # ширина
            size * 2,    # высота (XOR + AND маска)
            1,           # плоскости
            32,          # бит на пиксель
            0,           # сжатие (BI_RGB)
            len(pixels) + len(mask),
            0, 0, 0, 0
        )
        images.append((size, header + pixels + mask))

    out = bytearray()
    out += struct.pack("<HHH", 0, 1, len(images))  # ICONDIR: reserved, type=1 (иконка), количество

    offset = 6 + 16 * len(images)
    for size, data in images:
        w = 0 if size >= 256 else size
        h = 0 if size >= 256 else size
        out += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(data), offset)
        offset += len(data)

    for _, data in images:
        out += data

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(bytes(out))
    print(f"готово: {path} ({len(out)} байт, размеры {', '.join(map(str, SIZES))})")


if __name__ == "__main__":
    target = Path(sys.argv[1] if len(sys.argv) > 1 else "dotshell.ico")
    make_ico(target)
