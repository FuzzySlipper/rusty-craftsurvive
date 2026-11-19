"""Grows a crest of filaments with glowing beads down a prepared body's back (#10878, the glass-courser).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/add_crest.py -- \
           <body.glb> <body-64.png> <species.json> <crested.glb> <crested-64.png>

Tripo fuses thin shapes into the body, so a crest of fine filaments is not on the reference sheet: it is grown here,
from the species' `"crest"` block. Each filament roots just inside the body on its back's midline (found by casting
down onto the body), stands along the surface's normal swept back by the block's `sweep`, and is a three-sided shaft
that tapers to a bead. Length and sweep are interpolated along the body from `[forward, value]` pairs; filaments
splay alternately left and right by `splayDegrees`.

The paint grows a strip of `STRIP_ROWS` rows along its bottom (the body's UVs are moved up to keep every texel where it
was): the shaft is painted base to tip in `shaft` colours, the bead in `bead`. `emission_mask.py --hue` on the bead's
hue then makes the beads glow. Loose pieces like these move with the body surface nearest them (`pin_islands` in
build_creature.py), so the crest stands as it is in every clip.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree
import numpy as np

STRIP_ROWS = 3
# The strip's middle row holds the crest's texels, one each from u = 0: shaft colours, then the bead.
CREST_ROW = 1
# How far a filament's root sinks into the body, so it reads attached.
ROOT_DEPTH = 0.01
SHAFT_SIDES = 3


def interpolate(pairs, forward):
    """A value at `forward` from [forward, value] pairs, linear between them, held past either end."""
    pairs = sorted(pairs)
    if forward <= pairs[0][0]:
        return pairs[0][1]
    for (f0, v0), (f1, v1) in zip(pairs, pairs[1:]):
        if forward <= f1:
            return v0 + (v1 - v0) * (forward - f0) / (f1 - f0)
    return pairs[-1][1]


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def widen_paint(paint_path, out_path, crest):
    """The paint with the strip added along its bottom; returns (width, old height, new height)."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    # Blender keeps rows bottom first, as stored values (sRGB for a PNG), RGBA.
    old = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)
    wider = np.zeros((height + STRIP_ROWS, width, 4), dtype=np.float32)
    wider[..., 3] = 1.0
    wider[STRIP_ROWS:] = old
    texels = [colour for colour in crest["shaft"]] + [crest["bead"]]
    wider[:STRIP_ROWS, :, :3] = np.array(crest["shaft"][0], dtype=np.float32) / 255.0
    for u, colour in enumerate(texels):
        wider[:STRIP_ROWS, u, :3] = np.array(colour, dtype=np.float32) / 255.0
    out = bpy.data.images.new("crested", width, height + STRIP_ROWS, alpha=False)
    out.pixels[:] = wider.ravel()
    out.filepath_raw = str(Path(out_path).resolve())
    out.file_format = "PNG"
    out.save()
    return width, height, height + STRIP_ROWS


def texel_uv(u, width, height):
    """The centre of texel u in the crest row, in Blender UV (v up from the bottom)."""
    return Vector(((u + 0.5) / width, (CREST_ROW + 0.5) / height))


def main(argv):
    body_path, paint_path, species_path, out_glb, out_paint = argv[:5]
    crest = json.loads(Path(species_path).read_text())["crest"]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    width, old_height, height = widen_paint(paint_path, out_paint, crest)

    # The body's UVs move up by the strip, so each keeps its texel.
    uv = mesh.data.uv_layers.active.data
    for loop in uv:
        loop.uv = Vector((loop.uv.x, (loop.uv.y * old_height + STRIP_ROWS) / height))

    tree = BVHTree.FromObject(mesh, bpy.context.evaluated_depsgraph_get())
    top = max(v.co.z for v in mesh.data.vertices) + 1.0
    data = bmesh.new()
    data.from_mesh(mesh.data)
    uv_layer = data.loops.layers.uv.active
    shafts = len(crest["shaft"])
    start, end, count = crest["from"], crest["to"], crest["count"]
    made = 0
    for i in range(count):
        forward = start + (end - start) * i / (count - 1)
        hit, normal, _index, _distance = tree.ray_cast(Vector((0.0, -forward, top)), Vector((0.0, 0.0, -1.0)))
        if hit is None:
            continue
        # Up along the surface, swept back (towards +y, the animal's rear) and splayed to alternate sides.
        sweep = math.radians(interpolate(crest["sweep"], forward))
        splay = math.radians(crest.get("splayDegrees", 0.0)) * (1 if i % 2 else -1)
        side_normal = Vector((0.0, normal.y, normal.z)).normalized()
        direction = Matrix.Rotation(-sweep, 3, "X") @ side_normal
        direction = (Matrix.Rotation(splay, 3, "Y") @ direction).normalized()
        length = interpolate(crest["lengths"], forward)
        root = hit - side_normal * ROOT_DEPTH
        tip = root + direction * length
        across = direction.cross(Vector((1.0, 0.0, 0.0))).normalized()
        if across.length < 0.5:
            across = Vector((0.0, 1.0, 0.0))
        ring_base, ring_tip = [], []
        for k in range(SHAFT_SIDES):
            turn = Matrix.Rotation(2 * math.pi * k / SHAFT_SIDES, 3, direction)
            spoke = turn @ across
            ring_base.append(data.verts.new(root + spoke * crest["radius"]))
            ring_tip.append(data.verts.new(tip + spoke * crest["radius"] * 0.4))
        for k in range(SHAFT_SIDES):
            a, b = k, (k + 1) % SHAFT_SIDES
            face = data.faces.new((ring_base[a], ring_base[b], ring_tip[b], ring_tip[a]))
            for loop in face.loops:
                u = 0 if loop.vert in ring_base else shafts - 1
                loop[uv_layer].uv = texel_uv(u, width, height)
        # The bead: an octahedron on the tip.
        bead = crest["bead_radius"]
        centre = tip + direction * bead * 0.6
        axes = [direction, -direction, across, -across, direction.cross(across), -direction.cross(across)]
        points = [data.verts.new(centre + axis * bead) for axis in axes]
        for a in (0, 1):
            for b, c in ((2, 4), (4, 3), (3, 5), (5, 2)):
                face = data.faces.new((points[a], points[b], points[c]) if a == 0 else (points[a], points[c], points[b]))
                for loop in face.loops:
                    loop[uv_layer].uv = texel_uv(shafts, width, height)
        made += 1
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
    print(f"crest: {made} filaments; {triangles} triangles; paint {width}x{height}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
