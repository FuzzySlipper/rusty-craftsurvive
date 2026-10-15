"""Assembles a character GLB on the shared UAL rig (#10014).

usage: python3 -I art/characters/assemble_character.py <payload.npz> <slots.json> <out.glb> [--with-clips] [--rig <rig.glb>]

--rig names the GLB whose rig (nodes, inverse binds) and clips the character takes: UAL1 by
default, or a skeleton frame (frame.py) for characters fitted to one.

The payload holds one skinned part per material slot, already posed to the UAL rest pose and in
glTF space (metres, Y up, facing +Z): `<slot>_positions`, `_normals`, `_uvs`, `_joints` (indices
into the UAL skin's joint list), `_weights` and `_indices`. slots.json lists the slots in order,
each with a name, a PNG texture path (relative to slots.json) and a roughness. Slot order is the
GLB material order, which the Engine's material bindings (MeshMaterialBinding) index.

The rig, its nodes and its inverse binds are copied from UAL1 as stored, so the Engine's
clip-pack association accepts the pack for this character. Samplers are nearest for both
magnification and minification: a mipmapped minFilter with nearest magFilter panics the runtime on
pair 0a5dfab7b318 (rusty-engine #10011).
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
# UAL1 itself holds the rig the clip packs carry (read only; content/animations is user-owned).
RIG_SOURCE = ROOT / "content/animations/UAL1_Standard.glb"
NEAREST = 9728
CLAMP = 33071
REPEAT = 10497
ROOT_JOINT = "root"
REST_SECONDS = 1.0


def main(payload_path, slots_path, out_path, with_clips=False, rig=RIG_SOURCE):
    payload = np.load(payload_path)
    slots_path = Path(slots_path)
    slots = json.loads(slots_path.read_text())
    rig_doc, rig_bin = glb.read(rig)
    writer = glb.Writer("CraftSurvive assemble_character.py")
    glb.copy_rig(rig_doc, rig_bin, writer, 0)
    # Sampler 0 clamps; sampler 1 repeats around (cylindrically mapped shells cross their seam).
    writer.doc["samplers"] = [{"magFilter": NEAREST, "minFilter": NEAREST, "wrapS": CLAMP, "wrapT": CLAMP},
                              {"magFilter": NEAREST, "minFilter": NEAREST, "wrapS": REPEAT, "wrapT": CLAMP}]
    writer.doc["textures"], writer.doc["materials"] = [], []
    primitives = []
    joint_count = len(rig_doc["skins"][0]["joints"])
    for index, slot in enumerate(slots):
        name = slot["name"]
        weights = payload[f"{name}_weights"].astype(np.float32)
        weights /= weights.sum(axis=1, keepdims=True)
        joints = payload[f"{name}_joints"].astype(np.uint16)
        assert joints.max() < joint_count, name
        attributes = {
            "POSITION": writer.accessor(payload[f"{name}_positions"].astype(np.float32), glb.ARRAY_BUFFER, bounds=True),
            "NORMAL": writer.accessor(payload[f"{name}_normals"].astype(np.float32), glb.ARRAY_BUFFER),
            "TEXCOORD_0": writer.accessor(payload[f"{name}_uvs"].astype(np.float32), glb.ARRAY_BUFFER),
            "JOINTS_0": writer.accessor(joints, glb.ARRAY_BUFFER),
            "WEIGHTS_0": writer.accessor(weights, glb.ARRAY_BUFFER),
        }
        indices = writer.accessor(payload[f"{name}_indices"].astype(np.uint32).reshape(-1, 1), glb.ELEMENT_ARRAY_BUFFER)
        png = (slots_path.parent / slot["texture"]).read_bytes()  # absolute paths pass through
        image = writer.image(png, name)
        writer.doc["textures"].append({"sampler": 1 if slot.get("wrap") == "repeat" else 0, "source": image})
        writer.doc["materials"].append({
            "name": name,
            "pbrMetallicRoughness": {"baseColorTexture": {"index": index}, "metallicFactor": 0, "roughnessFactor": slot.get("roughness", 0.85)},
            **({"doubleSided": True} if slot.get("doubleSided") else {}),
        })
        primitives.append({"attributes": attributes, "indices": indices, "material": index})
    writer.doc["meshes"] = [{"name": "Character", "primitives": primitives}]
    # The Engine imports a GLB without clips through its static parser, which refuses skins, and
    # derives no rig to bind a clip pack to (pair 0a5dfab7b318). A still "rest" clip on the root
    # keeps the character animated and in place, so the pack's clips bind to it.
    root = next(i for i, node in enumerate(writer.doc["nodes"]) if node.get("name") == ROOT_JOINT)
    times = writer.accessor(np.array([[0.0], [REST_SECONDS]], np.float32), bounds=True)
    still = writer.accessor(np.array([writer.doc["nodes"][root].get("translation", [0, 0, 0])] * 2, np.float32))
    writer.doc["animations"] = [{"name": "rest", "channels": [{"sampler": 0, "target": {"node": root, "path": "translation"}}],
                                 "samplers": [{"input": times, "output": still, "interpolation": "LINEAR"}]}]
    if with_clips:
        # Offline previews only: the product attaches the clip pack through the Engine instead.
        names = {"Idle_Loop": "idle", "Walk_Loop": "walk", "Idle_Talking_Loop": "talk", "Walk_Formal_Loop": "walk_formal"}
        glb.copy_animations(rig_doc, rig_bin, writer, [a["name"] for a in rig_doc["animations"] if a["name"] in names])
        for animation in writer.doc["animations"]:
            animation["name"] = names.get(animation["name"], animation["name"])
    writer.write(out_path)
    triangles = sum(len(payload[f"{s['name']}_indices"]) // 3 for s in slots)
    print(out_path, Path(out_path).stat().st_size, "bytes,", triangles, "triangles,", len(slots), "slots")


extra = sys.argv[4:]
main(*sys.argv[1:4], with_clips="--with-clips" in extra, rig=Path(extra[extra.index("--rig") + 1]) if "--rig" in extra else RIG_SOURCE)
