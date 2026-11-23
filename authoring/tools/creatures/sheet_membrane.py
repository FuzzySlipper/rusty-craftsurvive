#!/usr/bin/env python3
"""Builds a membrane body straight from a side view (#10986, the fade-ribbon), without a generator.

usage: python3 -I authoring/tools/creatures/sheet_membrane.py <side.png> <generated.glb>
           [--length 5] [--head-share 0.2] [--head-metres 0.09] [--sheet-metres 0.024] [--step 4]
           [--fill --tail-share 0.65] [--rim 7a4fb0 --rim-edge 4e2f7a --rim-metres 0.05 --rim-fade 0.07]

A broad, thin swimmer seen from the side is its outline: a sail-thin membrane behind a narrower, rounder head. The view
(head on the left, on a plain backdrop) is straightened first: each column of the outline is moved up or down so its
middle lies on one line, so a body drawn swimming in a wave rests straight, as the rig expects, and keeps the depth the
sheet gave it at each point along it. Each face is then a grid over the outline, rounded by how far it lies inside it,
swelling to --head-metres across over the front --head-share of the length and easing to --sheet-metres across behind
it. Both faces take their paint from the view. The result is --length metres long with its head along glTF +x, so it
goes on to `prepare_mesh.py --head +x`. Take this route when a body is mostly an outline that Tripo would round out.

- `--fill`: where the sheet draws the membrane turning edge-on (a twist reads as a waist in a side view), the depth behind
  the head is taken from the outline's upper envelope, tapering to --tail-share of its broadest at the trailing end,
  each column's paint stretched to it.
- `--rim`: the membrane behind the head gets a margin of that colour --rim-metres deep along both long edges, fading
  into the sheet's paint over --rim-fade, with an edge line of --rim-edge one game texel deep: a drawn rim too fine for
  64 texels/m survives the reduction. The sheet's shading is kept within it.
"""
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheet_flyer import write_glb  # noqa: E402

THRESHOLD = 30
LENGTH = 5.0
STEP = 4
HEAD_SHARE = 0.2
HEAD_METRES = 0.09
SHEET_METRES = 0.024
# Over this share of the length behind the head, the thickness eases from the head's to the sheet's.
EASE_SHARE = 0.12
# How fast the faces swell inside the outline: the half thickness grows this many metres per metre inward.
SWELL = 0.8
# The two faces stay this far apart at the rim, so their rim points keep their own normals.
RIM_METRES = 0.002
# Filling a twist: the envelope eases in over this share of the length behind the head, is smoothed over this share,
# and tapers to this share of its broadest by the trailing end.
FILL_EASE_SHARE = 0.1
FILL_SMOOTH_SHARE = 0.015
TAIL_SHARE = 0.65
# A filled membrane's paint is calmed along its length over this many metres.
CALM_METRES = 0.35
# The game's texels a metre: the rim's edge line is one of them deep.
TEXELS_PER_METRE = 64
# How much of each pixel's own lightness against its column's the rim keeps, so the sheet's folds still show through.
SHADE_KEPT = 0.7


def option(argv, name, default, kind):
    return kind(argv[argv.index(name) + 1]) if name in argv else default


def outline(image):
    """The view's mask, its largest piece with its holes filled, against the backdrop in its corners."""
    pixels = np.asarray(image, dtype=int)
    backdrop = np.median(np.concatenate([pixels[:4, :4].reshape(-1, 3), pixels[:4, -4:].reshape(-1, 3)]), axis=0)
    mask = ndimage.binary_fill_holes(ndimage.binary_opening(np.abs(pixels - backdrop).sum(axis=2) > THRESHOLD, iterations=2))
    labels, count = ndimage.label(mask)
    if count > 1:
        mask = labels == 1 + np.argmax(ndimage.sum(mask, labels, range(1, count + 1)))
    return mask


def hex_colour(text):
    return np.array([int(text[i:i + 2], 16) for i in (0, 2, 4)], dtype=float)


def upper_envelope(values):
    """The least concave curve over a profile: a twist's dip is bridged by the line between its neighbours."""
    hull = []
    for x, y in enumerate(values):
        while len(hull) >= 2 and (hull[-1][0] - hull[-2][0]) * (y - hull[-2][1]) - (x - hull[-2][0]) * (hull[-1][1] - hull[-2][1]) >= 0:
            hull.pop()
        hull.append((x, y))
    xs, ys = zip(*hull)
    return np.interp(np.arange(len(values)), xs, ys)


def filled(paint, mask, head_share, tail_share):
    """Each column behind the head stretched about its middle to the outline's upper envelope, which tapers from the
    broadest column to --tail-share of it at the trailing end, eased in from the head."""
    pixels = np.asarray(paint)
    height, cols = mask.shape
    half = np.array([(np.ptp(np.nonzero(mask[:, c])[0]) + 1) / 2 if mask[:, c].any() else 0 for c in range(cols)], float)
    start = int(head_share * cols)
    broadest = start + int(np.argmax(half[start:]))
    taper = np.ones(cols)
    taper[broadest:] = 1 + (tail_share - 1) * np.linspace(0, 1, cols - broadest)
    wanted = np.maximum(half, upper_envelope(half) * taper)
    ease = np.clip((np.arange(cols) - start) / (FILL_EASE_SHARE * cols), 0, 1)
    target = ndimage.gaussian_filter1d(half + (wanted - half) * ease * ease * (3 - 2 * ease), FILL_SMOOTH_SHARE * cols)
    target = np.where(np.arange(cols) < start, half, np.maximum(target, half))
    grow = int(np.ceil(target.max() * 2)) + 8
    out_mask = np.zeros((grow, cols), bool)
    out = np.zeros((grow, cols, 3), np.uint8)
    rows = np.arange(grow) - grow / 2
    for c in range(cols):
        scale = half[c] / target[c] if target[c] > 0 else 1.0
        source = np.clip(np.round(height / 2 + rows * scale).astype(int), 0, height - 1)
        out[:, c] = pixels[source, c]
        out_mask[:, c] = np.abs(rows) <= target[c]
    return Image.fromarray(out), out_mask


def calmed(paint, mask, metres, head_share):
    """The membrane behind the head smoothed along its length (a running median over --calm-metres): where the sheet drew
    a twist, its edge-on band and shadow would show as a dark blot across the stretched membrane; the long folds stay."""
    pixels = np.asarray(paint)
    width = max(3, int(round(CALM_METRES / metres)) | 1)
    smooth = ndimage.median_filter(pixels, size=(1, width, 1), mode="nearest")
    along = np.arange(mask.shape[1]) / mask.shape[1]
    behind = np.clip((along - head_share) / 0.05, 0, 1)[None, :, None]
    return Image.fromarray((pixels * (1 - behind) + smooth * behind).astype(np.uint8))


def rimmed(paint, mask, metres, head_share, rim, edge, rim_metres, fade_metres):
    """The membrane's margins behind the head painted in the rim's colour, keeping the sheet's shading, with a darker
    edge line one game texel deep."""
    pixels = np.asarray(paint).astype(float)
    inside = ndimage.distance_transform_edt(mask) * metres
    lightness = pixels.mean(axis=2, keepdims=True)
    column_mean = np.array([lightness[mask[:, c], c].mean() if mask[:, c].any() else 128.0 for c in range(mask.shape[1])])
    shade = 1 + SHADE_KEPT * (lightness[..., 0] / column_mean[None, :] - 1)
    share = np.clip(1 - (inside - rim_metres) / fade_metres, 0, 1)
    along = np.arange(mask.shape[1]) / mask.shape[1]
    behind = np.clip((along - head_share) / 0.05, 0, 1)[None, :]
    share = share * behind
    colour = np.where((inside < 1 / TEXELS_PER_METRE)[..., None], edge, rim) * shade[..., None]
    pixels = pixels * (1 - share[..., None]) + colour * share[..., None]
    return Image.fromarray(np.clip(pixels, 0, 255).astype(np.uint8))


def straightened(image, mask):
    """The view and its mask with each column moved so the outline's middle lies on one row; the paint is carried out
    past the outline first, so no backdrop shows at the rim."""
    pixels = np.asarray(image)
    inner = ndimage.binary_erosion(mask, iterations=2)
    near = ndimage.distance_transform_edt(~inner, return_distances=False, return_indices=True)
    pixels = pixels[near[0], near[1]]
    rows, cols = mask.shape
    filled = [np.nonzero(mask[:, c])[0] for c in range(cols)]
    used = [c for c in range(cols) if len(filled[c])]
    left, right = min(used), max(used) + 1
    depth = max(filled[c].max() - filled[c].min() + 1 for c in used)
    height = depth + 8
    flat_mask = np.zeros((height, right - left), bool)
    flat = np.zeros((height, right - left, 3), np.uint8)
    for c in range(left, right):
        rows_in = filled[c]
        middle = (rows_in.min() + rows_in.max()) / 2 if len(rows_in) else rows / 2
        shift = int(round(middle - height / 2))
        source = np.clip(np.arange(height) + shift, 0, rows - 1)
        flat[:, c - left] = pixels[source, c]
        flat_mask[:, c - left] = mask[source, c] & (np.arange(height) + shift >= 0) & (np.arange(height) + shift < rows)
    return Image.fromarray(flat), ndimage.binary_fill_holes(flat_mask)


def build(mask, step, length, head_share, head_metres, sheet_metres):
    """Two faces over the outline: positions in glTF's frame (+x the head, +y up, +z the left side), uvs into the
    straightened view, and triangles."""
    rows, cols = mask.shape
    metres = length / cols
    distance = ndimage.distance_transform_edt(mask) * metres
    grid_r, grid_c = np.arange(0, rows, step), np.arange(0, cols, step)
    r, c = np.meshgrid(grid_r, grid_c, indexing="ij")
    inside = mask[r, c]
    # The cap on its half thickness: the head's over its share of the length, easing to the sheet's behind it.
    along = c / cols
    ease = np.clip((along - head_share) / EASE_SHARE, 0, 1)
    ease = ease * ease * (3 - 2 * ease)
    cap = (head_metres + (sheet_metres - head_metres) * ease) / 2
    half = np.maximum(np.minimum(distance[r, c] * SWELL, cap), RIM_METRES / 2)
    middle_row = rows / 2
    positions, uvs, triangles = [], [], []
    for sign in (1, -1):
        index = -np.ones(inside.shape, dtype=int)
        for i, j in zip(*np.where(inside)):
            index[i, j] = len(positions)
            positions.append(((cols / 2 - c[i, j]) * metres, (middle_row - r[i, j]) * metres, sign * half[i, j]))
            uvs.append((c[i, j] / cols, r[i, j] / rows))
        for i, j in zip(*np.where(inside[:-1, :-1] & inside[1:, :-1] & inside[:-1, 1:] & inside[1:, 1:])):
            a, b, d, e = index[i, j], index[i + 1, j], index[i, j + 1], index[i + 1, j + 1]
            # Each face's triangles wind outward, toward its own side.
            triangles += [(a, d, b), (d, e, b)] if sign > 0 else [(a, b, d), (d, b, e)]
    positions = np.array(positions, np.float32)
    positions[:, 1] -= positions[:, 1].min()
    return positions, np.array(uvs, np.float32), np.array(triangles, np.uint32), metres


def main():
    argv = sys.argv[1:]
    view = Image.open(argv[0]).convert("RGB")
    mask = outline(view)
    paint, flat = straightened(view, mask)
    length = option(argv, "--length", LENGTH, float)
    head_share = option(argv, "--head-share", HEAD_SHARE, float)
    if "--fill" in argv:
        paint, flat = filled(paint, flat, head_share, option(argv, "--tail-share", TAIL_SHARE, float))
        paint = calmed(paint, flat, length / flat.shape[1], head_share)
    if "--rim" in argv:
        paint = rimmed(paint, flat, length / flat.shape[1], head_share, hex_colour(option(argv, "--rim", "7a4fb0", str)),
                       hex_colour(option(argv, "--rim-edge", "4e2f7a", str)), option(argv, "--rim-metres", 0.05, float),
                       option(argv, "--rim-fade", 0.07, float))
    positions, uvs, triangles, metres = build(flat, option(argv, "--step", STEP, int), length,
                                              head_share,
                                              option(argv, "--head-metres", HEAD_METRES, float),
                                              option(argv, "--sheet-metres", SHEET_METRES, float))
    write_glb(argv[1], positions, uvs, triangles, paint)
    paint.save(Path(argv[1]).with_suffix(".view.png"))
    depth = flat.sum(axis=0).max() * metres
    print(f"{len(positions)} vertices, {len(triangles)} triangles, {flat.shape[1]}x{flat.shape[0]} view, "
          f"{length:.2f} m long, {depth:.2f} m deep at most, {metres * 1000:.1f} mm a pixel")


if __name__ == "__main__":
    main()
