"""Writes the character clip pack: a few of Quaternius's CC0 UAL1 Standard clips (#10014).

usage: python3 -I art/characters/make_clip_pack.py <source.glb> <out.glb> <character>

Reads content/animations/UAL1_Standard.glb (user-owned; read only) and writes a pack with its rig (nodes and inverse binds as stored), its
mannequin mesh (which the Engine needs to import the rig) and only the clips the product plays.
Characters built by assemble_character.py share this rig exactly, so the Engine's clip-pack
association (AnimationClipPackAssociationRequest) accepts the pack for them.

On pair 0a5dfab7b318 only one primary mesh animates per clip-pack identity: a second mesh
associated with the same pack bytes draws in its bind pose (rusty-engine #10018). Until that is
fixed each character gets its own pack, byte-distinct only by naming the character in the
generator string.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
SOURCE, OUT, CHARACTER = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3]
# UAL clip -> the id the product plays. Engine animation graphs accept only lowercase stable ids
# for every clip of an asset (render-presentation validate_id), so the pack renames them.
CLIPS = {"Idle_Loop": "idle", "Walk_Loop": "walk", "Idle_Talking_Loop": "talk", "Walk_Formal_Loop": "walk_formal"}
MESH_ATTRIBUTES = ["POSITION", "NORMAL", "JOINTS_0", "WEIGHTS_0"]


def main():
    doc, binary = glb.read(SOURCE)
    writer = glb.Writer(f"CraftSurvive make_clip_pack.py (UAL1 Standard, CC0, Quaternius) for {CHARACTER}")
    glb.copy_rig(doc, binary, writer, 0)
    primitives = []
    for prim in doc["meshes"][0]["primitives"]:
        attributes = {}
        for name in MESH_ATTRIBUTES:
            values = glb.accessor(doc, binary, prim["attributes"][name])
            attributes[name] = writer.accessor(values, glb.ARRAY_BUFFER, bounds=name == "POSITION")
        indices = writer.accessor(glb.accessor(doc, binary, prim["indices"]), glb.ELEMENT_ARRAY_BUFFER)
        primitives.append({"attributes": attributes, "indices": indices, "material": prim["material"]})
    writer.doc["meshes"] = [{"name": doc["meshes"][0]["name"], "primitives": primitives}]
    writer.doc["materials"] = doc["materials"]
    glb.copy_animations(doc, binary, writer, list(CLIPS))
    for animation in writer.doc["animations"]:
        animation["name"] = CLIPS[animation["name"]]
    OUT.parent.mkdir(parents=True, exist_ok=True)
    writer.write(OUT)
    print(OUT, OUT.stat().st_size, "bytes,", len(CLIPS), "clips")


main()
