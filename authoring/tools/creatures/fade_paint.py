"""Paints a fog foe's fade into its texture's alpha (#10984): solid at the head, thinning to the tail, for a masked
material whose per-creature alpha then sweeps it solid from the head back.

usage: python3 -I authoring/tools/creatures/fade_paint.py <body.glb> <out.png> [--chain head,neck,body14,...]
       [--cutoff 0.25] [--crumble 0.12] [--seed 10984]

Each texel's place along the body is read from the skin: the joints its triangles' points are weighted to, ranked
along the chain from the head (1) to the tail (0), so a coiled or looping rest pose reads the same as a straight one.
Its alpha is then the cutoff plus the rest of the range by that rank, with a little noise per texel so the edge
crumbles in whole texels rather than sliding as a clean line. The tail's texels stay just over the cutoff, so a
creature at full alpha draws whole.

The material draws a texel while its alpha times the creature's own alpha is at least the cutoff: at 1 the whole body,
lower only the texels nearer the head, and under the cutoff nothing. The colour is the GLB's own paint, unchanged.
Written as an RGBA PNG (the Engine admits RGBA PNGs only).
"""
import io
import json
import struct
import sys

import numpy as np
from PIL import Image

COMPONENTS = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}
TYPES = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
# How far over the cutoff the tail's texels stay, so noise never drops them out of a whole body.
TAIL_MARGIN = 0.02


def option(argv, name, default):
    return argv[argv.index(name) + 1] if name in argv else default


def read_glb(path):
    data = open(path, "rb").read()
    length = struct.unpack_from("<I", data, 12)[0]
    gltf = json.loads(data[20:20 + length])
    binary = data[20 + length + 8:]
    return gltf, binary


def accessor(gltf, binary, index):
    acc = gltf["accessors"][index]
    view = gltf["bufferViews"][acc["bufferView"]]
    count, width = acc["count"], COMPONENTS[acc["type"]]
    dtype = np.dtype(TYPES[acc["componentType"]])
    start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = view.get("byteStride", dtype.itemsize * width)
    rows = np.ndarray((count, width), dtype=dtype, buffer=binary, offset=start, strides=(stride, dtype.itemsize))
    return np.array(rows)


def ranks(gltf, chain):
    """Each skin joint's place from the head (1) to the tail (0); joints off the chain take their parent's."""
    names = [gltf["nodes"][i].get("name", "") for i in gltf["skins"][0]["joints"]]
    place = {name: 1 - (n / (len(chain) - 1)) for n, name in enumerate(chain)}
    parent = {child: node.get("name", "") for node in gltf["nodes"] for child in
              (gltf["nodes"][c].get("name", "") for c in node.get("children", []))}
    out = []
    for name in names:
        seen = name
        while seen not in place and seen in parent:
            seen = parent[seen]
        out.append(place.get(seen, 0.0))
    return np.array(out)


def main(argv):
    gltf, binary = read_glb(argv[0])
    cutoff = float(option(argv, "--cutoff", "0.25"))
    crumble = float(option(argv, "--crumble", "0.12"))
    rng = np.random.default_rng(int(option(argv, "--seed", "10984")))
    names = [gltf["nodes"][i].get("name", "") for i in gltf["skins"][0]["joints"]]
    # The default chain is the serpent family's, head first: its body bones run from the root at the tail.
    chain = option(argv, "--chain", ",".join(["head", "neck"] + [n for n in reversed(names) if n.startswith("body")] + ["root"])).split(",")
    rank = ranks(gltf, chain)

    primitive = gltf["meshes"][0]["primitives"][0]
    attributes = primitive["attributes"]
    uvs = accessor(gltf, binary, attributes["TEXCOORD_0"])
    joints = accessor(gltf, binary, attributes["JOINTS_0"]).astype(int)
    weights = accessor(gltf, binary, attributes["WEIGHTS_0"]).astype(float)
    triangles = accessor(gltf, binary, primitive["indices"]).reshape(-1, 3)
    along = (rank[joints] * weights).sum(axis=1) / np.maximum(weights.sum(axis=1), 1e-9)

    material = gltf["materials"][primitive["material"]]
    image_index = gltf["textures"][material["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"]
    view = gltf["bufferViews"][gltf["images"][image_index]["bufferView"]]
    start = view.get("byteOffset", 0)
    paint = Image.open(io.BytesIO(binary[start:start + view["byteLength"]])).convert("RGBA")
    width, height = paint.size

    # Each texel takes its place along the body from the triangle its centre falls in; texels no triangle covers keep
    # the nearest covered texel's (the charts' bleed), so filtering at a chart's edge never reads a hole.
    place = np.full((height, width), np.nan)
    for a, b, c in triangles:
        uv = uvs[[a, b, c]] * (width, height)
        low, high = np.floor(uv.min(axis=0)).astype(int), np.ceil(uv.max(axis=0)).astype(int)
        xs, ys = np.meshgrid(np.arange(max(low[0], 0), min(high[0], width)), np.arange(max(low[1], 0), min(high[1], height)))
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
        values = along[a] * (1 - u - v) + along[b] * u + along[c] * v
        place[ys.ravel()[inside], xs.ravel()[inside]] = values[inside]
    covered = ~np.isnan(place)
    if not covered.all():
        from scipy import ndimage  # noqa: PLC0415
        _, (iy, ix) = ndimage.distance_transform_edt(~covered, return_indices=True)
        place = place[iy, ix]

    noise = rng.uniform(-crumble, crumble, place.shape)
    alpha = cutoff + TAIL_MARGIN + (1 - cutoff - TAIL_MARGIN) * np.clip(place + noise, 0, 1)
    rgba = np.array(paint)
    rgba[..., 3] = np.round(np.clip(alpha, 0, 1) * 255).astype(np.uint8)
    Image.fromarray(rgba, "RGBA").save(argv[1])
    print(f"{argv[1]}: {width}x{height}, chain {chain[0]}..{chain[-1]}, cutoff {cutoff}, crumble {crumble}, "
          f"covered {covered.mean():.0%}")


if __name__ == "__main__":
    main(sys.argv[1:])
