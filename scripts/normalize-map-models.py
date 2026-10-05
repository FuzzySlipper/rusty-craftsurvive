"""Normalize generated map clutter models for the world-map view (Blender, run headless).

Each source GLB is centred on its base, scaled to unit height, given downscaled
textures (map clutter is seen small), and exported to content/map-models; then
scripts/bake-map-static-mesh.py turns each into the runtime static mesh.
A preview render is written next to the output list for review.

    blender -b --python scripts/normalize-map-models.py -- SOURCE.glb OUT.glb PREVIEW.png
"""
import math
import sys

import bpy
from mathutils import Vector

TEXTURE_SIZE = 256
argv = sys.argv[sys.argv.index("--") + 1:]
source, output, preview = argv

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=source)
meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
bpy.ops.object.select_all(action="DESELECT")
for o in meshes:
    o.select_set(True)
bpy.context.view_layer.objects.active = meshes[0]
if len(meshes) > 1:
    bpy.ops.object.join()
mesh = bpy.context.view_layer.objects.active
bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
corners = [mesh.matrix_world @ Vector(c) for c in mesh.bound_box]
low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
height = high.z - low.z
mesh.location -= Vector(((low.x + high.x) / 2, (low.y + high.y) / 2, low.z))
bpy.ops.object.transform_apply(location=True)
mesh.scale = (1 / height,) * 3
bpy.ops.object.transform_apply(scale=True)
for image in bpy.data.images:
    if image.size[0] > TEXTURE_SIZE:
        image.scale(TEXTURE_SIZE, TEXTURE_SIZE)
        image.pack()
bpy.ops.export_scene.gltf(filepath=output, export_format="GLB", use_selection=False, export_image_format="JPEG",
                          export_yup=True)

camera = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
bpy.context.scene.collection.objects.link(camera)
camera.location = (2.2, -2.2, 1.6)
camera.rotation_euler = (math.radians(70), 0, math.radians(45))
sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
bpy.context.scene.collection.objects.link(sun)
sun.rotation_euler = (math.radians(40), 0, math.radians(30))
sun.data.energy = 3
world = bpy.data.worlds.new("world")
world.color = (0.55, 0.62, 0.7)
scene = bpy.context.scene
scene.world = world
scene.camera = camera
scene.render.resolution_x = scene.render.resolution_y = 384
scene.render.filepath = preview
bpy.ops.render.render(write_still=True)
