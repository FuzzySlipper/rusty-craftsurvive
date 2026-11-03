"""Renders review frames of a creature GLB's clips, from the exported file itself (#10568).

usage: blender -b --factory-startup --python-exit-code 1 --python art/creatures/preview.py -- \
           <creature.glb> <out dir> [--frames 4] [--size 360] [--sequence walk,graze]

Writes <out dir>/<clip>-<view>-<n>.png: a side and a three-quarter view at evenly spaced times
of each clip, on a ground plane with a 1.8 m post for scale. With --sequence, it instead renders
every frame of the named clips from the three-quarter view, as <clip>-seq-<frame>.png, for
animations (sequence.py). These are art studies of the clips, not in-game evidence; montage.py
lays the stills out as one sheet.
"""
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

# (azimuth from the animal's left towards its front, elevation), degrees. The animal faces -Y.
VIEWS = {"side": (0.0, 8.0), "quarter": (40.0, 15.0)}
POST_METRES = 1.8


def main(argv):
    glb, out = Path(argv[0]), Path(argv[1])
    frames = int(argv[argv.index("--frames") + 1]) if "--frames" in argv else 4
    size = int(argv[argv.index("--size") + 1]) if "--size" in argv else 360
    sequence = argv[argv.index("--sequence") + 1].split(",") if "--sequence" in argv else None
    out.mkdir(parents=True, exist_ok=True)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    bpy.ops.import_scene.gltf(filepath=str(glb))
    rig = next(o for o in bpy.context.scene.objects if o.type == "ARMATURE")
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "TEXTURE"
    scene.render.resolution_x = scene.render.resolution_y = size
    scene.render.film_transparent = False
    scene.world = bpy.data.worlds.new("w")
    bpy.ops.mesh.primitive_plane_add(size=40)
    bpy.ops.mesh.primitive_cylinder_add(radius=0.06, depth=POST_METRES, location=(-2.2, 0, POST_METRES / 2))
    camera = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 8.0
    scene.collection.objects.link(camera)
    scene.camera = camera
    rig.animation_data_create()
    actions = sorted(bpy.data.actions, key=lambda a: a.name)
    if sequence:
        actions = [a for a in actions if a.name in sequence]

    def shoot(view, path):
        azimuth, elevation = VIEWS[view]
        a, e = math.radians(azimuth), math.radians(elevation)
        distance = 20.0
        camera.location = Vector((distance * math.cos(e) * math.cos(a),
                                  -distance * math.cos(e) * math.sin(a),
                                  1.2 + distance * math.sin(e)))
        direction = Vector((0, -0.3, 1.2)) - camera.location
        camera.rotation_euler = direction.to_track_quat("-Z", "Y").to_euler()
        scene.render.filepath = str(path)
        bpy.ops.render.render(write_still=True)

    for action in actions:
        rig.animation_data.action = action
        rig.animation_data.action_slot = action.slots[0]
        start, end = (int(f) for f in action.frame_range)
        if sequence:
            for frame in range(start, end + 1):
                scene.frame_set(frame)
                shoot("quarter", out / f"{action.name}-seq-{frame - start:03d}.png")
            continue
        for n in range(frames):
            scene.frame_set(int(round(start + (end - start) * n / max(frames - 1, 1))))
            for view in VIEWS:
                shoot(view, out / f"{action.name}-{view}-{n}.png")
    print(f"rendered {len(actions)} clips to {out}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
