"""Evaluates one MPFB2 recipe and dumps the morphed base mesh (#10028). Run inside Blender.

usage: blender -b -noaudio --factory-startup --python-exit-code 1 \
    --python art/characters/mpfb/dump_body.py -- <mpfb checkout> <recipe.json> <out.npy>

Blender is used only for what MPFB owns: its macro interpolation (gender, age, muscle, weight,
proportions, height, race) and its named targets. The output is every base-mesh vertex
(MakeHuman's fixed 19158-vertex topology, helpers and joint cubes included) in base order, in
Blender space (metres, Z up, facing -Y), so everything after this step works on known vertex
indices: the rig's joint cubes, the skin weights, the UV layout and the face slot.

The recipe is {"macro": {...}, "targets": {"nose/nose-volume-incr": 0.6, ...}}. A target named
"bilateral:<category>/<stem>" loads both the l- and r- versions.
"""
import importlib
import json
import sys
from pathlib import Path

import addon_utils
import bpy
import numpy as np

mpfb, recipe_path, out = (Path(a) for a in sys.argv[sys.argv.index("--") + 1:][:3])
recipe = json.loads(recipe_path.read_text())

bpy.context.preferences.filepaths.save_version = 0
bpy.context.preferences.extensions.repos.new(name="MPFB character", module="mpfb_character", custom_directory=str(mpfb.resolve() / "src"))
addon_utils.enable("bl_ext.mpfb_character.mpfb", default_set=True)
human = importlib.import_module("bl_ext.mpfb_character.mpfb.services.humanservice").HumanService
targets = importlib.import_module("bl_ext.mpfb_character.mpfb.services.targetservice").TargetService

for obj in list(bpy.data.objects):
    bpy.data.objects.remove(obj, do_unlink=True)
macro = targets.get_default_macro_info_dict()
macro.update(recipe.get("macro", {}))
body = human.create_human(macro_detail_dict=macro)
for name, weight in recipe.get("targets", {}).items():
    paths = [name]
    if name.startswith("bilateral:"):
        category, stem = name.split(":", 1)[1].split("/")
        paths = [f"{category}/{side}-{stem}" for side in ("l", "r")]
    for rel in paths:
        targets.load_target(body, str(mpfb / "src/mpfb/data/targets" / f"{rel}.target.gz"), weight=weight)
# Keep every vertex: the helper mask would renumber them.
for modifier in body.modifiers:
    modifier.show_viewport = False
bpy.context.view_layer.update()
evaluated = body.evaluated_get(bpy.context.evaluated_depsgraph_get())
mesh = evaluated.to_mesh()
positions = np.array([v.co[:] for v in mesh.vertices], np.float64) @ np.array(body.matrix_world.to_3x3()).T + np.array(body.matrix_world.translation)
evaluated.to_mesh_clear()
assert len(positions) == 19158, len(positions)
np.save(out, positions)
print("MPFB_DUMP", out, positions.shape, positions[:, 2].min(), positions[:, 2].max(), flush=True)
