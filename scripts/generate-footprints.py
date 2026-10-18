"""Paint the snow's footprints (#10068) as a 4x2 card atlas, pixel by pixel, at 64 texels a metre.

The top row is a boot print and the bottom row a paw's track (four toes before a pad); left to right each fades:
fresh (a crisp blue-grey hollow with a bright rim where the snow was pushed up), settled (fresh snow has softened
it: paler, smaller, no rim) and faint (a few pale texels). The fourth column is empty. Each print points up the
cell (toward v 0), centred, and lies flat on the snow (Modules/Weather/SnowTracks). The alpha is hard, for the
masked material.

    python3 scripts/generate-footprints.py content/game/textures/snow/footprints.png
"""
import argparse

import numpy as np
from PIL import Image

CELL = 32
# sRGB: the hollow's shadowed wall, its floor, and the pushed-up rim; then the settled and faint stages' colours.
WALL = (138, 152, 182)
FLOOR = (168, 182, 208)
RIM = (246, 249, 255)
SETTLED = (190, 202, 224)
FAINT = (206, 216, 234)
SEED = 10068


def ellipse(cx, cy, rx, ry):
    ys, xs = np.mgrid[0:CELL, 0:CELL]
    return ((xs + 0.5 - cx) / rx) ** 2 + ((ys + 0.5 - cy) / ry) ** 2 <= 1


def boot():
    """A sole and a heel, toe up: about 0.28 m long and 0.11 m wide."""
    return ellipse(16, 11, 3.6, 5.6) | ellipse(16, 21, 3.0, 3.2) | (ellipse(16, 16, 2.6, 4.0))


def paw():
    """A pad and four toes, toe up: about 0.12 m across."""
    shape = ellipse(16, 21, 3.4, 2.8)
    for dx, dy in ((-4.6, -5.0), (-1.7, -7.4), (1.7, -7.4), (4.6, -5.0)):
        shape |= ellipse(16 + dx, 21 + dy, 1.5, 1.7)
    return shape


def shift(mask, dx, dy):
    return np.roll(np.roll(mask, dy, axis=0), dx, axis=1)


def stages(mask, rng):
    """The fresh, settled and faint prints of one shape, as RGBA cells."""
    cells = []
    fresh = np.zeros((CELL, CELL, 4), dtype=np.uint8)
    rim = (shift(mask, 0, 1) | shift(mask, 1, 0) | shift(mask, -1, 0) | shift(mask, 0, -1)) & ~mask
    # The rim catches the light on the toe-side and left; the far side of the hollow is its shadowed wall.
    wall = mask & ~shift(mask, 0, 1)
    wall |= mask & ~shift(mask, -1, 0)
    fresh[mask] = (*FLOOR, 255)
    fresh[wall] = (*WALL, 255)
    fresh[rim & (shift(rim, 0, 1) | shift(rim, 1, 0))] = (*RIM, 255)
    cells.append(fresh)
    settled = np.zeros_like(fresh)
    inner = mask & shift(mask, 0, 1) & shift(mask, 0, -1) & shift(mask, 1, 0) & shift(mask, -1, 0)
    settled[inner] = (*SETTLED, 255)
    cells.append(settled)
    faint = np.zeros_like(fresh)
    keep = inner & (rng.random(mask.shape) < 0.35)
    faint[keep] = (*FAINT, 255)
    cells.append(faint)
    cells.append(np.zeros_like(fresh))
    return cells


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("output")
    args = parser.parse_args()
    rng = np.random.default_rng(SEED)
    atlas = np.zeros((CELL * 2, CELL * 4, 4), dtype=np.uint8)
    for row, shape in enumerate((boot(), paw())):
        for column, cell in enumerate(stages(shape, rng)):
            atlas[row * CELL:(row + 1) * CELL, column * CELL:(column + 1) * CELL] = cell
    Image.fromarray(atlas, "RGBA").save(args.output)
    print(f"generate-footprints {args.output} {CELL * 4}x{CELL * 2}")


if __name__ == "__main__":
    main()
