"""Stylise a generated texture toward CraftSurvive's palette and retro surface character (#9664).

Generated textures (asset-pipeline tools/materials, image models) come out as bland mid-realism:
fine noise, muddy colour, photographic detail. This turns one into a game texture:

1. resize to the target texel size (a power of two; Lanczos);
2. optional painterly grouping: a Kuwahara filter, run at twice the output size, flattens fine
   noise into strokes of one colour while keeping edges (--paint RADIUS in output texels, 0 disables);
3. tone: brightness, contrast about the mean, saturation (in linear-ish sRGB, simple and stable);
4. optional pull toward palette ramps: each pixel moves part of the way (--pull-strength) toward the
   nearest colour of the named ramps in CIE Lab, which unifies hues without posterising;
5. optional colour grouping: k-means into N colours (fixed seed, deterministic), blended back by
   --group-strength, for the economical colour count of an older game;
6. optional unsharp mask so the downsampled marks stay crisp under linear filtering.

Every neighbourhood operation wraps around the edges with --tile, so a tileable input stays tileable.
Alpha is carried through untouched. The arguments are printed as one line for the sources record.

    python3 scripts/stylise-texture.py IN.png OUT.png --size 256 --paint 2 --pull meadow,earth \
        --pull-strength 0.5 --colors 24 --group-strength 0.6 --saturation 1.15 --tile
"""
import argparse
import json
import pathlib
import sys

import numpy as np
from PIL import Image

PALETTE = pathlib.Path(__file__).resolve().parent.parent / "content" / "style" / "palette.json"
KMEANS_SEED = 9664
KMEANS_ITERATIONS = 16
KMEANS_SAMPLE = 20000
PAINT_OVERSAMPLE = 2


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def rgb_to_lab(rgb):
    """sRGB in 0..1 (..., 3) to CIE Lab (D65)."""
    lin = srgb_to_linear(rgb)
    m = np.array([[0.4124, 0.3576, 0.1805], [0.2126, 0.7152, 0.0722], [0.0193, 0.1192, 0.9505]])
    xyz = lin @ m.T / np.array([0.95047, 1.0, 1.08883])
    f = np.where(xyz > 0.008856, np.cbrt(xyz), 7.787 * xyz + 16 / 116)
    return np.stack([116 * f[..., 1] - 16, 500 * (f[..., 0] - f[..., 1]), 200 * (f[..., 1] - f[..., 2])], -1)


def hex_rgb(value):
    value = value.lstrip("#")
    return [int(value[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def box_mean(channel, radius, wrap):
    """Mean over a (2r+1)^2 window via an integral image; wrapped or edge-clamped."""
    mode = "wrap" if wrap else "edge"
    padded = np.pad(channel, ((radius + 1, radius), (radius + 1, radius)), mode=mode)
    integral = padded.cumsum(0).cumsum(1)
    size = 2 * radius + 1
    total = integral[size:, size:] - integral[:-size, size:] - integral[size:, :-size] + integral[:-size, :-size]
    return total / (size * size)


def kuwahara(rgb, radius, wrap):
    """Classic four-quadrant Kuwahara: each pixel takes the mean of its least-varied quadrant."""
    h, w, _ = rgb.shape
    luminance = rgb @ np.array([0.299, 0.587, 0.114])
    q = radius // 2 if radius > 1 else 1
    means, variances = [], []
    # Quadrant windows of half-size q centred at (+-q, +-q) offsets from the pixel.
    for dy in (-q, q):
        for dx in (-q, q):
            shifted = np.roll(rgb, (-dy, -dx), (0, 1)) if wrap else _shift_edge(rgb, dy, dx)
            lum = np.roll(luminance, (-dy, -dx), (0, 1)) if wrap else _shift_edge(luminance, dy, dx)
            mean = np.stack([box_mean(shifted[..., c], q, wrap) for c in range(3)], -1)
            variance = box_mean(lum * lum, q, wrap) - box_mean(lum, q, wrap) ** 2
            means.append(mean)
            variances.append(variance)
    choice = np.argmin(np.stack(variances), 0)
    stacked = np.stack(means)
    return np.take_along_axis(stacked, choice[None, ..., None].repeat(3, -1), 0)[0]


def _shift_edge(a, dy, dx):
    pad = [(abs(dy), abs(dy)), (abs(dx), abs(dx))] + [(0, 0)] * (a.ndim - 2)
    p = np.pad(a, pad, mode="edge")
    h, w = a.shape[:2]
    return p[abs(dy) + dy:abs(dy) + dy + h, abs(dx) + dx:abs(dx) + dx + w]


def kmeans(pixels, k):
    rng = np.random.default_rng(KMEANS_SEED)
    sample = pixels[rng.choice(len(pixels), min(KMEANS_SAMPLE, len(pixels)), replace=False)]
    centres = sample[rng.choice(len(sample), k, replace=False)].copy()
    for _ in range(KMEANS_ITERATIONS):
        labels = ((sample[:, None, :] - centres[None]) ** 2).sum(-1).argmin(1)
        for j in range(k):
            members = sample[labels == j]
            if len(members):
                centres[j] = members.mean(0)
    # Assign every pixel in slices to bound memory.
    out = np.empty(len(pixels), dtype=np.int64)
    for start in range(0, len(pixels), 65536):
        block = pixels[start:start + 65536]
        out[start:start + 65536] = ((block[:, None, :] - centres[None]) ** 2).sum(-1).argmin(1)
    return centres, out


def unsharp(rgb, amount, wrap):
    blurred = np.stack([box_mean(rgb[..., c], 1, wrap) for c in range(3)], -1)
    return rgb + amount * (rgb - blurred)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--size", type=int, default=256, help="output width; height keeps the aspect ratio")
    parser.add_argument("--paint", type=int, default=0, help="Kuwahara radius in output texels (0 = off)")
    parser.add_argument("--brightness", type=float, default=1.0)
    parser.add_argument("--contrast", type=float, default=1.0)
    parser.add_argument("--saturation", type=float, default=1.0)
    parser.add_argument("--pull", default="", help="comma-separated palette ramps (content/style/palette.json)")
    parser.add_argument("--pull-strength", type=float, default=0.5)
    parser.add_argument("--colors", type=int, default=0, help="k-means colour count (0 = off)")
    parser.add_argument("--group-strength", type=float, default=1.0)
    parser.add_argument("--sharpen", type=float, default=0.0)
    parser.add_argument("--tile", action="store_true", help="wrap neighbourhoods so a tileable input stays tileable")
    args = parser.parse_args()

    image = Image.open(args.source).convert("RGBA")
    height = max(1, round(image.height * args.size / image.width))
    if args.paint > 0:
        # Paint at twice the output size, then downsample: the quadrant filter's square
        # steps fall below a texel and the strokes stay.
        work = image.resize((args.size * PAINT_OVERSAMPLE, height * PAINT_OVERSAMPLE), Image.Resampling.LANCZOS)
        data = np.asarray(work, dtype=np.float64) / 255
        painted = kuwahara(data[..., :3], args.paint * PAINT_OVERSAMPLE, args.tile)
        merged = np.concatenate([painted, data[..., 3:]], -1)
        image = Image.fromarray((np.clip(merged, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")
    image = image.resize((args.size, height), Image.Resampling.LANCZOS)
    data = np.asarray(image, dtype=np.float64) / 255
    rgb, alpha = data[..., :3], data[..., 3:]

    rgb = rgb * args.brightness
    mean = rgb.mean((0, 1), keepdims=True)
    rgb = (rgb - mean) * args.contrast + mean
    grey = (rgb @ np.array([0.299, 0.587, 0.114]))[..., None]
    rgb = np.clip(grey + (rgb - grey) * args.saturation, 0, 1)

    if args.pull:
        ramps = json.loads(PALETTE.read_text())["ramps"]
        colours = np.array([hex_rgb(c) for name in args.pull.split(",") for c in ramps[name.strip()]])
        lab = rgb_to_lab(rgb)
        palette_lab = rgb_to_lab(colours)
        nearest = ((lab[..., None, :] - palette_lab) ** 2).sum(-1).argmin(-1)
        rgb = rgb + (colours[nearest] - rgb) * args.pull_strength

    if args.colors > 0:
        flat = rgb.reshape(-1, 3)
        centres, labels = kmeans(flat, args.colors)
        grouped = centres[labels].reshape(rgb.shape)
        rgb = rgb + (grouped - rgb) * args.group_strength

    if args.sharpen > 0:
        rgb = unsharp(rgb, args.sharpen, args.tile)

    out = np.concatenate([np.clip(rgb, 0, 1), alpha], -1)
    mode = "RGBA" if (alpha < 1).any() else "RGB"
    Image.fromarray((out * 255 + 0.5).astype(np.uint8)[..., :4 if mode == "RGBA" else 3], mode).save(args.output)
    print("stylise-texture " + " ".join(sys.argv[1:]))


if __name__ == "__main__":
    main()
