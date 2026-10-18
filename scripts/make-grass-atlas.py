"""Assemble a card atlas from painted plant sprites: the grass tufts (#9668) and the flower clumps (#9831).

Each source is a generated side-view tuft or clump on a flat white background (asset-pipeline FLUX.2 klein,
content/grass.sources.json) or, with --key magenta, on a flat magenta one, which keeps white petals out of
the key; magenta is keyed by hue so lavender and blue petals survive. This keys the background out to a hard alpha for a masked material,
drops a painted ground line along the base if there is one, crops to the tuft, scales it to sit on
the bottom edge of its cell (centred, stretched by at most MAXIMUM_STRETCH to fill it) and pulls its colours a little toward the
palette's ramps (--ramps, default meadow, foliage, autumn and snow). Four sources fill a 2x2 atlas, left
to right then top to bottom; a clump variant (TerrainScatter's grass, generate-flora.py's flowercards) samples
one cell.

    python3 scripts/make-grass-atlas.py OUT.png A.png B.png C.png D.png [--cell 256]
    python3 scripts/make-grass-atlas.py OUT.png A.png B.png C.png D.png --key magenta --ramps foliage,meadow,sky,snow,autumn

With --centre a source is a top-down patch (the places' moss mats, #10044): no ground line is dropped, it keeps its
proportions and sits in the middle of its cell.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageFilter

PALETTE = Path(__file__).resolve().parent.parent / "content" / "style" / "palette.json"
# Distance from white (0..1 per channel, summed) below which a pixel is background, and above which opaque.
KEY_LOW, KEY_HIGH = 0.10, 0.22
# A row is a painted ground line when this share of the tuft's width is opaque in it.
BASELINE_SHARE = 0.6
BASELINE_SEARCH = 0.04
PULL_STRENGTH = 0.25
# Magenta key: a pixel is background when its hue is within MAGENTA_NEAR degrees of MAGENTA_HUE and it is at
# least MAGENTA_SATURATION saturated, fading to opaque by MAGENTA_FAR; the edge is then eroded by
# MAGENTA_ERODE texels (an odd filter size) to drop the pixels that are part magenta.
MAGENTA_HUE, MAGENTA_NEAR, MAGENTA_FAR, MAGENTA_SATURATION, MAGENTA_ERODE = 315.0, 24.0, 36.0, 0.3, 5
MARGIN = 0.02
# A tuft is stretched to fill its cell, by at most this much in either direction beyond a uniform fit,
# so a wide painted tuft still fills the card's height.
MAXIMUM_STRETCH = 1.6


def hex_rgb(value):
    value = value.lstrip("#")
    return [int(value[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def key_alpha(rgb, key):
    """Opacity 0..1 per pixel: 0 on the flat background (white or magenta), 1 on the painted plant."""
    if key == "white":
        return np.clip(((1 - rgb).sum(-1) - KEY_LOW) / (KEY_HIGH - KEY_LOW), 0, 1)
    hue = np.degrees(np.arctan2(np.sqrt(3) * (rgb[..., 1] - rgb[..., 2]), 2 * rgb[..., 0] - rgb[..., 1] - rgb[..., 2])) % 360
    high, low = rgb.max(-1), rgb.min(-1)
    saturation = (high - low) / np.maximum(high, 1e-6)
    away = np.clip((np.abs(hue - MAGENTA_HUE) - MAGENTA_NEAR) / (MAGENTA_FAR - MAGENTA_NEAR), 0, 1)
    alpha = np.where(saturation >= MAGENTA_SATURATION, away, 1.0)
    eroded = Image.fromarray((alpha * 255 + 0.5).astype(np.uint8)).filter(ImageFilter.MinFilter(MAGENTA_ERODE))
    return np.asarray(eroded, dtype=np.float64) / 255


def tuft(path, cell, palette, key="white", pull=PULL_STRENGTH, centre=False):
    rgb = np.asarray(Image.open(path).convert("RGB"), dtype=np.float64) / 255
    alpha = key_alpha(rgb, key)
    rows = np.where(alpha.max(1) > 0.5)[0]
    cols = np.where(alpha.max(0) > 0.5)[0]
    top, bottom, left, right = rows[0], rows[-1], cols[0], cols[-1]
    # Drop a painted ground line: wide, nearly full rows at the very bottom of the tuft.
    width = right - left + 1
    search = max(1, int(rgb.shape[0] * BASELINE_SEARCH))
    while not centre and bottom > top and (bottom > rows[-1] - search) and (alpha[bottom, left:right + 1] > 0.5).mean() > BASELINE_SHARE:
        bottom -= 1
    rgb, alpha = rgb[top:bottom + 1, left:right + 1], alpha[top:bottom + 1, left:right + 1]
    # Pull colours toward the palette (nearest ramp colour, a little).
    flat = rgb.reshape(-1, 3)
    nearest = ((flat[:, None, :] - palette[None]) ** 2).sum(-1).argmin(1)
    rgb = (flat + (palette[nearest] - flat) * pull).reshape(rgb.shape)
    image = Image.fromarray((np.concatenate([rgb, alpha[..., None]], -1) * 255 + 0.5).astype(np.uint8), "RGBA")
    usable = int(cell * (1 - 2 * MARGIN))
    scale = min(usable / image.width, usable / image.height)
    scale_x = scale if centre else min(usable / image.width, scale * MAXIMUM_STRETCH)
    scale_y = scale if centre else min(usable / image.height, scale * MAXIMUM_STRETCH)
    image = image.resize((max(1, int(image.width * scale_x)), max(1, int(image.height * scale_y))), Image.Resampling.LANCZOS)
    # Hard alpha for the mask: the material's cutoff decides coverage, so keep it crisp.
    a = np.asarray(image)[..., 3]
    out = np.asarray(image).copy()
    out[..., 3] = np.where(a >= 128, 255, 0)
    canvas = Image.new("RGBA", (cell, cell), (0, 0, 0, 0))
    canvas.paste(Image.fromarray(out, "RGBA"), ((cell - image.width) // 2, (cell - image.height) // 2 if centre else cell - image.height))
    return canvas


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("output")
    parser.add_argument("sources", nargs=4)
    parser.add_argument("--cell", type=int, default=256)
    parser.add_argument("--key", choices=("white", "magenta"), default="white", help="the sources' flat background")
    parser.add_argument("--ramps", default="meadow,foliage,autumn,snow", help="palette ramps the colours are pulled toward")
    parser.add_argument("--pull-strength", type=float, default=PULL_STRENGTH)
    parser.add_argument("--centre", action="store_true", help="top-down patches: centred, proportions kept, no ground line")
    args = parser.parse_args()
    ramps = json.loads(PALETTE.read_text())["ramps"]
    palette = np.array([hex_rgb(c) for name in args.ramps.split(",") for c in ramps[name]])
    atlas = Image.new("RGBA", (args.cell * 2, args.cell * 2), (0, 0, 0, 0))
    for index, source in enumerate(args.sources):
        atlas.paste(tuft(source, args.cell, palette, args.key, args.pull_strength, args.centre), ((index % 2) * args.cell, (index // 2) * args.cell))
    atlas.save(args.output)
    print("make-grass-atlas " + " ".join(f"{Path(s).parent.name}/{Path(s).name}" for s in args.sources))


if __name__ == "__main__":
    main()
