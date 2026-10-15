"""Assembles a Quaternius character from the CC0 packs in local/ (owner-provided, ignored by git).

usage: blender -b --python art/characters/quaternius/assemble.py -- <recipe.json> <out.blend> [preview-dir]

A recipe names the base head, outfit parts and hair. Outfits are worn with only the base
character's head (the pack's Readme), so the base body is cut to its head and neck, and every
mesh is moved onto the first imported armature; all parts share one rig.
"""
import json
import sys
from pathlib import Path

import bpy
from mathutils import Vector

args = sys.argv[sys.argv.index("--") + 1:]
recipe = json.loads(Path(args[0]).read_text())
out_blend = Path(args[1])
preview = Path(args[2]) if len(args) > 2 else None
root = Path(recipe["root"])
HEAD_GROUPS = ("Head", "neck_01")
HEAD_KEEP = 0.5
# The outfits' own hands bend poorly (a clenched clip folds them into a claw); the base body's
# hands bend cleanly. So the base keeps its hands as well as its head, and the outfit parts
# lose theirs, cut at the wrist.
HAND_PREFIXES = ("hand_", "thumb_", "index_", "middle_", "ring_", "pinky_")
BASE_HAND_KEEP = 0.01   # the base keeps a little forearm past the wrist ...
OUTFIT_HAND_CUT = 0.98  # ... and the outfit keeps its wrist, so the two overlap with no gap
GLOVE_SHARE = 0.3       # faces this much on the hand bones take the glove material

bpy.ops.wm.read_factory_settings(use_empty=True)


def import_gltf(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(root / path))
    return [o for o in bpy.data.objects if o not in before]


rig = None
parts = []
for entry in [recipe["base"], *recipe["parts"], *recipe.get("hair", [])]:
    for obj in import_gltf(entry["file"]):
        if obj.type == "ARMATURE":
            if rig is None:
                rig = obj
                rig.name = "Rig"
            continue
        if obj.type != "MESH":
            continue
        if entry.get("drop") and any(d in obj.name for d in entry["drop"]):
            continue
        parts.append((entry, obj))

for entry, obj in parts:
    world = obj.matrix_world.copy()
    obj.parent = rig
    obj.matrix_world = world
    for mod in obj.modifiers:
        if mod.type == "ARMATURE":
            mod.object = rig
    hands = {g.index for g in obj.vertex_groups if g.name.startswith(HAND_PREFIXES)}
    hand_share = lambda v: sum(g.weight for g in v.groups if g.group in hands)
    if entry.get("head_only") or (entry is not recipe["base"] and entry.get("own_hands") is not True and hands):
        groups = {obj.vertex_groups[g].index for g in HEAD_GROUPS if g in obj.vertex_groups}
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.select_all(action="DESELECT")
        bpy.ops.object.mode_set(mode="OBJECT")
        for v in obj.data.vertices:
            if entry.get("head_only"):
                keep = sum(g.weight for g in v.groups if g.group in groups) >= HEAD_KEEP or hand_share(v) >= BASE_HAND_KEEP
            else:
                keep = hand_share(v) < OUTFIT_HAND_CUT
            v.select = not keep
        bpy.ops.object.mode_set(mode="EDIT")
        bpy.ops.mesh.delete(type="VERT")
        bpy.ops.object.mode_set(mode="OBJECT")

# Gloves: the base body's hands, given a painted leather material (recipe "gloves": texture path),
# so gloved outfits keep their gloves with hands that bend cleanly.
if recipe.get("gloves"):
    glove = bpy.data.materials.new("gloves")
    glove.use_nodes = True
    bsdf = next(n for n in glove.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    tex = glove.node_tree.nodes.new("ShaderNodeTexImage")
    tex.image = bpy.data.images.load(str(Path(recipe["gloves"]).resolve()))
    tex.image.name = "T_Gloves"
    glove.node_tree.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.9
    for entry, obj in parts:
        if entry is not recipe["base"] or len(obj.data.vertices) < 1000:
            continue
        hands = {g.index for g in obj.vertex_groups if g.name.startswith(HAND_PREFIXES)}
        obj.data.materials.append(glove)
        index = len(obj.data.materials) - 1
        for poly in obj.data.polygons:
            share = sum(sum(g.weight for g in obj.data.vertices[i].groups if g.group in hands)
                        for i in poly.vertices) / len(poly.vertices)
            if share >= GLOVE_SHARE:
                poly.material_index = index

# Remove the other imported armatures (every mesh now follows the first).
for obj in list(bpy.data.objects):
    if obj.type == "ARMATURE" and obj is not rig:
        bpy.data.objects.remove(obj)

bpy.ops.wm.save_as_mainfile(filepath=str(out_blend))
print("ASSEMBLED", [o.name for _, o in parts], "verts", sum(len(o.data.vertices) for _, o in parts))

if preview:
    preview.mkdir(parents=True, exist_ok=True)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in {e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items} else "BLENDER_EEVEE"
    scene.render.resolution_x, scene.render.resolution_y = 900, 1200
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[1].default_value = 0.6
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3
    sun.rotation_euler = (0.9, 0.2, 0.6)
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.lens = 70
    scene.collection.objects.link(cam)
    scene.camera = cam
    height = max((o.matrix_world @ Vector(c)).z for _, o in parts for c in o.bound_box)
    for name, (x, y, zf) in {"front": (0, -6, 0.55), "three-quarter": (3.6, -4.8, 0.55), "head": (0.9, -1.6, 0.92)}.items():
        cam.location = (x, y, height * zf + (0.1 if name == "head" else 0))
        target = Vector((0, 0, height * (0.92 if name == "head" else 0.5)))
        cam.rotation_euler = (target - cam.location).to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = str(preview / f"{name}.png")
        bpy.ops.render.render(write_still=True)
