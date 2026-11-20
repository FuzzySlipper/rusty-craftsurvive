"""Grows a forked tongue inside a prepared body's mouth (#10879, the salt-monitor).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/add_tongue.py -- \
           <body.glb> <body-64.png> <species.json> <tongued.glb> <tongued-64.png>

A monitor flicks a long forked tongue. Tripo cannot make one (a sheet that draws it out gives a tongue fused into the
jaw), so it is grown here from the species' `"tongue"` block: a flat strip `width` wide and `thickness` thick from
`root` ([forward, up] on the midline, inside the mouth) `length` forward, splitting for its last `forkMetres` into two
prongs `forkSpread` apart. At rest it lies inside the closed head; the clips push its bone forward to flick it out
(`tongue` and `tongue_tip` in the species' points, `pin_tongue` in build_creature.py).

The paint grows a strip of `STRIP_ROWS` rows along its bottom (the body's UVs move up to keep every texel where it was),
painted the block's `colour`.
"""
import json
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Vector

STRIP_ROWS = 2
TONGUE_ROW = 0
# Sections along the strip before the fork: enough for it to bend with nothing, since one bone carries it.
SECTIONS = 3


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def widen_paint(paint_path, out_path, colour):
    """The paint with the strip added along its bottom; returns (width, old height, new height)."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    old = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)
    wider = np.zeros((height + STRIP_ROWS, width, 4), dtype=np.float32)
    wider[..., 3] = 1.0
    wider[STRIP_ROWS:] = old
    wider[:STRIP_ROWS, :, :3] = np.array(colour, dtype=np.float32) / 255.0
    out = bpy.data.images.new("tongued", width, height + STRIP_ROWS, alpha=False)
    out.pixels[:] = wider.ravel()
    out.filepath_raw = str(Path(out_path).resolve())
    out.file_format = "PNG"
    out.save()
    return width, height, height + STRIP_ROWS


def box(data, uv_layer, uv, start, end, half_width_start, half_width_end, half_thickness):
    """A flat box from start to end along the animal's forward axis (Blender -Y), tapering in width."""
    def ring(centre, half_width):
        return [data.verts.new(centre + Vector((sx * half_width, 0.0, sz * half_thickness)))
                for sx, sz in ((-1, -1), (1, -1), (1, 1), (-1, 1))]

    a, b = ring(start, half_width_start), ring(end, half_width_end)
    faces = [(a[k], a[(k + 1) % 4], b[(k + 1) % 4], b[k]) for k in range(4)] + [(a[3], a[2], a[1], a[0]), (b[0], b[1], b[2], b[3])]
    for verts in faces:
        face = data.faces.new(verts)
        for loop in face.loops:
            loop[uv_layer].uv = uv


def main(argv):
    body_path, paint_path, species_path, out_glb, out_paint = argv[:5]
    tongue = json.loads(Path(species_path).read_text())["tongue"]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    width, old_height, height = widen_paint(paint_path, out_paint, tongue["colour"])
    uv = mesh.data.uv_layers.active.data
    for loop in uv:
        loop.uv = Vector((loop.uv.x, (loop.uv.y * old_height + STRIP_ROWS) / height))
    texel = Vector((0.5 / width, (TONGUE_ROW + 0.5) / height))

    data = bmesh.new()
    data.from_mesh(mesh.data)
    uv_layer = data.loops.layers.uv.active
    forward, up = tongue["root"]
    length, fork = tongue["length"], tongue["forkMetres"]
    half_width, half_thickness = tongue["width"] / 2, tongue["thickness"] / 2

    def at(metres, side=0.0):
        return Vector((side, -(forward + metres), up))

    stem = length - fork
    for k in range(SECTIONS):
        a, b = stem * k / SECTIONS, stem * (k + 1) / SECTIONS
        taper = lambda metres: half_width * (1.0 - 0.4 * metres / stem)  # noqa: E731
        box(data, uv_layer, texel, at(a), at(b), taper(a), taper(b), half_thickness)
    spread = tongue["forkSpread"] / 2
    for side in (-1, 1):
        box(data, uv_layer, texel, at(stem, side * half_width * 0.3), at(length, side * spread),
            half_width * 0.3, half_width * 0.12, half_thickness * 0.8)
    data.normal_update()
    data.to_mesh(mesh.data)
    data.free()

    image = bpy.data.images.load(str(Path(out_paint).resolve()))
    for material in mesh.data.materials:
        for node in material.node_tree.nodes:
            if node.type == "TEX_IMAGE":
                node.image = image
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.ops.export_scene.gltf(filepath=str(Path(out_glb).resolve()), export_format="GLB", use_selection=True,
                              export_yup=True, export_materials="EXPORT", export_image_format="AUTO")
    triangles = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    print(f"tongue: {length:.2f} m from {forward:.2f} forward; {triangles} triangles; paint {width}x{height}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
