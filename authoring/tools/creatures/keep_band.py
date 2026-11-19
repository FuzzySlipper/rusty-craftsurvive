"""Keeps one hue band of a master paint through pixel reduction, with its own palette (#10878).

usage: python3 -I authoring/tools/creatures/keep_band.py <master.png> <reduced.png> <out.png> \
           [--hue -20 22] [--saturation 0.22] [--colors 6]

pixel_reduce.py spends its palette on what covers most of a body, so a small part in another colour (the draft-hound's
pink, red-veined ear-fans among its tan hide) comes out in the body's colours. This takes the master down to the
reduced paint's size (box filter), finds the texels whose master hue lies in the band (degrees, either end may be
negative to wrap past red) at least that saturated, quantizes just those to their own few colours and writes them over
the reduced paint. It prints how many texels it kept.
"""
import colorsys
import sys

import numpy as np
from PIL import Image


def option(argv, name, default, count=1):
    if name not in argv:
        return default
    at = argv.index(name)
    values = [float(v) for v in argv[at + 1:at + 1 + count]]
    return values if count > 1 else values[0]


def main(argv):
    master_path, reduced_path, out_path = argv[:3]
    low, high = option(argv, "--hue", [-20.0, 22.0], 2)
    least = option(argv, "--saturation", 0.22)
    colors = int(option(argv, "--colors", 6))
    reduced = Image.open(reduced_path).convert("RGB")
    small = Image.open(master_path).convert("RGB").resize(reduced.size, Image.BOX)
    pixels = np.asarray(small, dtype=np.float32) / 255.0
    hsv = np.array([colorsys.rgb_to_hsv(*p) for p in pixels.reshape(-1, 3)]).reshape(pixels.shape)
    hue = hsv[..., 0] * 360.0
    hue = np.where(hue > 180.0 + (low + high) / 2.0, hue - 360.0, hue)
    band = (hue >= low) & (hue <= high) & (hsv[..., 1] >= least) & (hsv[..., 2] > 0.08)
    if not band.any():
        raise SystemExit("no texels in the band")
    picked = np.asarray(small)[band].reshape(1, -1, 3).astype(np.uint8)
    palette = Image.fromarray(picked).quantize(colors=colors, method=Image.Quantize.MEDIANCUT).convert("RGB")
    out = np.asarray(reduced).copy()
    out[band] = np.asarray(palette).reshape(-1, 3)
    Image.fromarray(out).save(out_path)
    print(f"band: {int(band.sum())} of {band.size} texels kept in {colors} colours -> {out_path}")


if __name__ == "__main__":
    main(sys.argv[1:])
