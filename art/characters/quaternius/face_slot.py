"""Gives a Quaternius head a paintable face slot (#10029).

usage:
  blender -b --factory-startup --python art/characters/quaternius/face_slot.py -- base  <in.glb> <work-dir>
  blender -b --factory-startup --python art/characters/quaternius/face_slot.py -- build <in.glb> <out.glb> <face.png>

The pack's head is a skin with holes for separate modelled eyeballs, eyebrow cards and a mouth.
Faces painted over that read doubled (the modelled eyes and brows stay where they are). Both
modes first make the face one surface:
1. drop the Eyes and Eyebrows meshes;
2. cap the eye and mouth holes with faces over their existing rim vertices (no new vertices,
   so skin weights are untouched); the neck opening stays open;
3. find a square face box around those features.

base: renders the box from the front, softly lit, as the head currently looks with its sockets
capped: the picture a painter (or an image model) repaints. Writes face-base.png, face-box.json.

build: moves the forward-facing head faces inside the box into a new "face" material whose UVs
are a planar front projection of the box, so the painted front picture is the face texture
exactly, and exports the GLB with its clips. The product swaps this slot's texture for
expressions (CharacterStyleTest's face slot).
"""
import json
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
mode = args[0]
HEAD = "Superhero_Female"
DROP = ("Eyes", "Eyebrows")
FACE_PX = 512
FACING = 0.3                  # minimum forward component of a face-slot face normal
FORWARD = Vector((0, -1, 0))  # the character faces Blender -Y (glTF +Z)
BOX_SIZE = 0.24              # face box side in metres: hairline to chin on the base female head
BOX_DROP = 0.026              # box centre below the eye holes' centre, in metres

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=args[1])
for obj in list(bpy.data.objects):
    if obj.type == "MESH" and obj.name.startswith(DROP):
        bpy.data.objects.remove(obj)
head = next(o for o in bpy.data.objects if o.type == "MESH" and o.name.startswith(HEAD))
mesh, world = head.data, head.matrix_world

bm = bmesh.new()
bm.from_mesh(mesh)
loops, seen = [], set()
for e in [e for e in bm.edges if e.is_boundary]:
    if e in seen:
        continue
    group, stack = [], [e]
    while stack:
        c = stack.pop()
        if c in seen:
            continue
        seen.add(c)
        group.append(c)
        stack += [x for v in c.verts for x in v.link_edges if x.is_boundary and x not in seen]
    z = sum((world @ v.co).z for ed in group for v in ed.verts) / (2 * len(group))
    loops.append((z, group))
loops.sort(key=lambda l: l[0])
caps = [g for _, g in loops[1:]]          # every hole but the lowest (the neck cut)
rim = [world @ v.co for g in caps for e in g for v in e.verts]
cap_faces = []
for group in caps:
    cap_faces += bmesh.ops.holes_fill(bm, edges=group, sides=0)["faces"]
lo = Vector((min(p.x for p in rim), min(p.y for p in rim), min(p.z for p in rim)))
hi = Vector((max(p.x for p in rim), max(p.y for p in rim), max(p.z for p in rim)))
eyes = [world @ v.co for _, g in loops[-2:] for e in g for v in e.verts]  # the two highest holes
size = BOX_SIZE
cx, cz = (lo.x + hi.x) / 2, sum(p.z for p in eyes) / len(eyes) - BOX_DROP
x0, z0 = cx - size / 2, cz - size / 2
front_y = lo.y

if mode == "base":
    bm.to_mesh(mesh)
    bm.free()
    work = Path(args[2])
    work.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = FACE_PX * 2
    scene.view_settings.view_transform = "Standard"
    w = bpy.data.worlds.new("w")
    scene.world = w
    w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (0.75, 0.72, 0.68, 1)
    w.node_tree.nodes["Background"].inputs[1].default_value = 1.4
    key = bpy.data.objects.new("key", bpy.data.lights.new("key", "SUN"))
    key.data.energy = 1.2
    key.rotation_euler = (1.15, 0, 0.35)
    scene.collection.objects.link(key)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = size
    cam.location = (cx, front_y - 1.0, cz)
    cam.rotation_euler = (1.5707963, 0, 0)
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = str(work / "face-base.png")
    bpy.ops.render.render(write_still=True)
    (work / "face-box.json").write_text(json.dumps({"x0": x0, "z0": z0, "size": size, "caps": len(caps)}, indent=1))
    print("FACE_BASE", work / "face-base.png")
    sys.exit(0)

out, face_png = args[2], args[3]
face_mat = bpy.data.materials.new("face")
face_mat.use_nodes = True
nt = face_mat.node_tree
bsdf = next(n for n in nt.nodes if n.type == "BSDF_PRINCIPLED")
tex = nt.nodes.new("ShaderNodeTexImage")
tex.image = bpy.data.images.load(str(Path(face_png).resolve()))
nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
bsdf.inputs["Roughness"].default_value = 0.9
mesh.materials.append(face_mat)
face_index = len(mesh.materials) - 1
uv = bm.loops.layers.uv.active
normals = world.to_3x3().inverted().transposed()
cap_set = set(cap_faces)
chosen = 0
for f in bm.faces:
    c = world @ f.calc_center_median()
    n = (normals @ f.normal).normalized()
    in_box = x0 <= c.x <= x0 + size and z0 <= c.z <= z0 + size
    if f in cap_set or (in_box and n.dot(FORWARD) > FACING):
        f.material_index = face_index
        chosen += 1
        for loop in f.loops:
            p = world @ loop.vert.co
            loop[uv].uv = ((p.x - x0) / size, (p.z - z0) / size)
bm.to_mesh(mesh)
bm.free()
for obj in bpy.data.objects:
    obj.select_set(True)
bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_skins=True,
                          export_animation_mode="ACTIONS", export_yup=True)
print("FACE_SLOT", json.dumps({"faces": chosen, "caps": len(caps), "box": [x0, z0, size]}))
