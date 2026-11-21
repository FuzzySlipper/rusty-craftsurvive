"""Paints a prepared mesh from its reference sheet's views (#10956), for a generated mesh that came without paint (Tripo's
texture step can stall): each texel of the mesh's charts takes the colour its surface point shows in the views it faces,
the left and right side views for the flanks, the front and back for the ends, and both for what faces up.

usage: python3 -I authoring/tools/creatures/paint_from_views.py <body.glb> <views dir> <master.png> [--texels 128]

<body.glb> is prepare_mesh.py's output: charted, standing on the ground, facing glTF +Z (its left +X). <views dir> holds
cut_views.py's front.png, left.png, right.png and back.png. Each view's silhouette is found against its background and
the mesh's bounds are fitted to it, so the views need not share a scale with the mesh. The master is written at the
mesh's own chart size (body.json beside it), RGBA, for pixel_reduce.
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

# How far a pixel may stray from a view's background and still be background.
THRESHOLD = 24
# What faces up takes this much of both the side and the end views.
UP_SHARE = 0.5


def read_glb(path):
    data = Path(path).read_bytes()
    length = struct.unpack("<I", data[12:16])[0]
    doc = json.loads(data[20:20 + length])
    binary = data[20 + length + 8:]

    def accessor(index):
        acc = doc["accessors"][index]
        view = doc["bufferViews"][acc["bufferView"]]
        count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3}[acc["type"]]
        dtype = {5126: np.float32, 5123: np.uint16, 5125: np.uint32}[acc["componentType"]]
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        return np.frombuffer(binary, dtype=dtype, count=acc["count"] * count, offset=start).reshape(acc["count"], count)

    primitive = doc["meshes"][0]["primitives"][0]
    attributes = primitive["attributes"]
    return (accessor(attributes["POSITION"]).astype(float), accessor(attributes["NORMAL"]).astype(float),
            accessor(attributes["TEXCOORD_0"]).astype(float), accessor(primitive["indices"]).reshape(-1, 3))


def silhouette(image):
    """The view's pixels and the bounds of what is not its background: (array, x0, y0, x1, y1)."""
    pixels = np.asarray(image.convert("RGB")).astype(int)
    background = pixels[4, 4]
    ys, xs = np.nonzero(np.abs(pixels - background).sum(axis=2) > THRESHOLD)
    return pixels, xs.min(), ys.min(), xs.max(), ys.max()


def sampler(view, across, low, high):
    """Samples a view at mesh points: `across` is (axis, sign) along the image's x, up is the image's y, fitted bounds."""
    pixels, x0, y0, x1, y1 = view
    axis, sign = across

    def sample(points):
        share = (points[:, axis] - low[axis]) / (high[axis] - low[axis])
        if sign < 0:
            share = 1.0 - share
        rise = (points[:, 1] - low[1]) / (high[1] - low[1])
        x = np.clip(np.round(x0 + share * (x1 - x0)).astype(int), 0, pixels.shape[1] - 1)
        y = np.clip(np.round(y1 - rise * (y1 - y0)).astype(int), 0, pixels.shape[0] - 1)
        return pixels[y, x]

    return sample


def main(argv):
    positions, normals, uvs, triangles = read_glb(argv[0])
    views = Path(argv[1])
    out = Path(argv[2])
    body = json.loads((Path(argv[0]).parent / "body.json").read_text())
    width, height = body["masterTexels"]
    low, high = positions.min(axis=0), positions.max(axis=0)
    # glTF axes: x is the left side, y up, z forward. The left view looks at the left flank with the front to the
    # image's left; the right view is its mirror; the front view shows the left side on the image's right; the back
    # view shows it on the image's left.
    left = sampler(silhouette(Image.open(views / "left.png")), (2, -1), low, high)
    right = sampler(silhouette(Image.open(views / "right.png")), (2, 1), low, high)
    front = sampler(silhouette(Image.open(views / "front.png")), (0, 1), low, high)
    back = sampler(silhouette(Image.open(views / "back.png")), (0, -1), low, high)

    master = np.zeros((height, width, 4), dtype=np.uint8)
    for a, b, c in triangles:
        corners = uvs[[a, b, c]] * [width, height]
        x0, y0 = np.floor(corners.min(axis=0)).astype(int)
        x1, y1 = np.ceil(corners.max(axis=0)).astype(int)
        xs, ys = np.meshgrid(np.arange(max(0, x0 - 1), min(width, x1 + 2)), np.arange(max(0, y0 - 1), min(height, y1 + 2)))
        centres = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], axis=1)
        # Barycentric weights of each texel centre, kept a little outside the triangle so its edges are covered.
        v0, v1 = corners[1] - corners[0], corners[2] - corners[0]
        area = v0[0] * v1[1] - v0[1] * v1[0]
        if abs(area) < 1e-9:
            continue
        d = centres - corners[0]
        wb = (d[:, 0] * v1[1] - d[:, 1] * v1[0]) / area
        wc = (v0[0] * d[:, 1] - v0[1] * d[:, 0]) / area
        wa = 1.0 - wb - wc
        inside = (wa > -0.05) & (wb > -0.05) & (wc > -0.05)
        if not inside.any():
            continue
        w = np.stack([wa[inside], wb[inside], wc[inside]], axis=1)
        points = w @ positions[[a, b, c]]
        facing = w @ normals[[a, b, c]]
        facing /= np.maximum(np.linalg.norm(facing, axis=1, keepdims=True), 1e-9)
        up = np.maximum(facing[:, 1], 0.0) * UP_SHARE
        weights = np.stack([np.maximum(facing[:, 0], 0) + up, np.maximum(-facing[:, 0], 0) + up,
                            np.maximum(facing[:, 2], 0) + up, np.maximum(-facing[:, 2], 0) + up], axis=1)
        weights /= np.maximum(weights.sum(axis=1, keepdims=True), 1e-9)
        colour = (weights[:, [0]] * left(points) + weights[:, [1]] * right(points)
                  + weights[:, [2]] * front(points) + weights[:, [3]] * back(points))
        tx, ty = xs.ravel()[inside], ys.ravel()[inside]
        # glTF UVs run down from the image's top.
        master[ty, tx, :3] = np.clip(colour, 0, 255).astype(np.uint8)
        master[ty, tx, 3] = 255
    Image.fromarray(master, "RGBA").save(out)
    print(f"painted {int((master[..., 3] > 0).sum())} of {width * height} texels -> {out}")


if __name__ == "__main__":
    main(sys.argv[1:])
