"""Bakes repainted views onto each base-colour texture of a multi-material character (#10029).

usage: blender -b --factory-startup --python art/characters/quaternius/paint_bake.py -- <in.glb> <work> <out-dir>

`work` is a view_paint.py render directory (asset-pipeline tools/texture-edit): view_N.png,
pos_N.exr, cameras.json, plus the repainted edit_N.png. view_paint's own bake assumes one
material and merges everything into it; a character carries several textures on overlapping
UV spaces (outfit, head, hair, eyes), so this bakes each base-colour image separately:
only the faces whose material uses that image write to it. Each texel takes the
facing-weighted blend of every view that sees it unoccluded, and its edited colour replaces
the original (texels no view sees keep theirs). Writes <out-dir>/<image name>.png at each
image's own size; patch_images.py puts them back into the GLB.
"""
import glob
import json
import os
import sys

import numpy as np

sys.path.insert(0, "/home/agent/dev/asset-pipeline/tools/texture-edit")
import view_paint as vp  # noqa: E402  (its helpers; its command dispatch is argv-gated)

import bpy  # noqa: E402

args = sys.argv[sys.argv.index("--") + 1:]
src, work, out_dir = args[0], args[1], args[2]
os.makedirs(out_dir, exist_ok=True)

objs = vp.import_glb(src)
vp.cycles(1)
meta = json.load(open(os.path.join(work, "cameras.json")))
edits = {os.path.basename(p): p for p in glob.glob(os.path.join(work, "edit_*.png"))}

# Each base-colour image and the (object, slot) pairs that use it.
users = {}
for o in objs:
    for i, slot in enumerate(o.material_slots):
        p = vp.principled(slot.material)
        node = vp.upstream_image(p.inputs["Base Color"]) if p else None
        if node and node.image:
            users.setdefault(node.image.name, (node.image, []))[1].append((o, i))

original = {o: [s.material for s in o.material_slots] for o in objs}


def isolated_bake(image_users, output, target):
    """Bakes geometry `output` into target, only on the given (object, slot) pairs."""
    mat = vp.geometry_material("bake_" + output, output)
    node = mat.node_tree.nodes.new("ShaderNodeTexImage"); node.image = target
    mat.node_tree.nodes.active = node
    skip = vp.geometry_material("skip_" + output, output)
    dummy = vp.new_image("dummy", 8, True)
    sn = skip.node_tree.nodes.new("ShaderNodeTexImage"); sn.image = dummy
    skip.node_tree.nodes.active = sn
    chosen = {}
    for o, i in image_users:
        chosen.setdefault(o, set()).add(i)
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        for i in range(len(o.material_slots)):
            o.material_slots[i].link = "OBJECT"
            o.material_slots[i].material = mat if i in chosen.get(o, ()) else skip
        o.select_set(o in chosen)
    bpy.context.view_layer.objects.active = next(iter(chosen))
    s = bpy.context.scene
    s.render.bake.margin = 8
    s.render.bake.use_clear = False
    bpy.ops.object.bake(type="EMIT")


report = {}
for name, (image, pairs) in users.items():
    w, h = image.size
    pos_img, nrm_img = vp.new_image("texel_pos", w, True), vp.new_image("texel_nrm", w, True)
    isolated_bake(pairs, "Position", pos_img)
    isolated_bake(pairs, "Normal", nrm_img)
    P, N = vp.pixels(pos_img)[..., :3], vp.pixels(nrm_img)[..., :3]
    valid = np.linalg.norm(N, axis=-1) > 0.5
    base = vp.pixels(image).copy()
    acc = np.zeros(base.shape[:2] + (3,), np.float32)
    wsum = np.zeros(base.shape[:2], np.float32)
    eps = meta["diag"] * 0.004
    for cam in meta["cameras"]:
        if cam["edit"] not in edits:
            continue
        S = cam["size"]
        edit = vp.pixels(vp.load(edits[cam["edit"]], S))
        posr = vp.pixels(vp.load(os.path.join(work, cam["position"]), non_color=True))
        M = np.array(cam["matrix_world"], np.float64)
        pc = (np.c_[P.reshape(-1, 3), np.ones(P.shape[0] * P.shape[1])] @ np.linalg.inv(M).T)[:, :3]
        half = cam["ortho_scale"] / 2
        px = ((pc[:, 0] / half) * 0.5 + 0.5) * S
        py = ((pc[:, 1] / half) * 0.5 + 0.5) * S
        ix, iy = np.clip(px.astype(int), 0, S - 1), np.clip(py.astype(int), 0, S - 1)
        inside = (px >= 0) & (px < S) & (py >= 0) & (py < S)
        seen = posr[iy, ix]
        visible = inside & (seen[:, 3] > 0.5) & (np.linalg.norm(seen[:, :3] - P.reshape(-1, 3), axis=1) < eps)
        facing = np.clip(-(N.reshape(-1, 3) @ -M[:3, 2]), 0, 1)
        weight = np.where(visible & valid.reshape(-1), facing ** 2, 0.0)
        acc += (weight[:, None] * edit[iy, ix, :3]).reshape(acc.shape)
        wsum += weight.reshape(wsum.shape)
    hit = wsum > 1e-4
    rgb = base[..., :3]
    rgb[hit] = np.clip(acc[hit] / wsum[hit][:, None], 0, 1)
    base[..., :3] = rgb
    result = bpy.data.images.new(name + "_painted", w, h, alpha=True)
    result.pixels.foreach_set(base.reshape(-1))
    result.filepath_raw = os.path.join(out_dir, name + ".png")
    result.file_format = "PNG"
    result.save()
    report[name] = {"size": [w, h], "texels_valid": int(valid.sum()), "texels_painted": int(hit.sum())}
    for o, mats in original.items():
        for i, m in enumerate(mats):
            o.material_slots[i].material = m

json.dump(report, open(os.path.join(out_dir, "bake.json"), "w"), indent=1)
print("PAINTED", json.dumps(report))
