"""Makes a creature's emission texture from its game paint (#10877): the texels of one hue band keep their colour, raised
to full brightness, and every other texel is black, so only those parts glow (the mouldback's blue-green glow caps).

usage: python3 -I authoring/tools/creatures/emission_mask.py <body-64.png> <emission.png> [--hue 150 215] [--saturation 0.25] [--value 0.35]

Hue is in degrees, saturation and value 0 to 1 (HSV of the sRGB texel). It prints how many texels it kept.
"""
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
    source, out = argv[0], argv[1]
    low, high = option(argv, "--hue", [150.0, 215.0], 2)
    saturation = option(argv, "--saturation", 0.25)
    value = option(argv, "--value", 0.35)
    rgb = np.asarray(Image.open(source).convert("RGB")).astype(np.float64) / 255.0
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    top, bottom = rgb.max(-1), rgb.min(-1)
    spread = np.maximum(top - bottom, 1e-6)
    hue = np.where(top == r, ((g - b) / spread) % 6, np.where(top == g, (b - r) / spread + 2, (r - g) / spread + 4)) * 60.0
    sat = np.where(top > 0, (top - bottom) / np.maximum(top, 1e-6), 0.0)
    glow = (hue >= low) & (hue <= high) & (sat >= saturation) & (top >= value)
    lit = np.where(glow[..., None], rgb / np.maximum(top, 1e-6)[..., None], 0.0)
    Image.fromarray((lit * 255).round().astype(np.uint8), "RGB").save(out)
    print(f"emission: {int(glow.sum())} of {glow.size} texels glow -> {out}")


if __name__ == "__main__":
    main(sys.argv[1:])
