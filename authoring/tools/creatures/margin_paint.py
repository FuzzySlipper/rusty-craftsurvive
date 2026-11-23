"""Paints a ribbon body's margins into its master texture (#10985, the needle-mouth): a colour along the top edge of the
back and the lower edge of the belly, fading into the flanks, where the generator painted the whole body one tint.

usage: python3 -I authoring/tools/creatures/margin_paint.py <body.glb> <master.png> <out.png>
       [--colour 7a4fb0] [--flank e4ddd6] [--from 0.35] [--to 0.8] [--flank-share 0.45] [--keep-ahead <metres>]
       [--head-from <metres> --keep-light 0.8 --keep-dark 0.3]

Each texel's surface normal is read from the prepared body (`prepare_mesh.py`'s body.glb, whose charts the master
fills): a texel facing up or down by more than --from (the normal's vertical part) starts to take --colour, fully at
--to, and the flanks between are drawn --flank-share of the way to --flank, so the pearl and the violet part. Texels
ahead of --keep-ahead metres (the body's forward is -Y in Blender, glTF +Z) keep the generator's paint: the head's
own marks. Ahead of --head-from, only the texels whose master lightness lies between --keep-dark and --keep-light
are repainted: the teeth and the eye, slits and mouth keep theirs. The paint's lightness is kept within each texel's colour, so the generator's shading and marks survive.
"""
import io
import json
import struct
import sys

import numpy as np
from PIL import Image

COMPONENTS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
TYPES = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
# How much of each texel's own lightness against the master's mean the new colour keeps.
SHADE_KEPT = 0.8


def option(argv, name, default):
    return argv[argv.index(name) + 1] if name in argv else default


def hex_colour(text):
    return np.array([int(text[i:i + 2], 16) for i in (0, 2, 4)], dtype=float)


def read_glb(path):
    data = open(path, "rb").read()
    length = struct.unpack_from("<I", data, 12)[0]
    return json.loads(data[20:20 + length]), data[20 + length + 8:]


def accessor(gltf, binary, index):
    acc = gltf["accessors"][index]
    view = gltf["bufferViews"][acc["bufferView"]]
    count, width = acc["count"], COMPONENTS[acc["type"]]
    dtype = np.dtype(TYPES[acc["componentType"]])
    start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = view.get("byteStride", dtype.itemsize * width)
    return np.array(np.ndarray((count, width), dtype=dtype, buffer=binary, offset=start, strides=(stride, dtype.itemsize)))


def smoothstep(low, high, x):
    t = np.clip((x - low) / max(high - low, 1e-9), 0, 1)
    return t * t * (3 - 2 * t)


def main(argv):
    gltf, binary = read_glb(argv[0])
    master = np.array(Image.open(argv[1]).convert("RGBA")).astype(float)
    colour = hex_colour(option(argv, "--colour", "7a4fb0"))
    flank = hex_colour(option(argv, "--flank", "e4ddd6"))
    low, high = float(option(argv, "--from", "0.35")), float(option(argv, "--to", "0.8"))
    flank_share = float(option(argv, "--flank-share", "0.45"))
    keep_ahead = float(option(argv, "--keep-ahead", "inf"))
    head_from = float(option(argv, "--head-from", "inf"))
    keep_light, keep_dark = float(option(argv, "--keep-light", "0.8")), float(option(argv, "--keep-dark", "0.3"))

    primitive = gltf["meshes"][0]["primitives"][0]
    attributes = primitive["attributes"]
    uvs = accessor(gltf, binary, attributes["TEXCOORD_0"])
    normals = accessor(gltf, binary, attributes["NORMAL"])
    positions = accessor(gltf, binary, attributes["POSITION"])
    triangles = accessor(gltf, binary, primitive["indices"]).reshape(-1, 3)
    height, width = master.shape[:2]

    # Each covered texel's normal's vertical part (glTF +Y is up) and how far forward it lies (glTF +Z).
    up = np.full((height, width), np.nan)
    ahead = np.full((height, width), np.nan)
    for a, b, c in triangles:
        uv = uvs[[a, b, c]] * (width, height)
        lo, hi = np.floor(uv.min(axis=0)).astype(int), np.ceil(uv.max(axis=0)).astype(int)
        xs, ys = np.meshgrid(np.arange(max(lo[0], 0), min(hi[0], width)), np.arange(max(lo[1], 0), min(hi[1], height)))
        if xs.size == 0:
            continue
        points = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], axis=1)
        d = uv[1:] - uv[0]
        det = d[0, 0] * d[1, 1] - d[0, 1] * d[1, 0]
        if abs(det) < 1e-12:
            continue
        rel = points - uv[0]
        u = (rel[:, 0] * d[1, 1] - rel[:, 1] * d[1, 0]) / det
        v = (rel[:, 1] * d[0, 0] - rel[:, 0] * d[0, 1]) / det
        inside = (u >= -1e-6) & (v >= -1e-6) & (u + v <= 1 + 1e-6)
        weights = np.stack([1 - u - v, u, v], axis=1)[inside]
        normal = weights @ normals[[a, b, c]]
        normal /= np.maximum(np.linalg.norm(normal, axis=1, keepdims=True), 1e-9)
        up[ys.ravel()[inside], xs.ravel()[inside]] = normal[:, 1]
        ahead[ys.ravel()[inside], xs.ravel()[inside]] = (weights @ positions[[a, b, c]])[:, 2]

    covered = ~np.isnan(up) & (ahead < keep_ahead)
    own = master[..., :3].mean(axis=2) / 255
    covered &= ~((ahead >= head_from) & ((own > keep_light) | (own < keep_dark)))
    margin = smoothstep(low, high, np.abs(np.nan_to_num(up)))
    rgb = master[..., :3]
    lightness = rgb.mean(axis=2, keepdims=True)
    mean = lightness[covered].mean()
    # The new colour carries the texel's own shading: its lightness against the master's mean.
    shade = 1 + SHADE_KEPT * (lightness - mean) / max(mean, 1)
    target = margin[..., None] * colour + (1 - margin[..., None]) * (flank_share * flank + (1 - flank_share) * rgb / shade)
    painted = np.clip(target * shade, 0, 255)
    rgb[covered] = painted[covered]
    # The bleed round each chart (prepare_mesh's padding) takes its nearest painted texel again, so reduction and
    # filtering at a chart's edge never read the old paint.
    inside = ~np.isnan(up)
    from scipy import ndimage  # noqa: PLC0415
    _, (iy, ix) = ndimage.distance_transform_edt(~inside, return_indices=True)
    bleed = ~inside & (master[..., :3].sum(axis=2) > 0)
    rgb[bleed] = rgb[iy, ix][bleed]
    master[..., :3] = rgb
    Image.fromarray(master.astype(np.uint8), "RGBA").save(argv[2])
    print(f"{argv[2]}: {width}x{height}, {covered.mean():.0%} of texels repainted, margin share "
          f"{(margin[covered] > 0.5).mean():.0%}")


if __name__ == "__main__":
    main(sys.argv[1:])
