"""Defines the shared face slot on MakeHuman's head, once, from the neutral body (#10028).

usage: python3 -I art/characters/mpfb/face_layout.py <neutral.npy> <out face_layout.json> <out reference.png>

Every MPFB head is the same topology, so the face slot is a fixed set of base triangles with a
fixed per-vertex UV (a front projection of the neutral, fitted head, anchored at the nose tip).
Each character's face texture is painted to this one layout, and painted features follow their
vertices when a recipe moves the eyes, nose or jaw.

MakeHuman's eye sockets are closed surfaces (the eyeballs are helpers, not drawn); their recessed
faces, the nostrils and the lip line always join the face slot, so the painted eyes and mouth are
the only ones. The reference image is the neutral face in this layout,
shaded by its normals, for painting face sets against.
"""
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
from basemesh import BaseMesh, to_gltf  # noqa: E402
from fit import fit  # noqa: E402
from fit_to_ual import front_visible  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
RIG = ROOT / "content/animations/UAL1_Standard.glb"
HEAD_SCALE = 1.25
# Metres about the nose tip on the neutral fitted head (head scale 1.25).
BOX = {"halfWidth": 0.095, "low": -0.07, "high": 0.15}
SIZE = 512


def main(neutral, out_json, out_png):
    base = BaseMesh()
    final, top, top_w, dense, index, target, order, facts = fit(base, to_gltf(np.load(neutral)), RIG, HEAD_SCALE, 1.3, 1.25)
    # fit() scales the body to UAL's hip height first; the box (authored in unscaled metres) follows,
    # so the layout and the faces painted to it stay registered.
    box = {k: (v * facts["prescale"] if k != "minForward" else v) for k, v in BOX.items()}
    tri_v, _, _ = base.obj
    body = base.faces_in("body")
    head_w = dense[:, index["head"]]
    near = (head_w > 0.9) & (np.arange(len(final)) < 13380) & (np.abs(final[:, 0]) < 0.03)
    nose = final[near][np.argmax(final[near][:, 2])]
    rect = (nose[0] - box["halfWidth"], nose[0] + box["halfWidth"], nose[1] + box["low"], nose[1] + box["high"])
    centroid = final[tri_v].mean(1)
    candidate = body & (head_w[tri_v].min(1) > 0.6) & (np.abs(centroid[:, 0] - nose[0]) < box["halfWidth"]) \
        & (centroid[:, 1] > rect[2]) & (centroid[:, 1] < rect[3]) & (centroid[:, 2] > nose[2] - 0.12)
    visible = front_visible(final, tri_v, candidate, rect)
    normal = np.cross(final[tri_v[:, 1]] - final[tri_v[:, 0]], final[tri_v[:, 2]] - final[tri_v[:, 0]])
    normal /= np.maximum(np.linalg.norm(normal, axis=1, keepdims=True), 1e-12)
    face = candidate & visible & (normal[:, 2] > 0.2)
    # MakeHuman's eye sockets, nostrils and lip line are closed but recessed: their angled faces
    # fail the front test and would show the body texture as a second pair of eyes or lips. Every
    # head triangle in those zones takes face paint.
    side = np.abs(centroid[:, 0] - nose[0])
    height = centroid[:, 1] - nose[1]
    depth = centroid[:, 2] - nose[2]
    s_ = facts["prescale"]
    eyes = (side > 0.008 * s_) & (side < 0.075 * s_) & (height > 0.012 * s_) & (height < 0.072 * s_) & (depth > -0.13 * s_)
    mouth = (side < 0.042 * s_) & (height > -0.06 * s_) & (height < 0.004 * s_) & (depth > -0.06 * s_)
    face |= candidate & (eyes | mouth)
    sockets = candidate & eyes
    loops = []
    used = np.unique(tri_v[face])
    uv = {int(v): [float((final[v, 0] - rect[0]) / (rect[1] - rect[0])), float(1 - (final[v, 1] - rect[2]) / (rect[3] - rect[2]))] for v in used}
    layout = {"triangles": np.nonzero(face)[0].tolist(), "uv": uv, "eyeLoops": [[int(v) for v in loop] for loop in loops],
              "sockets": np.nonzero(sockets)[0].tolist(),
              "box": box, "headScale": HEAD_SCALE, "size": SIZE}
    Path(out_json).write_text(json.dumps(layout) + "\n")

    # Reference: the neutral face in its layout, lit softly from the front by its normals.
    vertex_normal = np.zeros_like(final)
    for k in range(3):
        np.add.at(vertex_normal, tri_v[face][:, k], normal[face])
    vertex_normal /= np.maximum(np.linalg.norm(vertex_normal, axis=1, keepdims=True), 1e-12)
    image = np.full((SIZE, SIZE, 3), 40.0)
    zbuf = np.full((SIZE, SIZE), -np.inf)
    ys, xs = np.mgrid[0:SIZE, 0:SIZE] + 0.5
    light = np.array([0.3, 0.5, 0.8]) / np.linalg.norm([0.3, 0.5, 0.8])
    for t in tri_v[face]:
        p = np.array([uv[int(v)] for v in t]) * SIZE
        a, b, c = p
        m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
        if abs(np.linalg.det(m)) < 1e-9:
            continue
        lo = np.floor(p.min(0)).astype(int).clip(0, SIZE - 1)
        hi = np.ceil(p.max(0)).astype(int).clip(0, SIZE - 1)
        sub = np.stack([xs[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1], ys[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]], -1)
        w = np.einsum("ij,...j->...i", np.linalg.inv(m), sub - a)
        inside = (w[..., 0] >= 0) & (w[..., 1] >= 0) & (w.sum(-1) <= 1)
        bary = np.stack([1 - w.sum(-1), w[..., 0], w[..., 1]], -1)
        z = bary @ final[t, 2]
        n = bary @ vertex_normal[t]
        shade = 0.25 + 0.75 * np.clip(n @ light, 0, 1)
        region = zbuf[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        draw = inside & (z > region)
        region[draw] = z[draw]
        image[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1][draw] = (shade[draw] * 225)[:, None]
    Image.fromarray(image.clip(0, 255).astype(np.uint8)).save(out_png)
    print("face layout:", int(face.sum()), "triangles,", len(used), "vertices,", len(loops), "eye loops", [len(l) for l in loops])


if __name__ == "__main__":
    main(*sys.argv[1:4])
