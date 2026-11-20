"""Cuts a domed body's back down the middle and builds the ribbed plates inside it (#10880, the hummock).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/split_dome.py -- \
           <body.glb> <body-64.png> <species.json> <split.glb> <split-64.png>

In threat the hummock heaves the two halves of its back apart on ribbed orange-red plates. Tripo models the closed
dome only, so the rest is made here from the species' `"dome"` block:

- **The cut.** Faces over the midline above `cutAboveMetres`, between the block's `forward` ends, are cut along the
  midline and the cut opened, so each half is free of the other there. The new vertices on each lip are nudged a
  millimetre to their own side, so `pin_dome` in build_creature.py can tell the halves apart by the sign of x.
- **The lining.** The dome's faces within `bandMetres` of the midline are copied, set `liningInsetMetres` inside the
  dome toward its long axis (`axisMetres` up) and turned inward, and thinned (`liningShare` of their triangles), so an opened half shows dark flesh from
  inside rather than nothing.
- **The plates.** `plates.count` gill-like fins stand across the midline under the dome, each following the dome's
  own curve `plates.insetMetres` beneath it and `plates.heightMetres` deep, `plates.halfWidthMetres` to each side
  where the cut is widest and narrowing to its ends; a floor closes the space under them.

The paint grows a strip of `STRIP_ROWS` rows along its bottom (the body's UVs move up to keep every texel where it was),
one texel column per colour in `PAINT` order. The plates and their floor take columns 1 to 3, which is how pin_dome
keeps them on the body while the halves and their lining open.
"""
import json
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Vector
from mathutils.bvhtree import BVHTree

STRIP_ROWS = 2
# Texel columns of the strip, in order: the lining, the plates' faces, their crests, the floor beneath them.
PAINT = ("lining", "plate", "crest", "floor")
# How far the lips of the cut are pushed to their own side, metres.
LIP_NUDGE = 0.001


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def widen_paint(paint_path, out_path, colours):
    """The paint with the strip added along its bottom; returns (width, old height, new height)."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    old = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)
    wider = np.zeros((height + STRIP_ROWS, width, 4), dtype=np.float32)
    wider[..., 3] = 1.0
    wider[STRIP_ROWS:] = old
    # Texel columns past the named colours repeat the floor, so a filtered edge never picks up skin.
    for column in range(width):
        name = PAINT[min(column, len(PAINT) - 1)]
        wider[:STRIP_ROWS, column, :3] = np.array(colours[name], dtype=np.float32) / 255.0
    out = bpy.data.images.new("split", width, height + STRIP_ROWS, alpha=False)
    out.pixels[:] = wider.ravel()
    out.filepath_raw = str(Path(out_path).resolve())
    out.file_format = "PNG"
    out.save()
    return width, height, height + STRIP_ROWS


def forward_of(co):
    return -co.y


def cut(data, dome):
    """Cut the dome's top along x = 0 between its ends, and open the cut."""
    start, end = dome["forward"]
    above = dome["cutAboveMetres"]
    faces = [f for f in data.faces
             if all(v.co.z > above and start <= forward_of(v.co) <= end for v in f.verts)
             and min(v.co.x for v in f.verts) < 0.0 < max(v.co.x for v in f.verts)]
    geom = list({v for f in faces for v in f.verts}) + list({e for f in faces for e in f.edges}) + faces
    result = bmesh.ops.bisect_plane(data, geom=geom, plane_co=(0, 0, 0), plane_no=(1, 0, 0))
    lips = [e for e in result["geom_cut"] if isinstance(e, bmesh.types.BMEdge)]
    bmesh.ops.split_edges(data, edges=lips)
    nudged = 0
    for vertex in {v for e in lips for v in e.verts if e.is_valid} | {v for v in data.verts if abs(v.co.x) < 1e-6}:
        if not vertex.link_faces:
            continue
        side = sum(f.calc_center_median().x for f in vertex.link_faces)
        vertex.co.x = LIP_NUDGE if side > 0 else -LIP_NUDGE
        nudged += 1
    return len(lips), nudged


def taper(f, start, end, metres):
    """1 in the middle of the cut, falling smoothly to 0 at its ends."""
    edge = min(f - start, end - f)
    if edge <= 0:
        return 0.0
    x = min(1.0, edge / metres)
    return x * x * (3 - 2 * x)


def lining(mesh, dome, texel):
    """A thinned, inset, inward-facing copy of the dome's faces near the midline, as its own object."""
    start, end = dome["forward"]
    band = dome["bandMetres"]
    above = dome["liningAboveMetres"]
    source = bmesh.new()
    source.from_mesh(mesh.data)
    keep = [f for f in source.faces
            if all(abs(v.co.x) <= band and v.co.z > above and start <= forward_of(v.co) <= end for v in f.verts)]
    drop = [f for f in source.faces if f not in set(keep)]
    bmesh.ops.delete(source, geom=drop, context="FACES")
    # Straight in toward the body's long axis: a generated body's vertex normals are not all to be trusted.
    for v in source.verts:
        inward = Vector((v.co.x, 0.0, v.co.z - dome["axisMetres"]))
        v.co -= inward.normalized() * dome["liningInsetMetres"]
    for f in source.faces:
        f.normal_flip()
    uv_layer = source.loops.layers.uv.active
    for f in source.faces:
        for loop in f.loops:
            loop[uv_layer].uv = texel["lining"]
    data = bpy.data.meshes.new("lining")
    source.to_mesh(data)
    source.free()
    obj = bpy.data.objects.new("lining", data)
    bpy.context.scene.collection.objects.link(obj)
    decimate = obj.modifiers.new("thin", "DECIMATE")
    decimate.ratio = dome["liningShare"]
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.modifier_apply(modifier=decimate.name)
    return obj


def plates(data, uv_layer, dome, surface, texel):
    """The fins and their floor, added to `data`."""
    start, end = dome["forward"]
    spec = dome["plates"]
    count, segments = spec["count"], spec["segments"]
    inset, depth, half_thick = spec["insetMetres"], spec["heightMetres"], spec["thicknessMetres"] / 2
    margin = spec.get("endMetres", 0.15)

    def top(x, f):
        hit = surface.ray_cast(Vector((x, -f, 10.0)), Vector((0, 0, -1)))
        return hit[0].z if hit[0] else None

    def face(verts, colour):
        new = data.faces.new([data.verts.new(v) for v in verts])
        for loop in new.loops:
            loop[uv_layer].uv = texel[colour]

    positions = [start + margin + (end - start - 2 * margin) * (i + 0.5) / count for i in range(count)]
    for f in positions:
        width = spec["halfWidthMetres"] * max(0.35, taper(f, start, end, dome["taperMetres"]))
        xs = [-width + 2 * width * k / segments for k in range(segments + 1)]
        crest = []
        for x in xs:
            z = top(x, f)
            if z is None:
                raise SystemExit(f"the dome has no surface over ({x:.2f}, {f:.2f})")
            crest.append(z - inset)
        for k in range(segments):
            a, b = Vector((xs[k], -f, crest[k])), Vector((xs[k + 1], -f, crest[k + 1]))
            down = Vector((0, 0, -depth))
            front, back = Vector((0, -half_thick, 0)), Vector((0, half_thick, 0))
            # The two broad faces of the fin, then its crest.
            face([a + front, b + front, b + front + down, a + front + down], "plate")
            face([b + back, a + back, a + back + down, b + back + down], "plate")
            face([a + back, b + back, b + front, a + front], "crest")
    # The floor: under the fins from the first to the last, as wide as they are.
    rows = []
    for f in [start + margin * 0.5] + positions + [end - margin * 0.5]:
        width = 1.1 * spec["halfWidthMetres"] * max(0.35, taper(f, start, end, dome["taperMetres"]))
        row = []
        for k in range(segments + 1):
            x = -width + 2 * width * k / segments
            z = top(x, f)
            row.append(Vector((x, -f, (z if z is not None else 0.0) - inset - depth)))
        rows.append(row)
    for r in range(len(rows) - 1):
        for k in range(segments):
            face([rows[r][k], rows[r][k + 1], rows[r + 1][k + 1], rows[r + 1][k]], "floor")


def main(argv):
    body_path, paint_path, species_path, out_glb, out_paint = argv[:5]
    dome = json.loads(Path(species_path).read_text())["dome"]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    width, old_height, height = widen_paint(paint_path, out_paint, dome["colours"])
    uv = mesh.data.uv_layers.active.data
    for loop in uv:
        loop.uv = Vector((loop.uv.x, (loop.uv.y * old_height + STRIP_ROWS) / height))
    texel = {name: Vector(((column + 0.5) / width, 0.5 / height)) for column, name in enumerate(PAINT)}

    surface = BVHTree.FromObject(mesh, bpy.context.evaluated_depsgraph_get())
    before = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    data = bmesh.new()
    data.from_mesh(mesh.data)
    lips, nudged = cut(data, dome)
    uv_layer = data.loops.layers.uv.active
    plates(data, uv_layer, dome, surface, texel)
    data.normal_update()
    data.to_mesh(mesh.data)
    data.free()

    inner = lining(mesh, dome, texel)
    inner.data.materials.append(mesh.data.materials[0])
    bpy.ops.object.select_all(action="DESELECT")
    inner.select_set(True)
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.join()

    image = bpy.data.images.load(str(Path(out_paint).resolve()))
    for material in [m for m in mesh.data.materials if m]:
        for node in material.node_tree.nodes:
            if node.type == "TEX_IMAGE":
                node.image = image
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.ops.export_scene.gltf(filepath=str(Path(out_glb).resolve()), export_format="GLB", use_selection=True,
                              export_yup=True, export_materials="EXPORT", export_image_format="AUTO")
    triangles = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    print(f"split dome: {lips} edges cut, {nudged} lip vertices; {before} -> {triangles} triangles; paint {width}x{height}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
