"""Puts a GLB's materials in the product's slot order: body first, face second (#10029).

usage: python3 -I art/characters/quaternius/order_slots.py <in.glb> <out.glb> <body-material> <face-material>

CharacterStyleTest binds slot 0 as the body and slot 1 as the face (whose texture it swaps), so
the named materials move to indices 0 and 1 and every primitive is remapped.
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402
from retarget import write  # noqa: E402

source, out, body, face = sys.argv[1:5]
doc, binary = glb.read(source)
names = [m.get("name") for m in doc["materials"]]
order = [names.index(body), names.index(face)] + [i for i in range(len(names)) if names[i] not in (body, face)]
remap = {old: new for new, old in enumerate(order)}
doc["materials"] = [doc["materials"][i] for i in order]
for mesh in doc["meshes"]:
    for prim in mesh["primitives"]:
        if "material" in prim:
            prim["material"] = remap[prim["material"]]
write(out, doc, binary)
print("slots", [m.get("name") for m in doc["materials"]])
