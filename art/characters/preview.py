"""Offline turnaround previews of a character GLB (#10014). Art study only, not in-game evidence.

usage: blender -b --factory-startup --python-exit-code 1 --python art/characters/preview.py -- \
    <in.glb> <out.png> [--clip NAME --frame F] [--size 512] [--views front,three-quarter,side,back] [--zoom head]

Renders the views side by side with Eevee under a neutral sun and sky; textures sample as the
GLB's samplers say (Blender maps nearest to Closest).
"""
import math
import sys

import bpy
from mathutils import Vector

argv = sys.argv[sys.argv.index("--") + 1:]
src, out = argv[0], argv[1]
opts = dict(zip(argv[2::2], argv[3::2]))
size = int(opts.get("--size", 512))
views = opts.get("--views", "front,three-quarter,side,back").split(",")
YAW = {"front": 0, "three-quarter": 35, "side": 90, "back": 180}

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=src)
scene = bpy.context.scene
# Skinned meshes only: the importer adds a bone-display icosphere to the scene.
meshes = [o for o in scene.objects if o.type == "MESH" and (o.parent or o.modifiers)]
arm = next((o for o in scene.objects if o.type == "ARMATURE"), None)
if arm and "--clip" in opts:
    action = bpy.data.actions.get(opts["--clip"])
    arm.animation_data_create()
    arm.animation_data.action = action
    if hasattr(arm.animation_data, "action_slot") and action.slots:
        arm.animation_data.action_slot = action.slots[0]
    scene.frame_set(int(float(opts.get("--frame", 1))))
bpy.context.view_layer.update()
deps = bpy.context.evaluated_depsgraph_get()
lo, hi = Vector((1e9,) * 3), Vector((-1e9,) * 3)
for o in meshes:
    ev = o.evaluated_get(deps)
    for v in ev.to_mesh().vertices:
        w = ev.matrix_world @ v.co
        lo = Vector(map(min, lo, w))
        hi = Vector(map(max, hi, w))
centre, height = (lo + hi) / 2, hi.z - lo.z
target, span = centre, height * 1.1
if opts.get("--zoom") == "head":
    target, span = Vector((centre.x, centre.y, hi.z - height * 0.07)), height * 0.2

world = bpy.data.worlds.new("w")
scene.world = world
world.use_nodes = True
world.node_tree.nodes["Background"].inputs[0].default_value = (0.55, 0.6, 0.68, 1)
world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
sun.data.energy = 3.5
sun.rotation_euler = (math.radians(50), 0, math.radians(30))
scene.collection.objects.link(sun)
cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
cam.data.type = "ORTHO"
cam.data.ortho_scale = span
scene.collection.objects.link(cam)
scene.camera = cam
scene.render.engine = "BLENDER_EEVEE"
scene.render.resolution_x = scene.render.resolution_y = size
scene.render.film_transparent = False
scene.view_settings.view_transform = "Standard"

tiles = []
for view in views:
    yaw = math.radians(YAW[view])
    # glTF +Z forward imports as Blender -Y; the camera looks back at the character's front.
    direction = Vector((math.sin(yaw), -math.cos(yaw), 0))
    cam.location = target + direction * 10
    cam.rotation_euler = (direction * -1).to_track_quat("-Z", "Y").to_euler()
    path = f"{out}.{view}.png"
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    tiles.append(path)

sheet = bpy.data.images.new("sheet", size * len(tiles), size)
pixels = [0.0] * (size * len(tiles) * size * 4)
for i, path in enumerate(tiles):
    img = bpy.data.images.load(path)
    px = list(img.pixels)
    for row in range(size):
        start = (row * size * len(tiles) + i * size) * 4
        pixels[start:start + size * 4] = px[row * size * 4:(row + 1) * size * 4]
sheet.pixels = pixels
sheet.filepath_raw = out
sheet.file_format = "PNG"
sheet.save()
print("preview", out)
