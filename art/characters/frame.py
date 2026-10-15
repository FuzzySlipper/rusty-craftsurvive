"""Derives a skeleton frame from UAL1: other bone lengths, the same clips (#10028).

usage: python3 -I art/characters/frame.py <ual.glb> <frame.json> <out.glb>

The Engine binds a clip pack only to an identical rig, so every character shares its pack's
bone lengths. A frame is a second rig with its own copy of the clips: leg, torso, neck or arm
segments scaled. Clip keys follow:
- a scaled joint's translation keys scale with its rest offset;
- the pelvis keys shift up or down by the change in leg height, so feet stay on the ground.

Rotations are unchanged, so the motion is UAL's on a longer or shorter body. Inverse binds are
recomputed from the new rest pose.

frame.json: {"name": "long-legs", "scale": {"legs": 1.12, "torso": 1.0, "neck": 1.0, "arms": 1.0}}.
The output keeps UAL's mesh, skin, materials and only the clips the product plays (UAL names; the
packs rename them). A character fitted to the frame and the frame's pack then agree exactly.
"""
import json
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402
from fit_to_ual import world_matrices  # noqa: E402

CLIPS = ["Idle_Loop", "Walk_Loop", "Idle_Talking_Loop", "Walk_Formal_Loop"]
GROUPS = {
    "legs": ["calf_l", "calf_r", "foot_l", "foot_r"],
    "torso": ["spine_02", "spine_03", "neck_01"],
    "neck": ["Head"],
    "arms": ["lowerarm_l", "lowerarm_r", "hand_l", "hand_r"],
}


def main(source, spec_path, out):
    spec = json.loads(Path(spec_path).read_text())
    doc, binary = glb.read(source)
    nodes = json.loads(json.dumps(doc["nodes"]))
    by_name = {n["name"]: i for i, n in enumerate(nodes)}
    factor = {}
    for group, s in spec["scale"].items():
        for name in GROUPS[group]:
            factor[by_name[name]] = s
    before, parent_of = world_matrices({"nodes": nodes})
    for i, s in factor.items():
        nodes[i]["translation"] = [v * s for v in nodes[i].get("translation", [0, 0, 0])]
    # Keep the feet on the ground: raise the pelvis by the drop of the lower ankle.
    after, _ = world_matrices({"nodes": nodes})
    ankles = [by_name["foot_l"], by_name["foot_r"]]
    drop = min(before[a][1, 3] for a in ankles) - min(after[a][1, 3] for a in ankles)
    pelvis = by_name["pelvis"]
    parent = after[parent_of[pelvis]] if pelvis in parent_of else np.eye(4)
    lift = np.linalg.inv(parent[:3, :3]) @ np.array([0, drop, 0])
    nodes[pelvis]["translation"] = list(np.array(nodes[pelvis].get("translation", [0, 0, 0])) + lift)
    after, _ = world_matrices({"nodes": nodes})

    writer = glb.Writer(f"CraftSurvive frame.py: UAL1 Standard (CC0, Quaternius) as frame {spec['name']}")
    writer.doc["nodes"] = nodes
    writer.doc["scenes"], writer.doc["scene"] = doc["scenes"], doc.get("scene", 0)
    skin = dict(doc["skins"][0])
    ibm = np.array([np.linalg.inv(after[j]).T for j in skin["joints"]], np.float32).reshape(-1, 16)
    skin["inverseBindMatrices"] = writer.accessor(ibm)
    writer.doc["skins"] = [skin]
    primitives = []
    for prim in doc["meshes"][0]["primitives"]:
        attributes = {name: writer.accessor(glb.accessor(doc, binary, prim["attributes"][name]), glb.ARRAY_BUFFER, bounds=name == "POSITION")
                      for name in ("POSITION", "NORMAL", "JOINTS_0", "WEIGHTS_0")}
        primitives.append({"attributes": attributes, "indices": writer.accessor(glb.accessor(doc, binary, prim["indices"]), glb.ELEMENT_ARRAY_BUFFER),
                           "material": prim["material"]})
    writer.doc["meshes"] = [{"name": doc["meshes"][0]["name"], "primitives": primitives}]
    writer.doc["materials"] = doc["materials"]
    animations = []
    for anim in (a for a in doc["animations"] if a["name"] in CLIPS):
        samplers = []
        for ch in anim["channels"]:
            sampler = anim["samplers"][ch["sampler"]]
            values = glb.accessor(doc, binary, sampler["output"]).astype(np.float32)
            node, path = ch["target"]["node"], ch["target"]["path"]
            if path == "translation" and node in factor:
                values = values * factor[node]
            elif path == "translation" and node == pelvis:
                values = values + lift.astype(np.float32)
            samplers.append({"input": writer.accessor(glb.accessor(doc, binary, sampler["input"]), bounds=True),
                             "output": writer.accessor(values), "interpolation": sampler.get("interpolation", "LINEAR")})
        animations.append({"name": anim["name"], "samplers": samplers,
                           "channels": [{"sampler": k, "target": ch["target"]} for k, ch in enumerate(anim["channels"])]})
    writer.doc["animations"] = animations
    writer.write(out)
    legs = lambda w: float(w[by_name["pelvis"]][1, 3])  # noqa: E731
    print(out, spec["name"], f"pelvis {legs(before):.3f} -> {legs(after):.3f} m", f"lift {drop:+.3f} m")


if __name__ == "__main__":
    main(*sys.argv[1:4])
