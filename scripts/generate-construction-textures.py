"""Draw the construction study's repeating maps from the palette (#9684).

Four tiling maps for built pieces, in the same chunky, nearest-filtered texel scale as the
procedural trees' bark (about 3 cm a texel), so built work reads as one style with the trees:

- planks: vertical boards with dark seams, grain streaks, a knot or two and nail heads;
- timber: posts and beams, darker, with long grain;
- masonry: irregular stone courses in the stone ramp with lighter mortar;
- shingles: staggered wooden roof shingles, each row's lower edge in shadow.

Writes content/game/textures/construction/NAME.png (RGBA, the Engine admits only RGBA PNGs),
materials.json (with each map's hash, metresPerTile and nearest filtering) and blending.json, as
TerrainGroundMaterials reads a ground texture set.

    python3 scripts/generate-construction-textures.py
"""
import hashlib
import json
import random
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
FOLDER = ROOT / "content" / "game" / "textures" / "construction"
PALETTE = ROOT / "content" / "style" / "palette.json"
TILE = 64
# Metres one tile covers: 64 texels over 2 m is about 3 cm a texel.
METRES_PER_TILE = 2.0
TRIPLANAR_SHARPNESS = 8


def ramps():
    return {name: [np.array([int(h[i:i + 2], 16) for i in (1, 3, 5)], dtype=float) for h in values]
            for name, values in json.loads(PALETTE.read_text())["ramps"].items()}


def ramp(r, value):
    return r[min(len(r) - 1, max(0, int(round(value * (len(r) - 1)))))]


def noise(rng, scale):
    """Tiling value noise in [0, 1)."""
    cells = max(1, TILE // scale)
    lattice = np.array([[rng.random() for _ in range(cells)] for _ in range(cells)])
    ys, xs = np.mgrid[0:TILE, 0:TILE]
    fx, fy = xs / TILE * cells, ys / TILE * cells
    x0, y0 = np.floor(fx).astype(int), np.floor(fy).astype(int)
    tx, ty = fx - x0, fy - y0
    x1, y1, x0, y0 = (x0 + 1) % cells, (y0 + 1) % cells, x0 % cells, y0 % cells
    top = lattice[y0, x0] * (1 - tx) + lattice[y0, x1] * tx
    return top * (1 - ty) + (lattice[y1, x0] * (1 - tx) + lattice[y1, x1] * tx) * ty


def planks(p, rng):
    wood = [(a + b) / 2 for a, b in zip(p["earth"], p["autumn"])]
    image = np.zeros((TILE, TILE, 3))
    board = 8
    grain = noise(rng, 4)
    for x0 in range(0, TILE, board):
        tone = rng.uniform(0.35, 0.75)
        for x in range(x0, x0 + board):
            for y in range(TILE):
                streak = 0.12 * np.sin((x - x0) * 1.7 + grain[y, x] * 6)
                image[y, x] = ramp(wood, tone + streak + 0.1 * grain[y, x])
        image[:, x0] = ramp(wood, 0.0)
        # A butt joint somewhere along each board, and nail heads beside it.
        joint = rng.randrange(TILE)
        image[joint, x0:x0 + board] = ramp(wood, 0.05)
        for nail in (x0 + 2, x0 + board - 3):
            image[(joint + 2) % TILE, nail] = ramp(p["stone"], 0.2)
        if rng.random() < 0.4:
            kx, ky = x0 + rng.randint(2, board - 3), rng.randrange(TILE)
            image[ky, kx] = ramp(wood, 0.0)
            image[(ky + 1) % TILE, kx] = ramp(wood, 0.15)
    return image


def timber(p, rng):
    wood = p["bark"]
    image = np.zeros((TILE, TILE, 3))
    grain = noise(rng, 8)
    for x in range(TILE):
        line = 0.15 * np.sin(x * 0.9 + rng.random())
        for y in range(TILE):
            image[y, x] = ramp(wood, 0.45 + line + 0.3 * (grain[y, x] - 0.5))
    for _ in range(14):
        x, y, length = rng.randrange(TILE), rng.randrange(TILE), rng.randint(6, 20)
        for i in range(length):
            image[(y + i) % TILE, x] = ramp(wood, 0.1)
    return image


def masonry(p, rng):
    stone, mortar = p["stone"], p["sage"]
    image = np.zeros((TILE, TILE, 3))
    image[:, :] = ramp(mortar, 0.75)
    shade = noise(rng, 8)
    y = 0
    while y < TILE:
        course = rng.choice((8, 10, 12)) if TILE - y > 14 else TILE - y
        x = rng.randrange(TILE)
        end = x + TILE
        while x < end:
            width = rng.randint(9, 18)
            tone = rng.uniform(0.3, 0.8)
            for dy in range(1, course - 1):
                for dx in range(1, width - 1):
                    yy, xx = (y + dy) % TILE, (x + dx) % TILE
                    lit = 0.12 if dy == 1 else -0.12 if dy == course - 2 else 0.0
                    image[yy, xx] = ramp(stone, tone + lit + 0.2 * (shade[yy, xx] - 0.5))
            x += width
        y += course
    return image


def shingles(p, rng):
    wood = [(a * 2 + b) / 3 for a, b in zip(p["bark"], p["autumn"])]
    image = np.zeros((TILE, TILE, 3))
    row = 8
    for y0 in range(0, TILE, row):
        offset = (y0 // row) % 2 * 4
        x = offset
        while x < TILE + offset:
            width = rng.randint(5, 9)
            tone = rng.uniform(0.35, 0.8)
            for dx in range(width):
                for dy in range(row):
                    shadow = -0.35 if dy >= row - 2 else 0.0
                    image[y0 + dy, (x + dx) % TILE] = ramp(wood, tone + shadow + 0.05 * rng.random())
            for dy in range(row):
                image[y0 + dy, (x + width - 1) % TILE] = ramp(wood, 0.0)
            x += width
    return image


MAPS = {"planks": planks, "timber": timber, "masonry": masonry, "shingles": shingles}


def main():
    palette = ramps()
    FOLDER.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, draw in MAPS.items():
        image = draw(palette, random.Random(f"construction-{name}"))
        path = FOLDER / f"{name}.png"
        Image.fromarray(np.clip(image, 0, 255).astype(np.uint8), "RGB").convert("RGBA").save(path)
        manifest.append({"id": name, "width": TILE, "height": TILE,
                         "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                         "metresPerTile": METRES_PER_TILE, "triplanarSharpness": TRIPLANAR_SHARPNESS, "filter": "nearest"})
        print(f"generate-construction-textures {path.name}")
    (FOLDER / "materials.json").write_text(json.dumps(manifest, indent=2) + "\n")
    (FOLDER / "blending.json").write_text(json.dumps(
        {"transitionCells": 1, "weightContrast": 2, "textureScale": 1, "projectionSharpnessScale": 1}, indent=2) + "\n")


if __name__ == "__main__":
    main()
