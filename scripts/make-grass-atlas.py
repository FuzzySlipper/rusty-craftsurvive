"""Assemble the grass card atlas from painted grass tuft sprites (#9668).

Each source is a generated side-view tuft on a flat white background (asset-pipeline FLUX.2 klein,
content/grass.sources.json). This keys the white out to a hard alpha for a masked material,
drops a painted ground line along the base if there is one, crops to the tuft, scales it to sit on
the bottom edge of its cell (centred, stretched by at most MAXIMUM_STRETCH to fill it) and pulls its colours a little toward the
palette's meadow and autumn ramps. Four sources fill a 2x2 atlas, left to right then top to
bottom; TerrainScatter's clump variants each sample one cell.

    python3 scripts/make-grass-atlas.py OUT.png A.png B.png C.png D.png [--cell 256]
"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

PALETTE = Path(__file__).resolve().parent.parent / "content" / "style" / "palette.json"
# Distance from white (0..1 per channel, summed) below which a pixel is background, and above which opaque.
KEY_LOW, KEY_HIGH = 0.10, 0.22
# A row is a painted ground line when this share of the tuft's width is opaque in it.
BASELINE_SHARE = 0.6
BASELINE_SEARCH = 0.04
PULL_STRENGTH = 0.25
MARGIN = 0.02
# A tuft is stretched to fill its cell, by at most this much in either direction beyond a uniform fit,
# so a wide painted tuft still fills the card's height.
MAXIMUM_STRETCH = 1.6


def hex_rgb(value):
    value = value.lstrip("#")
    return [int(value[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def tuft(path, cell, palette):
    rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255
    distance = (1 - rgb).sum(-1)
    alpha = np.clip((distance - KEY_LOW) / (KEY_HIGH - KEY_LOW), 0, 1)
    rows = np.where(alpha.max(1) > 0.5)[0]
    cols = np.where(alpha.max(0) > 0.5)[0]
    top, bottom, left, right = rows[0], rows[-1], cols[0], cols[-1]
    # Drop a painted ground line: wide, nearly full rows at the very bottom of the tuft.
    width = right - left + 1
    search = max(1, int(rgb.shape[0] * BASELINE_SEARCH))
    while bottom > top and (bottom > rows[-1] - search) and (alpha[bottom, left:right + 1] > 0.5).mean() > BASELINE_SHARE:
        bottom -= 1
    rgb, alpha = rgb[top:bottom + 1, left:right + 1], alpha[top:bottom + 1, left:right + 1]
    # Pull colours toward the palette (nearest ramp colour, a little).
    flat = rgb.reshape(-1, 3)
    nearest = ((flat[:, None, :] - palette[None]) ** 2).sum(-1).argmin(1)
    rgb = (flat + (palette[nearest] - flat) * PULL_STRENGTH).reshape(rgb.shape)
    image = Image.fromarray((np.concatenate([rgb, alpha[..., None]], -1) * 255 + 0.5).astype(np.uint8), "RGBA")
    usable = int(cell * (1 - 2 * MARGIN))
    scale = min(usable / image.width, usable / image.height)
    scale_x = min(usable / image.width, scale * MAXIMUM_STRETCH)
    scale_y = min(usable / image.height, scale * MAXIMUM_STRETCH)
    image = image.resize((max(1, int(image.width * scale_x)), max(1, int(image.height * scale_y))), Image.Resampling.LANCZOS)
    # Hard alpha for the mask: the material's cutoff decides coverage, so keep it crisp.
    a = np.asarray(image)[..., 3]
    out = np.asarray(image).copy()
    out[..., 3] = np.where(a >= 128, 255, 0)
    canvas = Image.new("RGBA", (cell, cell), (0, 0, 0, 0))
    canvas.paste(Image.fromarray(out, "RGBA"), ((cell - image.width) // 2, cell - image.height))
    return canvas


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("output")
    parser.add_argument("sources", nargs=4)
    parser.add_argument("--cell", type=int, default=256)
    args = parser.parse_args()
    ramps = json.loads(PALETTE.read_text())["ramps"]
    palette = np.array([hex_rgb(c) for name in ("meadow", "foliage", "autumn", "snow") for c in ramps[name]])
    atlas = Image.new("RGBA", (args.cell * 2, args.cell * 2), (0, 0, 0, 0))
    for index, source in enumerate(args.sources):
        atlas.paste(tuft(source, args.cell, palette), ((index % 2) * args.cell, (index // 2) * args.cell))
    atlas.save(args.output)
    print("make-grass-atlas " + " ".join(f"{Path(s).parent.name}/{Path(s).name}" for s in args.sources))


if __name__ == "__main__":
    main()
