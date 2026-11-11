"""MakeHuman proxy (.proxy) and clothes (.mhclo) assets, fitted to any MPFB body (#10028).

Each asset vertex is bound to three base-mesh vertices with weights, plus an offset scaled by the
body's own size along each axis (the asset's x/y/z_scale reference vertex pairs). This mirrors
MPFB's ClothesService.fit_clothes_to_human (mpfb2 3af0cd5), on dump_body.py positions (Blender
space). A fitted asset is therefore fixed topology with its own UVs on every body, and its skin
weights are the same blend of the base vertices' weights.

The CC0 MakeHuman system assets (proxies, clothes, hair) are not in the MPFB checkout; they come
from makehuman_system_assets_cc0.zip (provenance in content/characters-cast.sources.json).
"""
from pathlib import Path

import numpy as np


class Asset:
    def __init__(self, path):
        path = Path(path)
        self.path = path
        self.name = path.stem
        self.refs, self.weights, self.offsets, self.scales = [], [], [], {}
        self.delete = np.zeros(0, int)
        obj_file = None
        mode = None
        for line in path.read_text(errors="replace").splitlines():
            words = line.split()
            if not words or words[0].startswith("#"):
                continue
            key = words[0]
            if mode == "v" and not words[0].lstrip("-").isdigit():
                mode = None
            if mode == "v":
                if len(words) == 1:
                    v = int(words[0])
                    self.refs.append((v, v, v))
                    self.weights.append((1.0, 0.0, 0.0))
                    self.offsets.append((0.0, 0.0, 0.0))
                else:
                    self.refs.append(tuple(int(w) for w in words[:3]))
                    self.weights.append(tuple(float(w) for w in words[3:6]))
                    d0, d1, d2 = (float(w) for w in words[6:9])
                    self.offsets.append((d0, -d2, d1))  # MakeHuman (x, y up, z) -> Blender (x, -z, y), as MPFB
                continue
            if mode == "d":
                if words[0].isdigit():
                    ids = []
                    for w in words:
                        if "-" in w:
                            a, b = w.split("-")
                            ids += range(int(a), int(b) + 1)
                        else:
                            ids.append(int(w))
                    self.delete = np.concatenate([self.delete, np.array(ids, int)])
                    continue
                mode = None
            if key == "obj_file":
                obj_file = words[1]
            elif key in ("x_scale", "y_scale", "z_scale"):
                self.scales[key] = (int(words[1]), int(words[2]), float(words[3]))
            elif key == "verts":
                mode = "v"
            elif key == "delete_verts":
                mode = "d"
        self.refs = np.array(self.refs, int)
        self.weights = np.array(self.weights)
        self.offsets = np.array(self.offsets)
        self.read_obj(path.parent / obj_file)

    def read_obj(self, path):
        verts, uvs, tri_v, tri_t = 0, [], [], []
        for line in path.read_text(errors="replace").splitlines():
            if line.startswith("v "):
                verts += 1
            elif line.startswith("vt "):
                uvs.append([float(x) for x in line.split()[1:3]])
            elif line.startswith("f "):
                corners = [c.split("/") for c in line.split()[1:]]
                v = [int(c[0]) - 1 for c in corners]
                t = [int(c[1]) - 1 if len(c) > 1 and c[1] else int(c[0]) - 1 for c in corners]
                for a in range(1, len(v) - 1):
                    tri_v.append([v[0], v[a], v[a + 1]])
                    tri_t.append([t[0], t[a], t[a + 1]])
        assert verts == len(self.refs), (path, verts, len(self.refs))
        self.tri_v, self.tri_t, self.uv = np.array(tri_v), np.array(tri_t), np.array(uvs)

    def fit(self, base_positions, offset_scale=1.0):
        """Asset vertex positions on a body (Blender space, as dump_body.py writes)."""
        p = base_positions
        size = np.ones(3)
        if self.scales:
            (a, b, r) = self.scales["x_scale"]
            size[0] = abs(p[a, 0] - p[b, 0]) / r
            (a, b, r) = self.scales["z_scale"]
            size[1] = abs(p[a, 1] - p[b, 1]) / r
            (a, b, r) = self.scales["y_scale"]
            size[2] = abs(p[a, 2] - p[b, 2]) / r
        blended = np.einsum("vk,vkj->vj", self.weights, p[self.refs])
        return blended + self.offsets * size * offset_scale

    def dense_weights(self, base_dense):
        """Per asset vertex: the blend of its reference vertices' skin weights."""
        return np.einsum("vk,vkb->vb", self.weights, base_dense[self.refs])
