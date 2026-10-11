"""Draw the cactus skin tile (#9831): a soft mottle between two of the palette's dull greens with loose rows of
pale spine dots. Deterministic, tiling, RGBA (the Engine admits only RGBA PNGs). No ribs: the tile is
wrapped over the cactus by scripts/stylise-mesh.py's Smart UV charts, which turn it any way, so a stripe would
run across an arm as often as along it.

    python3 scripts/generate-cactus-skin.py content/game/textures/props/cactus.png
"""
import json
import random
import sys
from pathlib import Path

import numpy as np
from PIL import Image

PALETTE = Path(__file__).resolve().parent.parent / "content" / "style" / "palette.json"
SIZE = 128
SEED = 9831
# Mottle: value-noise scales in texels (broad, then fine) and their weights; the two greens it blends between, as
# (ramp, index) in the palette, close in value so the skin reads as one flesh and not as camouflage; the sage keeps it grey-green.
NOISE_SCALES, NOISE_WEIGHTS = (32, 8), (0.8, 0.2)
GREENS = (("foliage", 2), ("sage", 2))
# Spines: a dot every DOT_SPACING texels in rows offset by half a spacing, DOT_SIZE texels square, nudged by up to DOT_JITTER.
DOT_SPACING, DOT_SIZE, DOT_JITTER = 16, 2, 1


def hex_rgb(value):
    return [int(value[i:i + 2], 16) for i in (1, 3, 5)]


def wrap_noise(rng, scale):
    """Tiling value noise at one scale: a random lattice, smoothly interpolated, wrapping on both axes."""
    cells = SIZE // scale
    lattice = np.array([[rng.random() for _ in range(cells)] for _ in range(cells)])
    coords = np.arange(SIZE) / scale
    i0 = np.floor(coords).astype(int)
    t = coords - i0
    t = t * t * (3 - 2 * t)
    i1, i0 = (i0 + 1) % cells, i0 % cells
    top = lattice[np.ix_(i0, i0)] * (1 - t)[None, :] + lattice[np.ix_(i0, i1)] * t[None, :]
    bottom = lattice[np.ix_(i1, i0)] * (1 - t)[None, :] + lattice[np.ix_(i1, i1)] * t[None, :]
    return top * (1 - t)[:, None] + bottom * t[:, None]


def main():
    out = Path(sys.argv[1])
    ramps = json.loads(PALETTE.read_text())["ramps"]
    rng = random.Random(SEED)
    value = sum(w * wrap_noise(rng, s) for s, w in zip(NOISE_SCALES, NOISE_WEIGHTS))
    value = (value - value.min()) / (value.max() - value.min())
    dark, light = (np.array(hex_rgb(ramps[name][index]), dtype=float) for name, index in GREENS)
    image = dark + (light - dark) * value[..., None]
    spine = np.array(hex_rgb(ramps["sandstone"][4]), dtype=float)
    for row, y in enumerate(range(0, SIZE, DOT_SPACING)):
        for x in range(0, SIZE, DOT_SPACING):
            dx = (DOT_SPACING // 2 if row % 2 else 0) + rng.randint(-DOT_JITTER, DOT_JITTER)
            dy = rng.randint(-DOT_JITTER, DOT_JITTER)
            for oy in range(DOT_SIZE):
                for ox in range(DOT_SIZE):
                    image[(y + dy + oy) % SIZE, (x + dx + ox) % SIZE] = spine
    rgba = np.concatenate([image, np.full((SIZE, SIZE, 1), 255.0)], -1)
    Image.fromarray(np.clip(rgba, 0, 255).astype(np.uint8), "RGBA").save(out)
    print(f"generate-cactus-skin {out.name}: {SIZE} px")


if __name__ == "__main__":
    main()
