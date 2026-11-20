"""Paints a prepared body's right side as the mirror of its left (#10882, the plume-neck).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/mirror_paint.py -- \
           <body.glb> <master.png> <mirrored.png> [--keep-within 0.02]

Tripo paints each side of a body on its own, so a face can come out different from its two sides even when the
reference sheet's right view is the left mirrored: the plume-neck's head had a smudged brow and mouth on its right.
This repaints every texel of a face on the right (its centre further than `--keep-within` metres right of the
midline) from the left: the texel's point on the body is mirrored across the midline, the nearest point of the body's
surface found there, and its texel copied. Faces on the midline keep their own paint. Run it on the master, before
pixel_reduce.py, so both sides reduce alike.
"""
import sys
from pathlib import Path

import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

KEEP_WITHIN = 0.02


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def barycentric(p, a, b, c):
    """Barycentric weights of 2D points p (n x 2) in triangle a, b, c."""
    v0, v1 = b - a, c - a
    v2 = p - a
    d00, d01, d11 = v0 @ v0, v0 @ v1, v1 @ v1
    denom = d00 * d11 - d01 * d01
    if abs(denom) < 1e-14:
        return None
    d20, d21 = v2 @ v0, v2 @ v1
    v = (d11 * d20 - d01 * d21) / denom
    w = (d00 * d21 - d01 * d20) / denom
    return np.stack([1.0 - v - w, v, w], axis=1)


def main(argv):
    body_path, master_path, out_path = argv[:3]
    keep = float(argv[argv.index("--keep-within") + 1]) if "--keep-within" in argv else KEEP_WITHIN
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.quads_convert_to_tris()
    bpy.ops.object.mode_set(mode="OBJECT")
    data = mesh.data
    image = bpy.data.images.load(str(Path(master_path).resolve()))
    width, height = image.size
    source = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)
    out = source.copy()
    uv = data.uv_layers.active.data
    verts = np.array([v.co[:] for v in data.vertices])
    tree = BVHTree.FromObject(mesh, bpy.context.evaluated_depsgraph_get())
    polygons = list(data.polygons)

    def uv_at(index, point):
        """The texel of the surface point `point` on polygon `index`."""
        poly = polygons[index]
        corners = [Vector(verts[i]) for i in poly.vertices]
        loops = [uv[poly.loop_start + k].uv for k in range(3)]
        a, b, c = corners
        v0, v1, v2 = b - a, c - a, point - a
        d00, d01, d11, d20, d21 = v0.dot(v0), v0.dot(v1), v1.dot(v1), v2.dot(v0), v2.dot(v1)
        denom = d00 * d11 - d01 * d01 or 1e-14
        wv = (d11 * d20 - d01 * d21) / denom
        ww = (d00 * d21 - d01 * d20) / denom
        wu = 1.0 - wv - ww
        return loops[0] * wu + loops[1] * wv + loops[2] * ww

    repainted = 0
    for poly in polygons:
        if poly.center.x >= -keep:
            continue
        corners = verts[list(poly.vertices)]
        loops = np.array([uv[poly.loop_start + k].uv[:] for k in range(3)])
        texels = loops * np.array([width, height])
        low = np.floor(texels.min(axis=0)).astype(int)
        high = np.ceil(texels.max(axis=0)).astype(int)
        xs, ys = np.meshgrid(np.arange(max(low[0], 0), min(high[0], width)), np.arange(max(low[1], 0), min(high[1], height)))
        if xs.size == 0:
            continue
        centres = np.stack([xs.ravel() + 0.5, ys.ravel() + 0.5], axis=1)
        weights = barycentric(centres, *texels)
        if weights is None:
            continue
        inside = np.all(weights >= -0.02, axis=1)
        for (x, y), w in zip(centres[inside].astype(int), weights[inside]):
            point = Vector(w @ corners)
            mirrored = Vector((-point.x, point.y, point.z))
            found, _normal, index, _distance = tree.find_nearest(mirrored)
            if found is None:
                continue
            u, v = uv_at(index, found)
            sx = min(width - 1, max(0, int(u * width)))
            sy = min(height - 1, max(0, int(v * height)))
            out[y, x] = source[sy, sx]
            repainted += 1
    result = bpy.data.images.new("mirrored", width, height, alpha=True)
    result.pixels[:] = out.ravel()
    result.filepath_raw = str(Path(out_path).resolve())
    result.file_format = "PNG"
    result.save()
    print(f"mirror_paint: {repainted} texels of the right side repainted from the left")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
