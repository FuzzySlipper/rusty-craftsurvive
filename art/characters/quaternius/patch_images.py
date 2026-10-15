"""Replaces a GLB's embedded images by name and gives its materials a matte painted finish (#10029).

usage: python3 -I art/characters/quaternius/patch_images.py <in.glb> <out.glb> <png-dir> [<png-dir> ...]

Every <name>.png in the directories replaces the embedded image of that name (later
directories win). Everything else (nodes, skin, meshes) is kept as stored, so the rig stays
identical and its clip pack still binds.

Painted surfaces carry their own light, so every material becomes matte: metallic 0, a
constant roughness, and no metallic-roughness or normal map (the packs' normal maps add
photographic detail the style sheet rules out).
"""
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402
from retarget import write  # noqa: E402

ROUGHNESS = 0.9

source, out, *dirs = sys.argv[1:]
doc, binary = glb.read(source)
replacements = {}
for d in dirs:
    replacements.update({p.stem: p.read_bytes() for p in Path(d).glob("*.png")})

chunks, length = [binary], len(binary)
replaced = []
for image in doc["images"]:
    png = replacements.get(image.get("name"))
    if png is None:
        continue
    pad = (-length) % 4
    chunks.append(b"\0" * pad)
    length += pad
    doc["bufferViews"].append({"buffer": 0, "byteOffset": length, "byteLength": len(png)})
    chunks.append(png)
    length += len(png)
    image["bufferView"] = len(doc["bufferViews"]) - 1
    image["mimeType"] = "image/png"
    replaced.append(image["name"])

for material in doc["materials"]:
    pbr = material.setdefault("pbrMetallicRoughness", {})
    pbr.pop("metallicRoughnessTexture", None)
    pbr["metallicFactor"] = 0
    pbr["roughnessFactor"] = ROUGHNESS
    material.pop("normalTexture", None)

binary = b"".join(chunks) + b"\0" * ((-length) % 4)
doc["buffers"] = [{"byteLength": len(binary)}]
write(out, doc, binary)
print("replaced", replaced)
