#!/usr/bin/env python3
"""Generate the small, seamless retro material studies used by CourtyardMaterials.

The source intentionally makes clustered grain rather than masonry courses:
the new Engine surface composition owns course spacing, profiles, and breaks.
"""

from __future__ import annotations

import hashlib
import struct
import zlib
from pathlib import Path


SIZE = 32
OUTPUT = Path(__file__).resolve().parents[1] / "content" / "textures" / "courtyard"

# Four-to-six deliberately close tones per material. The silhouette and the
# named material choice should carry more contrast than any individual tile.
TEXTURES: dict[str, tuple[int, tuple[tuple[int, int, int], ...]]] = {
    "courtyard-stone.png": (0x19A4_1E71, ((91, 84, 78), (99, 92, 85), (106, 98, 90), (83, 77, 73), (113, 104, 95))),
    "courtyard-plaster.png": (0x2B7C_6053, ((173, 156, 124), (181, 164, 132), (164, 148, 118), (188, 171, 138), (154, 140, 113))),
    "courtyard-ground.png": (0x3D52_A8C9, ((91, 67, 46), (100, 74, 51), (82, 60, 42), (108, 80, 54), (74, 54, 39))),
    "courtyard-moss.png": (0x4E91_27D5, ((62, 86, 53), (69, 94, 58), (55, 78, 48), (75, 99, 62), (50, 71, 45))),
    "courtyard-wood.png": (0x5FC3_489B, ((72, 46, 31), (80, 52, 35), (65, 41, 29), (88, 57, 38), (59, 38, 27))),
}


def mix(value: int) -> int:
    value ^= value >> 16
    value = (value * 0x7FEB352D) & 0xFFFFFFFF
    value ^= value >> 15
    value = (value * 0x846CA68B) & 0xFFFFFFFF
    return value ^ (value >> 16)


def random(seed: int, index: int) -> int:
    return mix(seed ^ (index * 0x9E3779B9))


def tile(seed: int, palette: tuple[tuple[int, int, int], ...]) -> list[list[tuple[int, int, int]]]:
    pixels = [[palette[0] for _ in range(SIZE)] for _ in range(SIZE)]

    # Every island contains exactly 2–4 pixels and wraps across the tile
    # border. This keeps quiet repeatable grain without encoding a brick grid.
    patch_shapes = (
        ((0, 0), (1, 0)),
        ((0, 0), (1, 0), (0, 1)),
        ((0, 0), (1, 0), (1, 1)),
        ((0, 0), (1, 0), (0, 1), (1, 1)),
        ((0, 0), (1, 0), (1, 1), (2, 1)),
    )
    for patch in range(28):
        value = random(seed, patch)
        center_x = value & 31
        center_y = (value >> 5) & 31
        color = palette[1 + ((value >> 12) % (len(palette) - 1))]
        shape = patch_shapes[(value >> 16) % len(patch_shapes)]
        flip_x = -1 if value & (1 << 20) else 1
        flip_y = -1 if value & (1 << 21) else 1
        for offset_x, offset_y in shape:
            pixels[(center_y + (offset_y * flip_y)) % SIZE][(center_x + (offset_x * flip_x)) % SIZE] = color

    # A few isolated pixels prevent visibly uniform islands while remaining
    # well below the contrast that would read as photographic noise.
    for speck in range(18):
        value = random(seed ^ 0xA5A5_A5A5, speck)
        pixels[(value >> 5) & 31][value & 31] = palette[1 + ((value >> 12) % (len(palette) - 1))]

    return pixels


def png_bytes(pixels: list[list[tuple[int, int, int]]]) -> bytes:
    raw = bytearray()
    for row in pixels:
        raw.append(0)  # PNG filter type: None
        for red, green, blue in row:
            raw.extend((red, green, blue, 255))

    def chunk(kind: bytes, payload: bytes) -> bytes:
        return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload) & 0xFFFFFFFF)

    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", SIZE, SIZE, 8, 6, 0, 0, 0)) + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b"")


def main() -> None:
    OUTPUT.mkdir(parents=True, exist_ok=True)
    for filename, (seed, palette) in TEXTURES.items():
        target = OUTPUT / filename
        target.write_bytes(png_bytes(tile(seed, palette)))
        print(f"{target.relative_to(OUTPUT.parents[2])} sha256={hashlib.sha256(target.read_bytes()).hexdigest()}")


if __name__ == "__main__":
    main()
