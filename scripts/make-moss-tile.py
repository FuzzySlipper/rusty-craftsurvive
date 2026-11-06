#!/usr/bin/env python3
"""The moss painted into a place's stone and the tufts on it (#10640): a seamless tile quilted from the moss mats' solid
interiors, and the mats themselves, both lightened alike so tufts sit in the moss around them.

Feathered blocks of the mats (content/game/textures/places/moss-mats.png) are laid at random, wrapping at the edges, so the
tile repeats without a seam and carries the mats' own colours. Blending softens the texels, so their contrast is stretched
back and each channel posted to a few steps. surface-moss.wgsl reads it at the moss field's texels, 128 of them across.

    python3 scripts/make-moss-tile.py
"""
import random
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "content/game/textures/places/moss-mats.png"
OUT = ROOT / "content/game/textures/places/moss-tile.png"
TUFTS = ROOT / "content/game/textures/places/moss-tufts.png"

SEED = 10640
SIZE = 128
BLOCK = 26
BLOCKS = 420
CELL = 64
# Blocks start this far inside a cell, every so many texels, and must be this opaque throughout.
CELL_MARGIN = 6
STEP = 3
SOLID = 250
CONTRAST = 1.7
POST = 10
FEATHER_POWER = 1.2
# The mats are dark for cards on bare stone; on it they want this much more light, in linear colour.
LIGHT = 1.7


def lightened(srgb):
    """Colours in 0..255 with LIGHT times their linear light."""
    colour = srgb / 255
    linear = np.where(colour <= 0.04045, colour / 12.92, ((colour + 0.055) / 1.055) ** 2.4)
    linear = np.clip(linear * LIGHT, 0, 1)
    return 255 * np.where(linear <= 0.0031308, linear * 12.92, 1.055 * linear ** (1 / 2.4) - 0.055)


def main():
    rng = random.Random(SEED)
    mats = np.array(Image.open(SOURCE).convert("RGBA")).astype(float)
    blocks = []
    for cell_y in (0, CELL):
        for cell_x in (0, CELL):
            for y in range(cell_y + CELL_MARGIN, cell_y + CELL - BLOCK - CELL_MARGIN + 1, STEP):
                for x in range(cell_x + CELL_MARGIN, cell_x + CELL - BLOCK - CELL_MARGIN + 1, STEP):
                    block = mats[y : y + BLOCK, x : x + BLOCK]
                    if block[..., 3].min() > SOLID:
                        blocks.append(block[..., :3])

    rows, columns = np.mgrid[0:BLOCK, 0:BLOCK]
    edge = np.minimum(np.minimum(columns, BLOCK - 1 - columns), np.minimum(rows, BLOCK - 1 - rows)) / (BLOCK / 2)
    feather = (edge**FEATHER_POWER)[..., None] + 1e-3

    colour = np.zeros((SIZE, SIZE, 3))
    weight = np.zeros((SIZE, SIZE, 1))
    for _ in range(BLOCKS):
        block = rng.choice(blocks)
        ys = (np.arange(BLOCK) + rng.randrange(SIZE)) % SIZE
        xs = (np.arange(BLOCK) + rng.randrange(SIZE)) % SIZE
        colour[np.ix_(ys, xs)] += block * feather
        weight[np.ix_(ys, xs)] += feather

    tile = colour / weight
    mean = tile.mean((0, 1))
    tile = np.clip(mean + (tile - mean) * CONTRAST, 0, 255)
    tile = np.clip(np.round(tile / POST) * POST, 0, 255)
    pixels = np.dstack([lightened(tile), np.full((SIZE, SIZE), 255.0)]).round().astype("uint8")
    Image.fromarray(pixels, "RGBA").save(OUT)
    tufts = np.dstack([lightened(mats[..., :3]), mats[..., 3]]).round().astype("uint8")
    Image.fromarray(tufts, "RGBA").save(TUFTS)
    print(f"{OUT.relative_to(ROOT)}: {len(blocks)} source blocks")


if __name__ == "__main__":
    main()
