#!/usr/bin/env python3
"""Paints the cast marks (#10529) into the decal atlases' empty bottom rows.

The marks are white, so a decal's colour gives their hue: a cast's glow, wet ground or bleached grass
(src/CraftSurvive.Game/Modules/Chemistry/CastLook.cs). Four 64 x 64 tiles from y 192: two soft blobs
with ragged rims, then two mottled ones for drying. The emission atlas takes the same tiles in white, so
a mark's emission shows where its colour does. Everything above y 192 is left as it is.

    python3 scripts/generate-cast-marks.py
"""
import math
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
ATLAS = ROOT / "content/game/textures/decals/atlas.png"
EMISSION = ROOT / "content/game/textures/decals/emission.png"
TOP, TILE, TILES = 192, 64, 4
SEED = 10529


def noise(rng, size, cells):
    """Smooth value noise from 0 to 1: a coarse random grid, bilinearly filled."""
    grid = rng.random((cells + 1, cells + 1))
    y, x = np.mgrid[0:size, 0:size] / size * cells
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0
    fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
    top = grid[y0, x0] * (1 - fx) + grid[y0, x0 + 1] * fx
    bottom = grid[y0 + 1, x0] * (1 - fx) + grid[y0 + 1, x0 + 1] * fx
    return top * (1 - fy) + bottom * fy


def tile(rng, mottled):
    """A blob's alpha: whole in the middle, a ragged rim fading out before the tile's edge."""
    y, x = (np.mgrid[0:TILE, 0:TILE] + 0.5) / TILE * 2 - 1
    angle = np.arctan2(y, x)
    rim = 0.78 + 0.12 * np.sin(angle * 3 + rng.random() * math.tau) + 0.08 * (noise(rng, TILE, 6) - 0.5)
    distance = np.sqrt(x * x + y * y) / rim
    alpha = np.clip((1 - distance) / 0.35, 0, 1)
    alpha = alpha * alpha * (3 - 2 * alpha)
    if mottled:
        alpha *= np.clip(0.35 + 0.9 * noise(rng, TILE, 10), 0, 1)
    else:
        alpha *= 0.85 + 0.15 * noise(rng, TILE, 8)
    return alpha


def main():
    generator = np.random.default_rng(SEED)
    atlas = np.array(Image.open(ATLAS).convert("RGBA"))
    emission = np.array(Image.open(EMISSION).convert("RGBA"))
    for index in range(TILES):
        alpha = tile(generator, mottled=index >= 2)
        x = index * TILE
        atlas[TOP:TOP + TILE, x:x + TILE, :3] = 255
        atlas[TOP:TOP + TILE, x:x + TILE, 3] = np.round(alpha * 255).astype(np.uint8)
        emission[TOP:TOP + TILE, x:x + TILE, :3] = 255
        emission[TOP:TOP + TILE, x:x + TILE, 3] = 255
    Image.fromarray(atlas).save(ATLAS)
    Image.fromarray(emission).save(EMISSION)
    print(f"painted {TILES} cast marks into {ATLAS.name} and {EMISSION.name} from y {TOP}")


if __name__ == "__main__":
    main()
