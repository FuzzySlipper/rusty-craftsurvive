"""Re-charts a fitted body for a small texture and bakes its generated texture into it (#10014).

usage: python3 art/characters/reuv.py <payload.npz> <basecolor.png> <out.npz> <out.png> \
    [--texels-per-metre 32] [--oversample 4] [--max-cost 4]

Needs the `xatlas` Python package (pip install xatlas, in a scratch virtual environment).

A generated atlas is cut into hundreds of small islands; reduced to the crunch rule's 32 texels
per metre (docs/visual-direction.md) it averages to colour noise. xatlas charts the welded body
into larger islands and packs them at the target density, one texel of padding apart. The
generated texture is then sampled through the original UVs at an oversampled resolution and box
reduced, so each texel is the mean colour of the surface it covers, and padding is dilated so
nearest sampling never reaches the background. out.npz is the payload with its body re-charted
(the new seams split vertices; each copies the payload vertex at its position).
"""
import argparse

import numpy as np
import xatlas
from PIL import Image

parser = argparse.ArgumentParser()
parser.add_argument("payload")
parser.add_argument("basecolor")
parser.add_argument("out_npz")
parser.add_argument("out_png")
parser.add_argument("--texels-per-metre", type=float, default=32)
parser.add_argument("--oversample", type=int, default=4)
parser.add_argument("--max-cost", type=float, default=4)
parser.add_argument("--double", help="also write the same atlas at twice the texel density (same UVs) here")
args = parser.parse_args()

payload = np.load(args.payload)
pos = payload["body_positions"].astype(np.float64)
old_uv = payload["body_uvs"].astype(np.float64)
tris = payload["body_indices"].reshape(-1, 3).astype(np.uint32)

# Weld the old UV seams so charts can span them; keep one payload vertex per welded vertex.
_, first, group = np.unique(np.round(pos / 1e-5).astype(np.int64), axis=0, return_index=True, return_inverse=True)
group = group.ravel()
welded = group[tris].astype(np.uint32)

atlas = xatlas.Atlas()
atlas.add_mesh(pos[first].astype(np.float32), welded)
chart = xatlas.ChartOptions()
chart.max_cost = args.max_cost
pack = xatlas.PackOptions()
pack.texels_per_unit = args.texels_per_metre
pack.padding = 1
pack.bilinear = False
pack.rotate_charts = True
atlas.generate(chart, pack)
vmapping, indices, uvs = atlas[0]
size = (atlas.width, atlas.height)
print("charts", atlas.chart_count, "atlas", size, "utilization", round(atlas.get_utilization(0), 3))

# xatlas keeps triangle order: corner k of new triangle t is corner k of old triangle t.
corner_old = tris.ravel()
new_to_payload = np.zeros(len(vmapping), np.int64)
new_to_payload[indices.ravel()] = corner_old  # any payload vertex at that welded position
remap = first[group[new_to_payload]]

# Bake: rasterise every new triangle at the oversampled size, sampling the old texture.
tex = np.asarray(Image.open(args.basecolor).convert("RGB"), np.float32)
th, tw = tex.shape[:2]
W, H = size[0] * args.oversample, size[1] * args.oversample
image = np.zeros((H, W, 3), np.float32)
known = np.zeros((H, W), bool)
ys, xs = np.mgrid[0:H, 0:W] + 0.5
for t in range(len(indices)):
    a, b, c = uvs[indices[t]] * [W, H]
    m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
    if abs(np.linalg.det(m)) < 1e-9:
        continue
    lo = np.floor(np.minimum(np.minimum(a, b), c)).astype(int).clip(0, [W - 1, H - 1])
    hi = np.ceil(np.maximum(np.maximum(a, b), c)).astype(int).clip(0, [W - 1, H - 1])
    sub = np.stack([xs[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1], ys[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]], -1)
    bary = np.einsum("ij,...j->...i", np.linalg.inv(m), sub - a)
    inside = (bary[..., 0] >= -0.02) & (bary[..., 1] >= -0.02) & (bary.sum(-1) <= 1.02)
    o = old_uv[corner_old[3 * t:3 * t + 3]]
    uv = o[0] + bary[..., :1] * (o[1] - o[0]) + bary[..., 1:] * (o[2] - o[0])
    sx = (uv[..., 0] * tw).astype(int).clip(0, tw - 1)
    sy = (uv[..., 1] * th).astype(int).clip(0, th - 1)
    region = image[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
    region[inside] = tex[sy, sx][inside]
    known[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1] |= inside



def reduce(k, width, height):
    """Mean of the covered samples in each texel (k x k samples), dilated into the padding."""
    cover = known.reshape(height, k, width, k).sum((1, 3))
    colour = (image * known[..., None]).reshape(height, k, width, k, 3).sum((1, 3)) / np.maximum(cover, 1)[..., None]
    filled = cover > 0
    while not filled.all():
        acc = np.zeros_like(colour)
        count = np.zeros(filled.shape)
        for dy in (-1, 0, 1):
            for dx in (-1, 0, 1):
                acc += np.roll(np.roll(colour * filled[..., None], dy, 0), dx, 1)
                count += np.roll(np.roll(filled, dy, 0), dx, 1)
        grow = (~filled) & (count > 0)
        colour[grow] = acc[grow] / count[grow][:, None]
        filled |= grow
    return Image.fromarray(np.concatenate([colour.clip(0, 255), np.full((*colour.shape[:2], 1), 255.0)], -1).astype(np.uint8), "RGBA")


reduce(args.oversample, size[0], size[1]).save(args.out_png)
if args.double:
    reduce(args.oversample // 2, size[0] * 2, size[1] * 2).save(args.double)
out = {key: payload[key] for key in payload.files if not key.startswith("body_")}
for name in ("positions", "normals", "joints", "weights"):
    out[f"body_{name}"] = payload[f"body_{name}"][remap]
out["body_uvs"] = uvs.astype(np.float32)
out["body_indices"] = indices.ravel().astype(np.uint32)
np.savez(args.out_npz, **out)
print("baked", args.out_png, size, len(vmapping), "vertices")
