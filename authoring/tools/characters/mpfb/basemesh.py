"""MakeHuman's fixed base mesh, as MPFB2 ships it (#10028).

Every MPFB human is this one topology with moved vertices, so its faces, UV layout, vertex
groups, joint cubes and skin weights are known by index once and reused for every character.
Positions come from dump_body.py (Blender space: metres, Z up, facing -Y); `to_gltf` turns them
into glTF space (Y up, facing +Z).
"""
import gzip
import json
from functools import cached_property
from pathlib import Path

import numpy as np

DATA = Path("/home/agent/dev/asset-pipeline/external/mpfb2/src/mpfb/data")


def to_gltf(points):
    p = np.asarray(points, np.float64)
    return np.stack([p[..., 0], p[..., 2], -p[..., 1]], -1)


class BaseMesh:
    def __init__(self, data=DATA):
        self.data = Path(data)

    @cached_property
    def obj(self):
        """Triangles (vertex and UV indices), UVs (v up), from base.obj; quads split 0-1-2, 0-2-3."""
        uvs, tri_v, tri_t = [], [], []
        for line in (self.data / "3dobjs/base.obj").read_text().splitlines():
            if line.startswith("vt "):
                uvs.append([float(x) for x in line.split()[1:3]])
            elif line.startswith("f "):
                corners = [c.split("/") for c in line.split()[1:]]
                v = [int(c[0]) - 1 for c in corners]
                t = [int(c[1]) - 1 for c in corners]
                for a, b in ((1, 2), (2, 3))[:len(v) - 2]:
                    tri_v.append([v[0], v[a], v[b]])
                    tri_t.append([t[0], t[a], t[b]])
        return np.array(tri_v), np.array(tri_t), np.array(uvs)

    @cached_property
    def groups(self):
        raw = json.loads((self.data / "mesh_metadata/basemesh_vertex_groups.json").read_text())
        return {name: np.concatenate([np.arange(a, b + 1) for a, b in ranges]) for name, ranges in raw.items()}

    def faces_in(self, group):
        """Triangles whose vertices all belong to a vertex group."""
        tri_v, _, _ = self.obj
        member = np.zeros(19158, bool)
        member[self.groups[group]] = True
        return member[tri_v].all(1)

    @cached_property
    def rig(self):
        return json.loads((self.data / "rigs/standard/rig.game_engine.json").read_text())

    @cached_property
    def weights(self):
        """Bone name -> (vertex indices, weights), from MPFB's game_engine weights."""
        raw = json.loads((self.data / "rigs/standard/weights.game_engine.json").read_text())["weights"]
        return {bone: (np.array([i for i, _ in pairs], int), np.array([w for _, w in pairs])) for bone, pairs in raw.items() if pairs}

    def point(self, spec, positions):
        if spec.get("strategy") == "CUBE":
            return positions[self.groups[spec["cube_name"]]].mean(0)
        if spec.get("strategy") == "MEAN":
            return positions[spec["vertex_indices"]].mean(0)
        return np.array(spec["default_position"], np.float64)

    def joints(self, positions):
        """Bone -> (head, tail) in the positions' space."""
        return {bone: (self.point(b["head"], positions), self.point(b["tail"], positions)) for bone, b in self.rig.items()}

    def parent(self, bone):
        return self.rig[bone]["parent"] or None
