"""Lays a generated flying body level for prepare_mesh.py (#10630).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/prepare_flyer.py -- <generated.glb> <level.glb>

`cut_flyer_views.py` shows Tripo a bird standing on its tail, so the body arrives with its beak along
+z and its belly along +x. A quarter turn about y puts the beak along +x and the belly down, which is
the frame `prepare_mesh.py --head +x` takes.
"""
import math
import sys

import bpy
from mathutils import Matrix


def main():
    source, target = sys.argv[sys.argv.index("--") + 1:][:2]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=source)
    turn = Matrix.Rotation(math.radians(90), 4, "Y")
    for item in [o for o in bpy.data.objects if o.parent is None]:
        item.matrix_world = turn @ item.matrix_world
    bpy.ops.export_scene.gltf(filepath=target, export_format="GLB")


main()
