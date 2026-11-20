"""Cuts a prepared body's mouth open and builds its inside, so the jaw opens on a real mouth (#10882, the plume-neck).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/open_mouth.py -- \
           <body.glb> <body-64.png> <species.json> <mouthed.glb> <mouthed-64.png>

Where a reference sheet draws the jaws barely parted, Tripo closes them: the mouth is a painted line on one surface,
and a clip that drops the jaw stretches that surface into a smear. This opens it from the species' `"mouthCut"` block:

- **The cut.** The head is cut along the bite line, a straight line on the animal's side from `hinge` to `tip`
  (each [forward, up]), through every face forward of the hinge, and the cut opened, so the upper and lower jaws are
  separate surfaces joined only at the mouth's corners (the last `cornerMetres` before the hinge stay joined).
- **The inside.** Each jaw's edge of the cut is closed by a fan to a point `depthMetres` inside the jaw on the
  midline: the roof of the mouth and its floor, painted `insideColour`.
- **What stays above.** With `upperLuminanceAbove`, faces painted brighter than that (fangs that hang outside the
  lower lip) are not cut, and pin_cut_mouth keeps them on the head.
- **The teeth.** Every `toothEvery` metres along each jaw's edge, a pointed triangle stands just inside it, rooted
  `toothMetres` within the jaw and pointing to the bite line, painted `toothColour`. Each tooth lies wholly on its own
  jaw's side of the cut, which is how pin_cut_mouth in build_creature.py tells whose it is.

The paint grows a strip of `STRIP_ROWS` rows along its bottom (the body's UVs move up to keep every texel where it was):
column 0 the inside, column 1 the teeth. pin_cut_mouth in build_creature.py then gives each vertex to the jaw on
whose side of the cut it lies.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Vector

STRIP_ROWS = 2
INSIDE_COLUMN, TOOTH_COLUMN = 0, 1
# How far a tooth's point stops short of the bite line, on its own jaw's side, in metres.
TOOTH_CLEAR = 0.003
# How far each lip's edge is moved off the bite line, in metres: enough that no export welds the jaws together again.
LIP_PART = 0.001


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def widen_paint(paint_path, out_path, inside, tooth):
    """The paint with the strip added along its bottom; returns (width, old height, new height)."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    old = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)
    wider = np.zeros((height + STRIP_ROWS, width, 4), dtype=np.float32)
    wider[..., 3] = 1.0
    wider[STRIP_ROWS:] = old
    wider[:STRIP_ROWS, :, :3] = np.array(inside, dtype=np.float32) / 255.0
    wider[:STRIP_ROWS, TOOTH_COLUMN, :3] = np.array(tooth, dtype=np.float32) / 255.0
    out = bpy.data.images.new("mouthed", width, height + STRIP_ROWS, alpha=False)
    out.pixels[:] = wider.ravel()
    out.filepath_raw = str(Path(out_path).resolve())
    out.file_format = "PNG"
    out.save()
    return width, height, height + STRIP_ROWS


def upper_painted(data, uv_layer, paint_path, cut, heights):
    """The faces whose texel is brighter than the block's `upperLuminanceAbove`: parts of the upper jaw, such as fangs,
    that reach below the bite line. The UVs have already moved up by the strip, so they are read back to the paint."""
    threshold = cut.get("upperLuminanceAbove")
    if threshold is None:
        return set()
    old_height, height = heights
    paint = bpy.data.images.load(str(Path(paint_path).resolve()), check_existing=True)
    width = paint.size[0]
    pixels = np.array(paint.pixels[:], dtype=np.float32).reshape(old_height, width, 4)
    found = set()
    for face in data.faces:
        u, v = face.loops[0][uv_layer].uv
        row = int((v * height - STRIP_ROWS))
        if not 0 <= row < old_height:
            continue
        r, g, b, _ = pixels[row, min(width - 1, int(u * width))]
        if 0.2126 * r + 0.7152 * g + 0.0722 * b > threshold:
            found.add(face.index)
    return found


def main(argv):
    body_path, paint_path, species_path, out_glb, out_paint = argv[:5]
    cut = json.loads(Path(species_path).read_text())["mouthCut"]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    width, old_height, height = widen_paint(paint_path, out_paint, cut["insideColour"], cut["toothColour"])
    for loop in mesh.data.uv_layers.active.data:
        loop.uv = Vector((loop.uv.x, (loop.uv.y * old_height + STRIP_ROWS) / height))
    texel = [Vector(((column + 0.5) / width, 0.5 / height)) for column in (INSIDE_COLUMN, TOOTH_COLUMN)]

    hinge_f, hinge_z = cut["hinge"]
    tip_f, tip_z = cut["tip"]
    # The bite line on the side view, as a plane through it and the side axis: Blender forward is -Y.
    along = Vector((0.0, -(tip_f - hinge_f), tip_z - hinge_z)).normalized()
    normal = Vector((1.0, 0.0, 0.0)).cross(along).normalized()
    if normal.z < 0:
        normal = -normal
    origin = Vector((0.0, -hinge_f, hinge_z))

    def above(point):
        return (point - origin).dot(normal)

    data = bmesh.new()
    data.from_mesh(mesh.data)
    uv_layer = data.loops.layers.uv.active
    reach = cut.get("reachMetres", 0.25)
    # Faces painted as the upper jaw's own (#10883: the root-adder's fangs hang outside its lower lip) are not cut:
    # they stay whole on the head.
    upper = upper_painted(data, uv_layer, paint_path, cut, (old_height, height))
    region = [f for f in data.faces if f.index not in upper and all(-v.co.y >= hinge_f - 0.01 for v in f.verts)
              and all(abs(above(v.co)) <= reach for v in f.verts)]
    geom = list({v for f in region for v in f.verts}) + list({e for f in region for e in f.edges}) + region
    result = bmesh.ops.bisect_plane(data, geom=geom, plane_co=origin, plane_no=normal, dist=1e-5)
    corner = hinge_f + cut["cornerMetres"]
    on_line = [e for e in result["geom_cut"] if isinstance(e, bmesh.types.BMEdge)
               and all(-v.co.y >= corner for v in e.verts)]
    bmesh.ops.split_edges(data, edges=on_line)

    # The two edges of the cut: boundary edges on the bite line, sorted to the side each one's face lies on.
    edges = {"upper": [], "lower": []}
    for e in data.edges:
        if not e.is_boundary or len(e.link_faces) != 1:
            continue
        if not all(abs(above(v.co)) < 1e-4 and -v.co.y >= corner - 1e-4 for v in e.verts):
            continue
        side = "upper" if above(e.link_faces[0].calc_center_median()) > 0 else "lower"
        edges[side].append(e)
    if not edges["upper"] or not edges["lower"]:
        raise SystemExit(f"mouthCut: the cut left {len(edges['upper'])} upper and {len(edges['lower'])} lower edges")
    # Part the lips a hair: a glTF export welds vertices that share a place and a texel, which would join the jaws
    # again at every point of the cut.
    for side, sign in (("upper", 1.0), ("lower", -1.0)):
        for v in {v for e in edges[side] for v in e.verts}:
            v.co += normal * (sign * LIP_PART)

    def face(verts, uv):
        made = data.faces.new(verts)
        for loop in made.loops:
            loop[uv_layer].uv = uv
        return made

    depth = cut["depthMetres"]
    teeth = 0
    for side, sign in (("upper", 1.0), ("lower", -1.0)):
        ring = edges[side]
        centre_f = sum(-v.co.y for e in ring for v in e.verts) / (2 * len(ring))
        centre = Vector((0.0, -centre_f, hinge_z + (tip_z - hinge_z) * (centre_f - hinge_f) / (tip_f - hinge_f)))
        centre += normal * (sign * depth)
        middle = data.verts.new(centre)
        for e in ring:
            a, b = e.verts
            made = face([a, b, middle], texel[0])
            # Inward, away from the jaw's own outside: the face's normal points toward the other jaw.
            made.normal_update()
            if made.normal.dot(normal) * sign > 0:
                made.normal_flip()
        # Teeth stand inside the jaw's edge, from within the jaw to a point at the bite line, every toothEvery metres:
        # wholly on their own jaw's side, so the cut decides whose they are, and hidden until the mouth opens.
        ordered = sorted(ring, key=lambda e: -(e.verts[0].co.y + e.verts[1].co.y))
        walked = 0.0
        for e in ordered:
            walked += e.calc_length()
            if walked < cut["toothEvery"]:
                continue
            walked = 0.0
            a, b = (v.co.copy() for v in e.verts)
            mid = (a + b) * 0.5
            inset = Vector((-mid.x, 0.0, 0.0)).normalized() * cut.get("toothInsetMetres", 0.006)
            root = normal * (sign * cut["toothMetres"])
            tip = normal * (sign * TOOTH_CLEAR)
            face([data.verts.new(a + inset + root), data.verts.new(b + inset + root), data.verts.new(mid + inset + tip)],
                 texel[1])
            teeth += 1
    data.normal_update()
    data.to_mesh(mesh.data)
    data.free()

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
    print(f"mouth: cut {len(on_line)} edges; {len(edges['upper'])} upper and {len(edges['lower'])} lower edges;"
          f" {teeth} teeth; {triangles} triangles; paint {width}x{height}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
