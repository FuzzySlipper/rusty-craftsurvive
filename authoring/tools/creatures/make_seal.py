"""Makes the membrane a mender grows across a ruin doorway (#10961): a prop mesh the product stands in the doorway and grows
up from the floor, settled with Patch on the wiki's Ruins wildlife page.

usage: python3 -I authoring/tools/creatures/make_seal.py <out dir> [--content-root content/game]

The membrane spans the kit's doorway, 2 m across (x) and 3 m up (y), bowed a little along z, the way through; both faces
are drawn, each with its own points. Its paint is the game's 64 texels/m: pale shell grown thin, darker veins running up
from the floor, a jade band where it meets the frame and plum where it grew from. Written as seal.prop-mesh.json with
seal.png beside it.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

WIDTH, HEIGHT, BOW = 2.0, 3.0, 0.12
COLUMNS, ROWS = 8, 10
TEXELS_PER_METRE = 64
SEED = 10961
PALE = np.array((214, 204, 186))
PALE_DARK = np.array((190, 176, 156))
VEIN = np.array((160, 132, 116))
JADE = np.array((138, 164, 126))
PLUM = np.array((112, 58, 70))


def sheet():
    """The bowed sheet's points, uvs and triangles, then the same again facing the other way."""
    points, uvs = [], []
    for row in range(ROWS + 1):
        v = row / ROWS
        for column in range(COLUMNS + 1):
            u = column / COLUMNS
            # It bows out most at its middle and meets the frame flat at its edges.
            bow = BOW * np.sin(np.pi * u) * np.sin(np.pi * min(1.0, v * 1.1))
            points.append((WIDTH * (u - 0.5), HEIGHT * v, bow))
            uvs.append((u, 1.0 - v))
    triangles = []
    for row in range(ROWS):
        for column in range(COLUMNS):
            a = row * (COLUMNS + 1) + column
            b, c, d = a + 1, a + COLUMNS + 1, a + COLUMNS + 2
            triangles += [(a, b, c), (b, d, c)]
    count = len(points)
    back = [(a + count, c + count, b + count) for a, b, c in triangles]
    return np.array(points + points, dtype=float), np.array(uvs + uvs, dtype=float), triangles + back


def normals_of(points, triangles):
    normals = np.zeros_like(points)
    for a, b, c in triangles:
        normals[[a, b, c]] += np.cross(points[b] - points[a], points[c] - points[a])
    return normals / np.linalg.norm(normals, axis=1, keepdims=True)


def paint(rng):
    width, height = int(WIDTH * TEXELS_PER_METRE / 2), int(HEIGHT * TEXELS_PER_METRE / 2)
    image = np.zeros((height, width, 3))
    veins = rng.integers(3, width - 3, 7)
    # A few thicker patches where it grew slower, scattered, never in rows.
    blotches = rng.random((height // 4 + 1, width // 4 + 1)) < 0.08
    for row in range(height):
        for column in range(width):
            colour = PALE + rng.integers(-4, 5, 3)
            if blotches[row // 4, column // 4]:
                colour = PALE_DARK
            # The veins wander a little as they climb from the floor.
            if any(abs(column - (vein + int(2 * np.sin(row / 9 + vein)))) == 0 for vein in veins) and row > height // 5:
                colour = VEIN
            if column < 2 or column >= width - 2 or row < 2:
                colour = JADE
            if row >= height - 3:
                colour = PLUM
            image[row, column] = colour
    return np.clip(image, 0, 255).astype(np.uint8)


def main(argv):
    out = Path(argv[0])
    content_root = argv[argv.index("--content-root") + 1] if "--content-root" in argv else "content/game"
    out.mkdir(parents=True, exist_ok=True)
    points, uvs, triangles = sheet()
    normals = normals_of(points, triangles)
    Image.fromarray(paint(np.random.default_rng(SEED))).convert("RGBA").save(out / "seal.png")
    flat = lambda array: [round(float(v), 5) for v in np.asarray(array).ravel()]
    texture = str((out / "seal.png").resolve().relative_to(Path(content_root).resolve()))
    piece = {"name": "seal", "height": HEIGHT, "parts": [{"role": "shell", "positions": flat(points), "normals": flat(normals),
             "uvs": flat(uvs), "colors": [1.0] * (4 * len(points)), "indices": [int(i) for tri in triangles for i in tri]}],
             "textures": {"shell": texture}}
    (out / "seal.prop-mesh.json").write_text(json.dumps(piece))
    print(f"seal: {len(triangles)} triangles -> {out / 'seal.prop-mesh.json'}")


if __name__ == "__main__":
    main(sys.argv[1:])
