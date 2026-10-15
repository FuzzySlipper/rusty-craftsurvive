"""Exports an assembled Quaternius character as an Engine-ready GLB (#10029).

usage: blender -b <assembled.blend> --python art/characters/quaternius/export.py -- <out.glb> <texture-size>

- Drops helper objects the packs carry (Icosphere bone gizmos).
- Reduces every texture to texture-size px (the packs ship 2K-4K).
- Adds a one-frame "rest" clip: the Engine refuses a skinned GLB without clips (#10014).
Material slot order (body first, face second) is fixed afterwards by order_slots.py.
"""
import sys

import bpy

args = sys.argv[sys.argv.index("--") + 1:]
out, size = args[0], int(args[1])

for obj in list(bpy.data.objects):
    if obj.type == "MESH" and obj.name.startswith("Icosphere"):
        bpy.data.objects.remove(obj)

for image in bpy.data.images:
    if image.size[0] > size:
        image.scale(size, max(1, image.size[1] * size // image.size[0]))
        image.pack()

rig = next(o for o in bpy.data.objects if o.type == "ARMATURE")
rig.animation_data_clear()
bpy.context.view_layer.objects.active = rig
bpy.ops.object.mode_set(mode="POSE")
for pb in rig.pose.bones:
    pb.rotation_mode = "QUATERNION"
    pb.location = (0, 0, 0)
    pb.rotation_quaternion = (1, 0, 0, 0)
    pb.scale = (1, 1, 1)
    for path in ("location", "rotation_quaternion", "scale"):
        pb.keyframe_insert(path, frame=1)
bpy.ops.object.mode_set(mode="OBJECT")
rig.animation_data.action.name = "rest"

bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_animations=True, export_skins=True,
                          export_animation_mode="ACTIONS", export_yup=True, export_apply=False)
print("EXPORTED", out)
