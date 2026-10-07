"""Deterministic grass blades for CraftSurvive's scattered grass clumps (#9546, #9668): grass-blades.png, blades
rising from the bottom edge of a transparent card, tapering to a point, darker at the root and lighter toward
the tip. Colours come from the palette's meadow ramp (content/style/palette.json) so the cards sit in the painted
meadow ground: each blade picks its own root and tip shades, and a few are dry ochre seed stalks from the autumn
ramp. Alpha is opaque inside a blade and clear outside, for a masked material. No runtime image generation."""
import json, math, random, struct, zlib
from pathlib import Path

SIZE = 128
BLADES = 34
DRY_SHARE = 0.12
SEED = 9546
RAMPS = json.loads((Path(__file__).resolve().parent.parent / 'content' / 'style' / 'palette.json').read_text())['ramps']


def rgb(hex_colour):
    return tuple(int(hex_colour.lstrip('#')[i:i + 2], 16) for i in (0, 2, 4))


MEADOW = [rgb(c) for c in RAMPS['meadow']]
AUTUMN = [rgb(c) for c in RAMPS['autumn']]

def png(path, width, height, rows):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', zlib.crc32(kind + data) & 0xffffffff)
    raw = b''.join(b'\0' + bytes(row) for row in rows)
    path.write_bytes(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
                     + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b''))

random.seed(SEED)
pixels = [[0, 0, 0, 0] * SIZE for _ in range(SIZE)]
for _ in range(BLADES):
    dry = random.random() < DRY_SHARE
    ramp = AUTUMN if dry else MEADOW
    root_index = random.choice((0, 1))
    ROOT = ramp[root_index]
    TIP = ramp[random.choice((root_index + 2, root_index + 3))]
    base = random.uniform(6, SIZE - 6)
    height = random.uniform(0.55, 0.98) * SIZE
    width = random.uniform(3.5, 7.0)
    lean = random.uniform(-0.35, 0.35)
    for y in range(SIZE):
        rise = SIZE - 1 - y
        if rise > height:
            continue
        t = rise / height
        centre = base + lean * rise + 6 * math.sin(t * 2.2) * lean
        half = width * (1 - t) * 0.5 + 0.4
        shade = [round(r + (p - r) * t) for r, p in zip(ROOT, TIP)]
        for x in range(int(centre - half), int(centre + half) + 1):
            if 0 <= x < SIZE:
                pixels[y][x * 4:x * 4 + 4] = [*shade, 255]

out = Path(__file__).resolve().parent.parent / 'content' / 'game' / 'textures' / 'grass-blades.png'
png(out, SIZE, SIZE, pixels)
print(out)
