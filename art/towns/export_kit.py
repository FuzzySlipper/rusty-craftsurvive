"""Export the town kit as separately placeable runtime pieces with shared textures.

blender -b --factory-startup --python art/towns/export_kit.py -- \
  --output content/game/towns

Writes `kit/<piece>.glb` (each at its own origin, glTF Y-up, front facing +Z),
`kit/textures/<material>.png` (one shared copy per material image) and `kit.json`
(each piece's mesh, material slots, bounds and anchors). Layouts, palettes and
services are authored product data; this script only produces the kit.
"""

from __future__ import annotations

import argparse
import copy
import hashlib
import json
import struct
import sys
from pathlib import Path

import bpy
from mathutils import Vector

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from buildings import barrel, building
from export_runtime import bake, export_runtime, game_point
from geometry import MATERIALS, materials
from landmarks import beacon, landing_bell
from scenery import ferry_shelter, harbour_crane, market_canopy, pier, quay_wall

# Shared material images are downscaled once; the fixed town camera never sees
# a texel of the 1254 px sources at its working distances.
TEXTURE_SIZE = 1024
GLB_MAGIC, JSON_CHUNK, BIN_CHUNK = b"glTF", 0x4E4F534A, 0x004E4942
LAMP_NAMES = ("lantern", "porch.lamp", "interior.lamp", "interior.side.lamp", "ferry.lamp", "beacon.lantern")

# Pieces other than buildings, each built at its own origin.
LANDMARKS = {
    "crane": lambda: harbour_crane((0, 0, 0)),
    "beacon": lambda: beacon((0, 0, 0), 0),
    "ferry-shelter": lambda: ferry_shelter((0, 0, 0), 0, lights=False),
    "landing-bell": lambda: landing_bell((0, 0, 0), 0),
}
DRESSING = {
    "pier-short": lambda: pier("pier", (0, 0, 0), 7, 3.0),
    "pier-long": lambda: pier("pier", (0, 0, 0), 9, 3.3),
    "quay-wall": lambda: quay_wall((0, 0, 0)),
    "market-canopy": lambda: market_canopy((0, 0, 0), 0),
    "barrel": lambda: barrel("barrel", (0, 0, 0.04), scale=0.95),
}


def reset(config):
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    # Each piece names its material slots exactly as the recipe does; no ".001" copies.
    for material in list(bpy.data.materials):
        bpy.data.materials.remove(material)
    for data in list(bpy.data.meshes):
        bpy.data.meshes.remove(data)
    MATERIALS.clear()
    materials(copy.deepcopy(config["materials"]), wet=False)


def meshes_of(root):
    return [obj for obj in [root, *root.children_recursive] if obj.type == "MESH"]


def bounds(objects):
    corners = [game_point(obj.matrix_world @ Vector(corner)) for obj in objects for corner in obj.bound_box]
    return {
        "min": [round(min(point[i] for point in corners), 4) for i in range(3)],
        "max": [round(max(point[i] for point in corners), 4) for i in range(3)],
    }


def lamps_of(root):
    return [
        game_point(obj.matrix_world.translation)
        for obj in root.children_recursive
        if obj.type == "EMPTY" and (obj.name.startswith(LAMP_NAMES) or "lantern" in obj.name)
    ]


class Textures:
    """Moves each GLB's embedded images out to one shared, downscaled file per material."""

    def __init__(self, directory: Path):
        self.directory = directory
        self.written: dict[str, str] = {}

    def externalize(self, path: Path) -> list[str]:
        data = path.read_bytes()
        json_length = struct.unpack_from("<I", data, 12)[0]
        document = json.loads(data[20 : 20 + json_length])
        bin_offset = 20 + json_length + 8
        binary = data[bin_offset : bin_offset + struct.unpack_from("<I", data, 20 + json_length)[0]]
        views = document.get("bufferViews", [])
        image_views = set()
        for image in document.get("images", []):
            view = views[image.pop("bufferView")]
            image_views.add(views.index(view))
            start = view.get("byteOffset", 0)
            name = image["name"]
            self.write(name, binary[start : start + view["byteLength"]])
            image.pop("mimeType", None)
            image["uri"] = f"textures/{name}.png"
        # Repack the remaining views without the image bytes and renumber references.
        remap, packed, kept = {}, bytearray(), []
        for index, view in enumerate(views):
            if index in image_views:
                continue
            start = view.get("byteOffset", 0)
            packed += b"\0" * (-len(packed) % 4)
            remap[index] = len(kept)
            view = dict(view, byteOffset=len(packed))
            packed += binary[start : start + view["byteLength"]]
            kept.append(view)
        document["bufferViews"] = kept
        for accessor in document.get("accessors", []):
            if "bufferView" in accessor:
                accessor["bufferView"] = remap[accessor["bufferView"]]
        packed += b"\0" * (-len(packed) % 4)
        document["buffers"] = [{"byteLength": len(packed)}]
        encoded = json.dumps(document, separators=(",", ":")).encode()
        encoded += b" " * (-len(encoded) % 4)
        total = 12 + 8 + len(encoded) + 8 + len(packed)
        path.write_bytes(
            struct.pack("<4sII", GLB_MAGIC, 2, total)
            + struct.pack("<II", len(encoded), JSON_CHUNK)
            + encoded
            + struct.pack("<II", len(packed), BIN_CHUNK)
            + bytes(packed)
        )
        return [material["name"] for material in document.get("materials", [])]

    def write(self, name: str, encoded: bytes):
        digest = hashlib.sha256(encoded).hexdigest()
        if name in self.written:
            if self.written[name] != digest:
                raise SystemExit(f"texture {name} differs between pieces; one material, one image")
            return
        self.written[name] = digest
        self.directory.mkdir(parents=True, exist_ok=True)
        target = self.directory / f"{name}.png"
        target.write_bytes(encoded)
        image = bpy.data.images.load(str(target))
        if image.size[0] > TEXTURE_SIZE:
            image.scale(TEXTURE_SIZE, TEXTURE_SIZE)
        # The Engine's texture resources admit 8-bit RGBA PNGs only; copy into an opaque RGBA image.
        rgba = bpy.data.images.new(name + ".rgba", image.size[0], image.size[1], alpha=True)
        rgba.colorspace_settings.name = image.colorspace_settings.name
        rgba.pixels.foreach_set(image.pixels[:])
        rgba.filepath_raw = str(target)
        rgba.file_format = "PNG"
        rgba.save()
        bpy.data.images.remove(rgba)
        bpy.data.images.remove(image)


def export_piece(output: Path, textures: Textures, name: str, objects, config, pivot=Vector((0, 0, 0))):
    baked = bake(name, objects, pivot)
    path = output / "kit" / f"{name}.glb"
    path.parent.mkdir(parents=True, exist_ok=True)
    export_runtime(path, [baked], config["materials"])
    return f"towns/kit/{name}.glb", textures.externalize(path)


def building_piece(output, textures, config, key):
    recipe = config["buildings"][key]
    reset(config)
    root = building(key, recipe, lights=False)
    bpy.context.view_layer.update()
    sign = next(obj for obj in root.children_recursive if obj.type == "EMPTY" and obj.name.startswith("service.sign."))
    moving = [
        obj
        for obj in sign.children_recursive
        if obj.type == "MESH" and not obj.name.startswith(("sign.bracket", "sign.brace"))
    ]
    architecture = [obj for obj in meshes_of(root) if obj not in moving]
    w, d, h, rise = (recipe[k] for k in ("width", "depth", "eaves", "roof_rise"))
    piece = {
        "kind": "building",
        "bounds": bounds(architecture),
        "entrance": game_point(Vector((0, -d / 2 - 0.2, 1.3))),
        # Deliberately generous selection box; the entrance stays the actual threshold.
        "selection": {"centre": [0, round(h / 2, 4), 0], "size": [w, h, d]},
        "lamps": lamps_of(root),
        "chimneys": [game_point(Vector((w * 0.28, d * 0.20, h + rise * 0.72 + 1.16)))],
    }
    pivot = sign.matrix_world @ Vector((0, -0.19, 0.4))
    axis = sign.matrix_world.to_3x3() @ Vector((0, -1, 0))
    piece["mesh"], piece["materials"] = export_piece(output, textures, key, architecture, config)
    sign_mesh, sign_materials = export_piece(output, textures, key + "-sign", moving, config, pivot)
    piece["sign"] = {"mesh": sign_mesh, "materials": sign_materials, "pivot": game_point(pivot), "axis": game_point(axis)}
    return piece


def simple_piece(output, textures, config, key, build, kind):
    reset(config)
    root = build()
    bpy.context.view_layer.update()
    objects = meshes_of(root)
    piece = {"kind": kind, "bounds": bounds(objects), "lamps": lamps_of(root), "chimneys": []}
    piece["mesh"], piece["materials"] = export_piece(output, textures, key, objects, config)
    return piece


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :])
    config = json.loads((HERE / "recipes.json").read_text())
    textures = Textures(args.output / "kit" / "textures")
    pieces = {key: building_piece(args.output, textures, config, key) for key in config["buildings"]}
    pieces |= {key: simple_piece(args.output, textures, config, key, build, "landmark") for key, build in LANDMARKS.items()}
    pieces |= {key: simple_piece(args.output, textures, config, key, build, "dressing") for key, build in DRESSING.items()}
    kit = {
        "source": "art/towns/export_kit.py from art/towns/recipes.json; Blender Z-up converted to glTF Y-up, front facing +Z",
        "pieces": pieces,
    }
    (args.output / "kit.json").write_text(json.dumps(kit, indent=2) + "\n")
    print("TOWN_KIT_EXPORTED", len(pieces), "pieces", len(textures.written), "textures")


if __name__ == "__main__":
    main()
