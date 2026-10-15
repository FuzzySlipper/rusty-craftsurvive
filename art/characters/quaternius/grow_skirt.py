"""Grows a long flared skirt from a Quaternius character's body and skins it to the rig (#10029).

usage: blender -b --factory-startup --python art/characters/quaternius/grow_skirt.py -- <in.glb> <out.glb> <texture.png> [recipe.json]

No generative step: the skirt is a ring mesh built around the character in its rest pose.
1. At each height from the waist to the hem, take the 2D convex hull of the hip, leg and boot
   vertices around it, and read the hull's radius at each angle. The hull bridges the gap
   between the legs, so the skirt hangs as one piece.
2. Offset it outward, never let it narrow going down (it flares by at least `flare` metres per
   metre of drop, more towards the hem), add a few broad folds, and close it with thickness.
3. Skin it to pelvis and thighs only: pelvis at the waist, and towards the hem a share that
   follows each thigh on its side, so it swings with the stride without splitting between the
   legs. The calves and feet do not move it.
4. Remove the meshes it replaces (the trouser legs by default), and export with its clips.

The recipe can override every constant below.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
src, out, texture = args[0], args[1], args[2]
R = {
    "top": 1.0,            # waist height (m): just under the bodice's peplum, which overlaps it
    "hem": 0.16,           # hem height (m)
    "rings": 30,
    "segments": 56,
    "offset": 0.018,       # clearance outside the body hull (m)
    "flare_top": 0.05,     # minimum widening per metre of drop at the waist
    "flare_hem": 0.32,     # ... and at the hem
    "folds": 7,            # broad folds around the hem
    "fold_depth": 0.035,   # their depth at the hem, as a share of the radius
    "thickness": 0.008,
    "slab": 0.035,         # half-height of the vertex slab read at each ring (m)
    "thigh_share": 0.6,    # how much of the hem follows the thighs
    "sources": ["Legs", "Feet"],
    "trim": {"Feet": 0.3}, # cut these meshes above this height (m): boot shafts hidden by the skirt
    "remove": ["Legs", "Icosphere"],
    "material": "skirt",
}
if len(args) > 3:
    R.update(json.loads(Path(args[3]).read_text()))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
depsgraph = bpy.context.evaluated_depsgraph_get()
points = []
for obj in bpy.data.objects:
    if obj.type == "MESH" and any(s in obj.name for s in R["sources"]):
        evaluated = obj.evaluated_get(depsgraph)
        points += [evaluated.matrix_world @ v.co for v in evaluated.data.vertices]


def hull(pts):
    pts = sorted(set(pts))
    if len(pts) < 3:
        return pts
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def ray_radius(poly, cx, cy, angle):
    """Distance from (cx, cy) to the polygon edge along the angle."""
    dx, dy = math.cos(angle), math.sin(angle)
    best = 0.0
    for i in range(len(poly)):
        (x1, y1), (x2, y2) = poly[i], poly[(i + 1) % len(poly)]
        ex, ey = x2 - x1, y2 - y1
        den = dx * ey - dy * ex
        if abs(den) < 1e-12:
            continue
        t = ((x1 - cx) * ey - (y1 - cy) * ex) / den
        u = ((x1 - cx) * dy - (y1 - cy) * dx) / den
        if t > 0 and 0 <= u <= 1:
            best = max(best, t)
    return best


top, hem, rings, segs = R["top"], R["hem"], R["rings"], R["segments"]
waist = [p for p in points if abs(p.z - top) < R["slab"]]
cx = sum(p.x for p in waist) / len(waist)
cy = sum(p.y for p in waist) / len(waist)
angles = [2 * math.pi * j / segs for j in range(segs)]
radii = []
for i in range(rings):
    t = i / (rings - 1)
    z = top + (hem - top) * t
    slab = [(p.x, p.y) for p in points if abs(p.z - z) < R["slab"]]
    poly = hull(slab) if len(slab) >= 3 else []
    row = [(ray_radius(poly, cx, cy, a) if poly else 0) + R["offset"] for a in angles]
    if radii:
        dz = (top - hem) / (rings - 1)
        flare = R["flare_top"] + (R["flare_hem"] - R["flare_top"]) * t * t
        row = [max(r, prev + flare * dz) for r, prev in zip(row, radii[-1])]
    # smooth around the ring, so the hull's corners become cloth
    row = [(row[j - 1] + 2 * row[j] + row[(j + 1) % segs]) / 4 for j in range(segs)]
    radii.append(row)

bm = bmesh.new()
uv = bm.loops.layers.uv.new("UVMap")
verts = []
for i, row in enumerate(radii):
    t = i / (rings - 1)
    z = top + (hem - top) * t
    ring = []
    for j, a in enumerate(angles):
        fold = 1 + R["fold_depth"] * t * t * math.sin(R["folds"] * a + 0.7)
        r = row[j] * fold
        ring.append(bm.verts.new((cx + r * math.cos(a), cy + r * math.sin(a), z)))
    verts.append(ring)
for i in range(rings - 1):
    for j in range(segs):
        k = (j + 1) % segs
        f = bm.faces.new((verts[i][j], verts[i + 1][j], verts[i + 1][k], verts[i][k]))
        for loop, (ii, jj) in zip(f.loops, ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1))):
            loop[uv].uv = (jj / segs, 1 - ii / (rings - 1))
bm.normal_update()
mesh = bpy.data.meshes.new("Skirt")
bm.to_mesh(mesh)
bm.free()
skirt = bpy.data.objects.new("Skirt", mesh)
bpy.context.scene.collection.objects.link(skirt)
for poly in mesh.polygons:
    poly.use_smooth = True
# Outward normals: the ring winds counter-clockwise seen from above, so faces point out
# when built top-to-bottom; flip if the first face points inward.
first = mesh.polygons[0]
if (first.center - Vector((cx, cy, first.center.z))).dot(first.normal) < 0:
    mesh.flip_normals()

solid = skirt.modifiers.new("thickness", "SOLIDIFY")
solid.thickness = R["thickness"]
solid.offset = -1
bpy.context.view_layer.objects.active = skirt
bpy.ops.object.modifier_apply(modifier=solid.name)

# Skin: pelvis at the waist; towards the hem a share follows the thigh on each side.
groups = {name: skirt.vertex_groups.new(name=name) for name in ("pelvis", "thigh_l", "thigh_r")}
for v in mesh.vertices:
    t = min(max((top - v.co.z) / (top - hem), 0), 1)
    dx = v.co.x - cx
    reach = max(abs(dx), 1e-4) / max(math.hypot(dx, v.co.y - cy), 1e-4)
    left = R["thigh_share"] * t * min(max(0.5 + 0.5 * math.copysign(reach, dx), 0), 1)
    right = R["thigh_share"] * t * min(max(0.5 - 0.5 * math.copysign(reach, dx), 0), 1)
    groups["thigh_l"].add([v.index], left, "REPLACE")
    groups["thigh_r"].add([v.index], right, "REPLACE")
    groups["pelvis"].add([v.index], 1 - left - right, "REPLACE")
skirt.parent = rig
arm = skirt.modifiers.new("rig", "ARMATURE")
arm.object = rig

mat = bpy.data.materials.new(R["material"])
mat.use_nodes = True
bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
tex = mat.node_tree.nodes.new("ShaderNodeTexImage")
tex.image = bpy.data.images.load(str(Path(texture).resolve()))
mat.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 0.9
mesh.materials.append(mat)

for obj in list(bpy.data.objects):
    if obj.type == "MESH" and any(r in obj.name for r in R["remove"]):
        bpy.data.objects.remove(obj)
for name, height in R["trim"].items():
    for obj in [o for o in bpy.data.objects if o.type == "MESH" and name in o.name]:
        cut = bmesh.new()
        cut.from_mesh(obj.data)
        bmesh.ops.delete(cut, geom=[v for v in cut.verts if (obj.matrix_world @ v.co).z > height], context="VERTS")
        cut.to_mesh(obj.data)
        cut.free()
for obj in bpy.data.objects:
    obj.select_set(True)
bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_skins=True,
                          export_animation_mode="ACTIONS", export_yup=True)
print("SKIRT", json.dumps({"vertices": len(mesh.vertices), "faces": len(mesh.polygons),
                           "hem_radius": round(max(radii[-1]), 3), "waist_radius": round(max(radii[0]), 3)}))
