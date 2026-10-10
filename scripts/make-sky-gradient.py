"""Write CraftSurvive's gradient-only sky panoramas (#9804).

The sky's base is a plain gradient by elevation: no landforms, clouds, stars, sun or moon. Everything
that moves or changes (the cloud layer, the sun's disc, the horizon terrain, weather on the horizon)
is drawn over it, so nothing painted disagrees with the map or the clock.

The stops are the clear sky of the painted panoramas they replace (#9667): the median of the darkest
two fifths of each row, so the clouds and painted mountains are left out. Between stops the colour
runs straight in linear light, softly blurred along elevation. A faint fixed-seed dither keeps an
8-bit gradient from banding.

    python3 scripts/make-sky-gradient.py content/game/textures

It prints each panorama's horizon colour in linear light, which DayNightSky carries as the open
fog colour (DayHorizon, NightHorizon).
"""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image

# (elevation in degrees, sRGB 0-255), from the zenith down to the nadir.
DAY = [
    (90, (72, 114, 163)),
    (60, (73, 116, 167)),
    (45, (85, 133, 182)),
    (30, (111, 161, 199)),
    (20, (138, 181, 207)),
    (10, (166, 191, 203)),
    (3, (179, 196, 200)),
    (0, (176, 194, 199)),
    (-3, (152, 174, 187)),
    (-90, (148, 167, 180)),
]
NIGHT = [
    (90, (6, 9, 24)),
    (60, (8, 11, 27)),
    (45, (10, 15, 32)),
    (30, (14, 20, 40)),
    (20, (18, 25, 46)),
    (12, (21, 29, 52)),
    (6, (23, 32, 57)),
    (0, (26, 36, 61)),
    (-3, (22, 31, 52)),
    (-10, (13, 18, 30)),
    (-30, (8, 10, 16)),
    (-90, (8, 10, 16)),
]
PANORAMAS = {"sky-panorama.png": (DAY, 2048), "sky-night.png": (NIGHT, 1024)}
SEED = 9804
DITHER = 0.5 / 255
FOG_ROWS = 0.012
BLUR_DEGREES = 2.5


def linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def srgb(c):
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * np.power(np.clip(c, 0, None), 1 / 2.4) - 0.055)


def gradient(stops, width):
    height = width // 2
    elevation = 90 - (np.arange(height) + 0.5) / height * 180
    heights = np.array([e for e, _ in stops], dtype=np.float64)[::-1]
    colours = linear(np.array([c for _, c in stops], dtype=np.float64) / 255)[::-1]
    rows = np.stack([np.interp(elevation, heights, colours[:, channel]) for channel in range(3)], 1)
    # Straight segments between stops would show a crease at each; a soft blur along elevation rounds them.
    sigma = BLUR_DEGREES / 180 * height
    taps = np.arange(-int(4 * sigma), int(4 * sigma) + 1)
    kernel = np.exp(-0.5 * (taps / sigma) ** 2)
    kernel /= kernel.sum()
    padded = np.pad(rows, ((len(taps) // 2, len(taps) // 2), (0, 0)), mode="edge")
    rows = np.stack([np.convolve(padded[:, channel], kernel, mode="valid") for channel in range(3)], 1)
    image = np.repeat(srgb(rows)[:, None, :], width, 1)
    image += np.random.default_rng(SEED).uniform(-DITHER, DITHER, image.shape)
    return image


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    for name, (stops, width) in PANORAMAS.items():
        image = gradient(stops, width)
        rgb = (np.clip(image, 0, 1) * 255 + 0.5).astype(np.uint8)
        Image.fromarray(rgb, "RGB").convert("RGBA").save(args.directory / name)
        rows = max(1, int(rgb.shape[0] * FOG_ROWS))
        middle = rgb.shape[0] // 2
        fog = linear(rgb[middle - rows:middle].astype(np.float64) / 255).reshape(-1, 3).mean(0)
        print(f"{name}: {width}x{width // 2}; horizon colour (linear) {fog[0]:.4f}, {fog[1]:.4f}, {fog[2]:.4f}")


if __name__ == "__main__":
    main()
