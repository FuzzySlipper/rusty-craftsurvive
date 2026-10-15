"""Cuts a 2x2 expression sheet into the face textures the product swaps at runtime (#10014).

usage: python3 -I art/characters/face_set.py <sheet.png> <out-dir> <name> [sizes...]

The sheet's cells are neutral, blink (top row), talk, smile (bottom row), each the face slot's
whole front projection. Blink and talk take only their changed band (eyes, mouth) over the
neutral cell with a feathered mask, so swapping never shifts the rest of the face; smile keeps
its whole cell (cheeks and eyes change too). Each texture is box-reduced to every size: the face
is denser than the body on purpose (docs/visual-direction.md texture crunch), and the sizes let
the in-game test compare face densities.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

# The sheet was painted over an earlier fit whose lips sat higher: below the nose line, each
# texture row samples the sheet at NOSE + (v - NOSE) * LOWER_FACE, so the painted mouth meets
# the modelled lips (fit_to_ual.py anchors the face box at the nose tip).
NOSE, LOWER_FACE = 0.66, 0.74
# Bands in cell coordinates (left, top, right, bottom), feathered over FEATHER of the cell.
EYES = (0.02, 0.27, 0.98, 0.52)
MOUTH = (0.18, 0.70, 0.82, 0.95)
FEATHER = 0.03


def band_mask(size, box):
    mask = Image.new("L", (size, size), 0)
    l, t, r, b = (int(v * size) for v in box)
    mask.paste(255, (l, t, r, b))
    return mask.filter(ImageFilter.GaussianBlur(FEATHER * size))


def stretch_lower(image):
    a = np.asarray(image)
    h = a.shape[0]
    v = (np.arange(h) + 0.5) / h
    source = np.where(v > NOSE, NOSE + (v - NOSE) * LOWER_FACE, v)
    return Image.fromarray(a[(source * h).astype(int).clip(0, h - 1)])


def main(sheet_path, out_dir, name, *sizes):
    sheet = Image.open(sheet_path).convert("RGB")
    cell = sheet.width // 2
    cells = {key: sheet.crop((x * cell, y * cell, (x + 1) * cell, (y + 1) * cell))
             for key, (x, y) in {"neutral": (0, 0), "blink": (1, 0), "talk": (0, 1), "smile": (1, 1)}.items()}
    faces = {
        "neutral": cells["neutral"],
        "blink": Image.composite(cells["blink"], cells["neutral"], band_mask(cell, EYES)),
        "talk": Image.composite(cells["talk"], cells["neutral"], band_mask(cell, MOUTH)),
        "smile": cells["smile"],
    }
    faces = {key: stretch_lower(image) for key, image in faces.items()}
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    for size in (int(s) for s in (sizes or ("64",))):
        for expression, image in faces.items():
            image.convert("RGBA").resize((size, size), Image.BOX).save(out / f"{name}-{expression}-{size}.png")
        print("faces", size, list(faces))


main(*sys.argv[1:])
