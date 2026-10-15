"""Grows a knee-length coat with sleeves from a Quaternius character's body (#10029).

usage: blender -b --factory-startup --python art/characters/quaternius/grow_coat.py -- <in.glb> <out.glb> <texture.png> [recipe.json]

The skirt test's method (grow_skirt.py), extended to a whole garment; no generative geometry.
- Body: rings from the collar to the hem. Each ring is the 2D convex hull of the torso, leg and
  boot vertices at its height (the arms and the tunic's short sleeves left out above the
  armpit), offset outward, smoothed, and never narrowing below the hips, where it flares to
  the hem. A V-shaped front opening is cut from the collar to the hem.
- Sleeves: per arm, rings along the arm from inside the shoulder to the wrist, each the hull
  of the arm's vertices across it, offset; slightly wider at the cuff.
- Skin: weights copied from the nearest body surface (Blender's data transfer), then below
  the hips blended into the skirt scheme: pelvis, plus a share of the thigh on each side, so
  the coat's tails swing with the stride without splitting.
- Thickness, one painted texture, exported with its clips.
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
    "collar": 1.545,       # top ring height (m), around the neck base
    "shoulder": 1.49,      # shoulder ring height (m)
    "armpit": 1.36,        # above this, arm and tunic-sleeve vertices are left out of the body hull
    "torso_half": 0.215,   # ... beyond this |x|
    "hip": 0.97,           # below this the coat flares and follows the skirt weights
    "hem": 0.48,           # hem height (m): about the knee
    "rings": 34,
    "segments": 60,
    "offset": 0.016,
    "leg_clearance": 0.02, # extra clearance below the hips, where the thighs swing forward (m)
    "collar_offset": 0.012,
    "flare_hip": 0.08,     # minimum widening per metre of drop just below the hips
    "flare_hem": 0.40,     # ... and at the hem
    "open_top": 6.0,       # front opening half-angle (degrees) at the collar
    "open_hem": 16.0,      # ... and at the hem
    "sleeve_start": 0.17,  # |x| where a sleeve starts, inside the shoulder (m)
    "sleeve_end": 0.70,    # |x| of the cuff, at the wrist
    "sleeve_rings": 16,
    "sleeve_segments": 24,
    "sleeve_offset": 0.010,
    "cuff_flare": 0.012,
    "thickness": 0.009,
    "slab": 0.03,
    "thigh_share": 0.9,    # high: the coat panels must follow the thighs, or the trousers poke through
    "blend_band": 0.08,    # height over which copied weights give way to the skirt scheme (m)
    "material": "coat",
}
if len(args) > 3:
    R.update(json.loads(Path(args[3]).read_text()))

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
dg = bpy.context.evaluated_depsgraph_get()


def world_points(*names):
    pts = []
    for obj in bpy.data.objects:
        if obj.type == "MESH" and any(n in obj.name for n in names):
            ev = obj.evaluated_get(dg)
            pts += [ev.matrix_world @ v.co for v in ev.data.vertices]
    return pts


body_pts = world_points("_Body", "_Legs", "_Feet")
# The sleeves follow the arms only; the tunic's shoulder pads would puff them out.
arm_pts = world_points("_Arms")


def hull(pts):
    pts = sorted(set(pts))
    if len(pts) < 3:
        return pts
    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
    lo, up = [], []
    for p in pts:
        while len(lo) >= 2 and cross(lo[-2], lo[-1], p) <= 0:
            lo.pop()
        lo.append(p)
    for p in reversed(pts):
        while len(up) >= 2 and cross(up[-2], up[-1], p) <= 0:
            up.pop()
        up.append(p)
    return lo[:-1] + up[:-1]


def ray(poly, cx, cy, a):
    dx, dy = math.cos(a), math.sin(a)
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


def smooth(row):
    n = len(row)
    return [(row[j - 1] + 2 * row[j] + row[(j + 1) % n]) / 4 for j in range(n)]


bm = bmesh.new()
uv = bm.loops.layers.uv.new("UVMap")

# --- body -------------------------------------------------------------------------------
segs, rings = R["segments"], R["rings"]
heights = [R["collar"]] + [R["shoulder"] + (R["hem"] - R["shoulder"]) * i / (rings - 2) for i in range(rings - 1)]
mid = [p for p in body_pts if abs(p.z - R["hip"]) < R["slab"]]
cx = sum(p.x for p in mid) / len(mid)
cy = sum(p.y for p in mid) / len(mid)
angles = [2 * math.pi * j / segs for j in range(segs)]
radii = []
for i, z in enumerate(heights):
    near = [p for p in body_pts if abs(p.z - z) < R["slab"] and (z < R["armpit"] or abs(p.x) <= R["torso_half"])]
    poly = hull([(p.x, p.y) for p in near])
    off = R["collar_offset"] if i == 0 else R["offset"]
    if z < R["hip"]:
        off += R["leg_clearance"] * min((R["hip"] - z) / 0.15, 1)
    row = smooth([ray(poly, cx, cy, a) + off for a in angles])
    if radii and z < R["hip"]:
        t = (R["hip"] - z) / (R["hip"] - R["hem"])
        flare = R["flare_hip"] + (R["flare_hem"] - R["flare_hip"]) * t * t
        dz = heights[i - 1] - z
        row = [max(r, prev + flare * dz) for r, prev in zip(row, radii[-1])]
    radii.append(row)

front = -math.pi / 2  # the character faces Blender -Y
body_verts = []
for i, (z, row) in enumerate(zip(heights, radii)):
    body_verts.append([bm.verts.new((cx + r * math.cos(a), cy + r * math.sin(a), z)) for a, r in zip(angles, row)])
span = heights[0] - heights[-1]
for i in range(rings - 1):
    zt = (heights[i] + heights[i + 1]) / 2
    t = (heights[0] - zt) / span
    half = math.radians(R["open_top"] + (R["open_hem"] - R["open_top"]) * t)
    for j in range(segs):
        k = (j + 1) % segs
        am = (angles[j] + math.pi / segs) - front
        am = (am + math.pi) % (2 * math.pi) - math.pi
        if abs(am) < half:
            continue  # the front opening
        f = bm.faces.new((body_verts[i][j], body_verts[i + 1][j], body_verts[i + 1][k], body_verts[i][k]))
        for loop, (ii, jj) in zip(f.loops, ((i, j), (i + 1, j), (i + 1, j + 1), (i, j + 1))):
            loop[uv].uv = (0.5 * jj / segs, 1 - (heights[0] - heights[ii]) / span)

# --- sleeves ----------------------------------------------------------------------------
sr, ss = R["sleeve_rings"], R["sleeve_segments"]
for side in (1, -1):
    xs = [side * (R["sleeve_start"] + (R["sleeve_end"] - R["sleeve_start"]) * i / (sr - 1)) for i in range(sr)]
    rows, centres = [], []
    for i, x in enumerate(xs):
        near = [p for p in arm_pts if abs(p.x - x) < R["slab"]]
        cy_ = sum(p.y for p in near) / len(near)
        cz_ = sum(p.z for p in near) / len(near)
        poly = hull([(p.y, p.z) for p in near])
        phis = [2 * math.pi * j / ss for j in range(ss)]
        row = smooth([ray(poly, cy_, cz_, ph) + R["sleeve_offset"] + R["cuff_flare"] * (i / (sr - 1)) ** 2 for ph in phis])
        rows.append(row)
        centres.append((cy_, cz_))
    verts = [[bm.verts.new((x, cyc + r * math.cos(ph), czc + r * math.sin(ph)))
              for ph, r in zip([2 * math.pi * j / ss for j in range(ss)], row)]
             for x, (cyc, czc), row in zip(xs, centres, rows)]
    for i in range(sr - 1):
        for j in range(ss):
            k = (j + 1) % ss
            quad = (verts[i][j], verts[i][k], verts[i + 1][k], verts[i + 1][j])
            f = bm.faces.new(quad if side > 0 else tuple(reversed(quad)))
            for loop in f.loops:
                ii = next(n for n in range(sr) if loop.vert in verts[n])
                jj = verts[ii].index(loop.vert)
                loop[uv].uv = (0.5 + 0.5 * jj / ss, 1 - ii / (sr - 1))

bm.normal_update()
mesh = bpy.data.meshes.new("Coat")
bm.to_mesh(mesh)
bm.free()
coat = bpy.data.objects.new("Coat", mesh)
bpy.context.scene.collection.objects.link(coat)
for poly in mesh.polygons:
    poly.use_smooth = True
# outward normals: a face's normal should point away from the body axis / arm axis
bm2 = bmesh.new()
bm2.from_mesh(mesh)
for f in bm2.faces:
    c = f.calc_center_median()
    if abs(c.x) > R["sleeve_start"] + 0.02 and c.z > R["armpit"] - 0.05:
        axis = Vector((c.x, 0.07, 1.456))
        outward = c - Vector((c.x, axis.y, axis.z))
    else:
        outward = c - Vector((cx, cy, c.z))
    if f.normal.dot(outward) < 0:
        f.normal_flip()
bm2.to_mesh(mesh)
bm2.free()

solid = coat.modifiers.new("thickness", "SOLIDIFY")
solid.thickness = R["thickness"]
solid.offset = -1
bpy.context.view_layer.objects.active = coat
bpy.ops.object.modifier_apply(modifier=solid.name)

# --- skin -------------------------------------------------------------------------------
# Each coat vertex takes the inverse-distance blend of its four nearest body vertices' weights.
from mathutils import kdtree  # noqa: E402

sources = [o for o in bpy.data.objects if o.type == "MESH" and any(n in o.name for n in ("_Body", "_Arms", "_Legs"))]
src_pts, src_w = [], []
for s in sources:
    names = {g.index: g.name for g in s.vertex_groups}
    for v in s.data.vertices:
        src_pts.append(s.matrix_world @ v.co)
        src_w.append({names[g.group]: g.weight for g in v.groups if g.weight > 0})
tree = kdtree.KDTree(len(src_pts))
for i, p in enumerate(src_pts):
    tree.insert(p, i)
tree.balance()
weights = []
for v in mesh.vertices:
    acc, total = {}, 0.0
    for _, i, d in tree.find_n(coat.matrix_world @ v.co, 4):
        k = 1 / max(d, 1e-4)
        total += k
        for n, x in src_w[i].items():
            acc[n] = acc.get(n, 0) + k * x
    weights.append({n: x / total for n, x in acc.items()})
groups = {}


def group(name):
    if name not in groups:
        groups[name] = coat.vertex_groups.new(name=name)
    return groups[name]


for v, w in zip(mesh.vertices, weights):
    z = v.co.z
    sleeve = abs(v.co.x) > R["sleeve_start"] + 0.02 and z > R["armpit"] - 0.05
    if not sleeve and z < R["hip"] + R["blend_band"]:
        t = min(max((R["hip"] - z) / (R["hip"] - R["hem"]), 0), 1)
        dx = v.co.x - cx
        reach = abs(dx) / max(math.hypot(dx, v.co.y - cy), 1e-4)
        left = R["thigh_share"] * t * min(max(0.5 + 0.5 * math.copysign(reach, dx), 0), 1)
        right = R["thigh_share"] * t * min(max(0.5 - 0.5 * math.copysign(reach, dx), 0), 1)
        skirt = {"pelvis": 1 - left - right, "thigh_l": left, "thigh_r": right}
        b = min(max((R["hip"] + R["blend_band"] - z) / R["blend_band"], 0), 1)  # 0 above the band, 1 below
        names = set(w) | set(skirt)
        w = {n: (1 - b) * w.get(n, 0) + b * skirt.get(n, 0) for n in names}
    total = sum(w.values()) or 1
    for n, x in sorted(w.items(), key=lambda kv: -kv[1])[:4]:
        if x > 0:
            group(n).add([v.index], x / total, "REPLACE")

coat.parent = rig
arm = coat.modifiers.new("rig", "ARMATURE")
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
    if obj.type == "MESH" and obj.name.startswith("Icosphere"):
        bpy.data.objects.remove(obj)
# The tunic's short sleeves and shoulder pads are hidden under the coat sleeves, and would poke
# through them as the arms move: cut the tunic outside the torso above the armpit.
for obj in [o for o in bpy.data.objects if o.type == "MESH" and "_Body" in o.name]:
    cut = bmesh.new()
    cut.from_mesh(obj.data)
    hidden = [v for v in cut.verts if abs((obj.matrix_world @ v.co).x) > R["torso_half"] - 0.03
              and (obj.matrix_world @ v.co).z > R["armpit"] - 0.06]
    bmesh.ops.delete(cut, geom=hidden, context="VERTS")
    cut.to_mesh(obj.data)
    cut.free()
for obj in bpy.data.objects:
    obj.select_set(True)
bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_skins=True,
                          export_animation_mode="ACTIONS", export_yup=True)
print("COAT", json.dumps({"vertices": len(mesh.vertices), "faces": len(mesh.polygons), "groups": len(groups)}))
