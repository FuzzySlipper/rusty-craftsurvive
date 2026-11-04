"""Shows how a species' joints sit in its prepared body, for fitting them (#10570).

usage: blender -b --factory-startup --python-exit-code 1 --python art/creatures/fit.py -- \
           <body.glb> <species.json> <out prefix> [--texture <paint.png>] [--scale <view metres>]

Writes <out prefix>-side.png, -front.png and -top.png, orthographic with the bones drawn through
the body, and prints the body's extent in slices along its length, in the species' own
[side, forward, up] metres: where its back and belly are, and how wide it is, at each.
"""
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
import build_creature  # noqa: E402
from families import FAMILIES  # noqa: E402

SLICE_METRES = 0.4
JOINT_METRES = 0.05


def slices(mesh):
    points = [mesh.matrix_world @ v.co for v in mesh.data.vertices]
    forward = [-p.y for p in points]
    print(f"forward {min(forward):.2f} to {max(forward):.2f}, up {min(p.z for p in points):.2f} to {max(p.z for p in points):.2f}, side {max(abs(p.x) for p in points):.2f}")
    f = min(forward)
    while f < max(forward):
        cut = [p for p in points if f <= -p.y < f + SLICE_METRES]
        if cut:
            print(f"  {f:6.2f}: up {min(p.z for p in cut):5.2f} to {max(p.z for p in cut):5.2f}, side {max(abs(p.x) for p in cut):4.2f}")
        f += SLICE_METRES


def main(argv):
    species = json.loads(Path(argv[1]).read_text())
    out = argv[2]
    view = float(argv[argv.index("--scale") + 1]) if "--scale" in argv else 7.0
    texture = argv[argv.index("--texture") + 1] if "--texture" in argv else None
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = build_creature.import_mesh(argv[0], texture)
    slices(mesh)
    rig = build_creature.build_armature(species, FAMILIES[species["family"]])
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.color_type = "TEXTURE"
    scene.display.shading.show_xray = True
    scene.display.shading.xray_alpha = 0.55
    scene.render.resolution_x, scene.render.resolution_y = 1200, 800
    scene.world = bpy.data.worlds.new("w")
    for bone in rig.data.bones:
        for point in (bone.head_local, bone.tail_local):
            bpy.ops.mesh.primitive_uv_sphere_add(radius=JOINT_METRES, location=point)
        bpy.ops.mesh.primitive_cylinder_add(radius=JOINT_METRES / 3, depth=(bone.tail_local - bone.head_local).length,
                                            location=(bone.head_local + bone.tail_local) / 2)
        cylinder = bpy.context.object
        cylinder.rotation_mode = "QUATERNION"
        cylinder.rotation_quaternion = Vector((0, 0, 1)).rotation_difference((bone.tail_local - bone.head_local).normalized())
    camera = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = view
    scene.collection.objects.link(camera)
    scene.camera = camera
    middle = view * 0.3
    for name, location, rotation in (("side", (20, 0, middle), (90, 0, 90)), ("front", (0, -20, middle), (90, 0, 0)), ("top", (0, 0, 20), (0, 0, 0))):
        camera.location = location
        camera.rotation_euler = [math.radians(a) for a in rotation]
        scene.render.filepath = f"{out}-{name}.png"
        bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
