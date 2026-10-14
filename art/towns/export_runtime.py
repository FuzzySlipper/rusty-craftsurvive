"""Runtime glTF helpers shared by the kit exporter (export_kit.py): baking a piece's
meshes into one object, writing portable glTF material factors, and converting Blender
Z-up points to the game's Y-up frame.
"""

from __future__ import annotations

import json
import struct
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from geometry import MATERIALS, color
from build import export


def game_point(point):
    return [
        round(float(point[0]), 5),
        round(float(point[2]), 5),
        round(float(-point[1]), 5),
    ]


def bake(name, objects, pivot=Vector((0, 0, 0))):
    depsgraph = bpy.context.evaluated_depsgraph_get()
    baked = []
    for source in objects:
        data = bpy.data.meshes.new_from_object(
            source.evaluated_get(depsgraph),
            preserve_all_data_layers=True,
            depsgraph=depsgraph,
        )
        data.transform(Matrix.Translation(-pivot) @ source.matrix_world)
        obj = bpy.data.objects.new(name + ".part", data)
        bpy.context.collection.objects.link(obj)
        baked.append(obj)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in baked:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = baked[0]
    bpy.ops.object.join()
    joined = bpy.context.object
    joined.name = name
    return joined


def export_runtime(path, objects, definitions):
    """Make the recipe's tint and prop cutouts explicit in portable glTF PBR.

    Blender's node exporter drops the texture multiply and treats every linked
    alpha as blending. Leaves are cutouts; trunks/boats are opaque. Keeping
    those depth-writing avoids the distant fog showing through nearby trees.
    Image and geometry bytes remain unchanged. Timber palettes are applied at
    runtime (content/game/towns/palettes.json), not baked into separate files.
    """
    export(path, objects)
    data = path.read_bytes()
    json_length = struct.unpack_from("<I", data, 12)[0]
    document = json.loads(data[20 : 20 + json_length])
    names = {material.name: key for key, material in MATERIALS.items()}
    for material in document["materials"]:
        key = names[material["name"]]
        definition = definitions.get(key, {})
        if definition.get("tint"):
            material["pbrMetallicRoughness"]["baseColorFactor"] = color(
                definition["tint"]
            )
        if ".prop-mesh.json:" in key:
            material["alphaMode"] = "MASK" if key.endswith(":leaves") else "OPAQUE"
            if material["alphaMode"] == "MASK":
                material["alphaCutoff"] = 0.4
    encoded = json.dumps(document, separators=(",", ":")).encode()
    encoded += b" " * (-len(encoded) % 4)
    tail = data[20 + json_length :]
    path.write_bytes(
        struct.pack(
            "<4sIIII",
            b"glTF",
            2,
            20 + len(encoded) + len(tail),
            len(encoded),
            0x4E4F534A,
        )
        + encoded
        + tail
    )
