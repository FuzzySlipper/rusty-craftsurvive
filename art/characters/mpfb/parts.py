"""Turns a fitted MakeHuman body into skinned parts for assemble_character.py (#10028).

A part is a set of base-mesh triangles with per-corner UVs. Corners are split into payload
vertices by (base vertex, UV) pair, so a part keeps MakeHuman's UV layout exactly; every
character shares it because the topology is fixed.
"""
import numpy as np


def welded_normals(positions, triangles):
    """Smooth area-weighted normals over base vertices (the UV seams share them)."""
    face = np.cross(positions[triangles[:, 1]] - positions[triangles[:, 0]], positions[triangles[:, 2]] - positions[triangles[:, 0]])
    acc = np.zeros_like(positions)
    for k in range(3):
        np.add.at(acc, triangles[:, k], face)
    length = np.linalg.norm(acc, axis=1, keepdims=True)
    return acc / np.where(length > 0, length, 1)


def part(payload, name, positions, normals, joints, weights, tri_v, corner_uv):
    """Adds slot `name`: triangles tri_v (base vertex ids, n x 3) with corner UVs (n x 3 x 2, v up)."""
    corners = tri_v.ravel()
    uv = corner_uv.reshape(-1, 2)
    key = np.concatenate([corners[:, None].astype(np.float64), np.round(uv, 6)], 1)
    unique, first, inverse = np.unique(key, axis=0, return_index=True, return_inverse=True)
    ids = corners[first]
    payload[f"{name}_positions"] = positions[ids].astype(np.float32)
    payload[f"{name}_normals"] = normals[ids].astype(np.float32)
    payload[f"{name}_uvs"] = np.stack([uv[first, 0], 1 - uv[first, 1]], 1).astype(np.float32)
    payload[f"{name}_joints"] = joints[ids].astype(np.uint16)
    payload[f"{name}_weights"] = weights[ids].astype(np.float32)
    payload[f"{name}_indices"] = inverse.ravel().astype(np.uint32)
    return ids
