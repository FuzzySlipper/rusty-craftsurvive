#!/usr/bin/env python3
"""Builds a level flying body straight from a flyer's reference sheet (#10631), without a generator.

usage: python3 -I authoring/tools/creatures/sheet_flyer.py <reference.png> <level.glb> [--step 3] [--wing 0.025]

A bird in the air is mostly a plan: its outline from below, with the belly on one side and the back on
the other. So the sheet's view from below gives the outline and the paint under it, the view from above
the paint over it, and the side view the body's depth and the line it follows. Neck, legs and wing edges
are rounded by how far they lie inside the outline, and wings are a thin plate. The sheet is laid out as
`cut_flyer_views.py` expects (reference-spec.txt). The result is in the frame `prepare_flyer.py` writes,
so it goes on to `prepare_mesh.py --head +x`. Take this route for a bird when Tripo can't be used.
"""
import io
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

THRESHOLD = 24
SIDE_SHARE = 0.32
STEP = 3
WING_METRES = 0.025
# The bird's length in the file's units: prepare_mesh.py scales it to the real one.
LENGTH = 2.5
# A body's width in the view from below, as a share of its depth in the side view.
BODY_WIDTH_SHARE = 1.25
TUBE_SHARE = 1.2


def option(argv, name, default, kind):
    return kind(argv[argv.index(name) + 1]) if name in argv else default


def outline(image, background):
    """The view's mask with its holes filled, and the box around it."""
    difference = np.abs(np.asarray(image, dtype=int) - np.array(background)).sum(axis=2)
    mask = ndimage.binary_fill_holes(ndimage.binary_opening(difference > THRESHOLD))
    labels, count = ndimage.label(mask)
    if count > 1:
        mask = labels == 1 + np.argmax(ndimage.sum(mask, labels, range(1, count + 1)))
    rows, cols = np.where(mask)
    return mask, (cols.min(), rows.min(), cols.max() + 1, rows.max() + 1)


def bled(image, mask):
    """The paint carried out past the outline, so no background shows at the rim."""
    inner = ndimage.binary_erosion(mask, iterations=2)
    indices = ndimage.distance_transform_edt(~inner, return_distances=False, return_indices=True)
    return Image.fromarray(np.asarray(image)[indices[0], indices[1]])


def side_profile(sheet, background, rows):
    """Per row of the plan, the side view's centre line and half depth, as shares of the bird's length."""
    width, height = sheet.size
    part = sheet.crop((0, 0, width, int(height * SIDE_SHARE)))
    mask, (left, top, right, bottom) = outline(part, background)
    mask = mask[top:bottom, left:right]
    length = right - left
    columns = np.clip((np.arange(rows) + 0.5) / rows * length, 0, length - 1).astype(int)
    centre, half = np.zeros(rows), np.zeros(rows)
    for row, column in enumerate(columns):
        filled = np.where(mask[:, column])[0]
        if len(filled):
            centre[row] = (filled.min() + filled.max()) / 2
            half[row] = (filled.max() - filled.min() + 1) / 2
    middle = np.median(centre[centre > 0])
    return (middle - centre) / length, half / length


def surface(mask, profile, step, wing_metres):
    """The heights of the corners of a grid over the plan: where the bird's middle lies, and how far out from it."""
    rows, cols = mask.shape
    metres_per_pixel = LENGTH / rows
    centre, half = profile
    distance = ndimage.distance_transform_edt(mask) * metres_per_pixel
    middle = cols / 2
    grid_rows, grid_cols = np.arange(0, rows, step), np.arange(0, cols, step)
    r, c = np.meshgrid(grid_rows, grid_cols, indexing="ij")
    inside = mask[r, c]
    across = np.abs(c - middle) * metres_per_pixel
    row_half = half[r] * LENGTH
    body = row_half * BODY_WIDTH_SHARE
    ellipse = row_half * np.sqrt(np.clip(1 - (across / np.maximum(body, 1e-6)) ** 2, 0, 1))
    tube = distance[r, c] * TUBE_SHARE
    thick = np.where(across < body, np.minimum(tube, np.maximum(ellipse, wing_metres)), np.minimum(tube, wing_metres))
    return r, c, inside, centre[r] * LENGTH, thick


def build(mask, centre, thick, r, c, inside, cols, rows):
    """Two sheets sharing their rim: positions in glTF's frame, uvs into the atlas, and triangles."""
    metres_per_pixel = LENGTH / rows
    neighbours = ndimage.binary_erosion(inside, structure=np.ones((3, 3)))
    thick = np.where(neighbours, thick, 0.0)
    positions, uvs, triangles = [], [], []
    for sign in (-1, 1):
        index = -np.ones(inside.shape, dtype=int)
        for i, j in zip(*np.where(inside)):
            index[i, j] = len(positions)
            forward = (rows / 2 - r[i, j]) * metres_per_pixel
            side = (c[i, j] - cols / 2) * metres_per_pixel
            up = centre[i, j] + sign * thick[i, j]
            positions.append((forward, up, -side))
            u = c[i, j] / cols
            # The atlas holds the view from below on its left and from above, mirrored into place, on its right.
            uvs.append(((u if sign < 0 else 2 - u) / 2, r[i, j] / rows))
        for i, j in zip(*np.where(inside[:-1, :-1] & inside[1:, :-1] & inside[:-1, 1:] & inside[1:, 1:])):
            a, b, d, e = index[i, j], index[i + 1, j], index[i, j + 1], index[i + 1, j + 1]
            triangles += [(a, d, b), (d, e, b)] if sign > 0 else [(a, b, d), (d, b, e)]
    return np.array(positions, np.float32), np.array(uvs, np.float32), np.array(triangles, np.uint32)


def write_glb(path, positions, uvs, triangles, atlas):
    png = io.BytesIO()
    atlas.save(png, "PNG")
    blobs = [positions.tobytes(), uvs.tobytes(), triangles.tobytes(), png.getvalue()]
    views, offset = [], 0
    for blob in blobs:
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(blob)})
        offset += (len(blob) + 3) // 4 * 4
    binary = b"".join(blob + b"\0" * (-len(blob) % 4) for blob in blobs)
    document = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "body"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1}, "indices": 2, "material": 0}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0, "roughnessFactor": 1}}],
        "textures": [{"source": 0}],
        "images": [{"bufferView": 3, "mimeType": "image/png"}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": views,
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3", "min": positions.min(axis=0).tolist(), "max": positions.max(axis=0).tolist()},
            {"bufferView": 1, "componentType": 5126, "count": len(uvs), "type": "VEC2"},
            {"bufferView": 2, "componentType": 5125, "count": triangles.size, "type": "SCALAR"},
        ],
    }
    text = json.dumps(document, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(text), 0x4E4F534A) + text + struct.pack("<II", len(binary), 0x004E4942) + binary)


def main():
    argv = sys.argv[1:]
    sheet = Image.open(argv[0]).convert("RGB")
    background = sheet.getpixel((4, 4))
    width, height = sheet.size
    split = int(height * SIDE_SHARE)
    below = sheet.crop((0, split, width // 2, height))
    above = sheet.crop((width // 2, split, width, height))
    below_mask, below_box = outline(below, background)
    above_mask, above_box = outline(above, background)
    plan = below_mask[below_box[1]:below_box[3], below_box[0]:below_box[2]]
    rows, cols = plan.shape
    under = bled(below, below_mask).crop(below_box)
    over = bled(above, above_mask).crop(above_box).resize((cols, rows), Image.LANCZOS)
    atlas = Image.new("RGB", (2 * cols, rows))
    atlas.paste(under, (0, 0))
    atlas.paste(over.transpose(Image.FLIP_LEFT_RIGHT), (cols, 0))

    profile = side_profile(sheet, background, rows)
    r, c, inside, centre, thick = surface(plan, profile, option(argv, "--step", STEP, int), option(argv, "--wing", WING_METRES, float))
    positions, uvs, triangles = build(plan, centre, thick, r, c, inside, cols, rows)
    write_glb(argv[1], positions, uvs, triangles, atlas)
    print(f"{len(positions)} vertices, {len(triangles)} triangles, {cols}x{rows} plan, depth {2 * profile[1].max() * LENGTH:.2f} m")


if __name__ == "__main__":
    main()
