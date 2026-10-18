"""Paint the places' icicle fringes (#10068) as a 2x2 card atlas, pixel by pixel, at the small props' 64 texels/m.

Each cell is a fringe one card wide: a band of clear ice along the cell's bottom edge (the lip it freezes to) and
icicles rising from it, which the cards hang upside down from the lip, as the moss curtains hang
(Modules/Places/PlaceMossCards). Every icicle tapers to a point and is shaded as a cylinder lit from the left: a
pale highlight column, a blue-white body and a deeper blue far edge. The alpha is hard, for the masked material.
The four cells differ in how long and how crowded their icicles are.

    python3 scripts/generate-icicles.py content/game/textures/places/icicles.png
"""
import argparse

import numpy as np
from PIL import Image

CELL = 64
# sRGB: the highlight, the body, the far edge, and the band's own body.
HIGHLIGHT = (232, 244, 255)
BODY = (190, 216, 238)
EDGE = (128, 162, 200)
BAND = (206, 228, 246)
# The band along the lip, in texels, and how far its underside wavers.
BAND_TEXELS, BAND_WAVER = 4, 1
# Per cell: (icicles, shortest, longest) in texels; the cell is CELL texels high, so the longest reaches near its top.
CELLS = [(7, 6, 40), (10, 4, 24), (6, 10, 58), (8, 5, 48)]
WIDEST, NARROWEST = 7, 3
SEED = 10068


def fringe(rng, count, shortest, longest):
    rgba = np.zeros((CELL, CELL, 4), dtype=np.uint8)

    def put(x, y, colour):
        if 0 <= x < CELL and 0 <= y < CELL:
            rgba[CELL - 1 - y, x] = (*colour, 255)

    for x in range(CELL):
        depth = BAND_TEXELS + rng.integers(-BAND_WAVER, BAND_WAVER + 1)
        for y in range(depth):
            put(x, y, HIGHLIGHT if y == depth - 1 and x % 7 == 2 else BAND)
    # Icicles spaced along the fringe, jittered; longer ones a little wider at the root.
    slots = np.linspace(2, CELL - 3, count)
    for centre in slots:
        centre = int(round(centre + rng.uniform(-1.5, 1.5)))
        length = int(rng.integers(shortest, longest + 1))
        root = int(np.clip(NARROWEST + (length / longest) * (WIDEST - NARROWEST) + rng.uniform(-0.5, 0.5), NARROWEST, WIDEST))
        for y in range(length):
            width = max(1, int(round(root * (1 - y / length) ** 0.8)))
            left = centre - width // 2
            for i in range(width):
                colour = HIGHLIGHT if i == 0 and width > 1 else EDGE if i == width - 1 and width > 2 else BODY
                put(left + i, BAND_TEXELS - 1 + y, colour)
    return rgba


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("output")
    args = parser.parse_args()
    rng = np.random.default_rng(SEED)
    atlas = np.zeros((CELL * 2, CELL * 2, 4), dtype=np.uint8)
    for index, (count, shortest, longest) in enumerate(CELLS):
        row, column = index // 2, index % 2
        atlas[row * CELL:(row + 1) * CELL, column * CELL:(column + 1) * CELL] = fringe(rng, count, shortest, longest)
    Image.fromarray(atlas, "RGBA").save(args.output)
    print(f"generate-icicles {args.output} {CELL * 2}x{CELL * 2}")


if __name__ == "__main__":
    main()
