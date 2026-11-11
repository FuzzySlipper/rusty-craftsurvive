"""Minimal GLB reading and writing for the character kit (#10014).

The Engine only binds a clip pack to a character whose rig matches it exactly: the same joints,
local rest matrices and inverse binds (rusty-engine render-model `is_clip_compatible_with`). These
helpers copy a rig's nodes and inverse binds as stored, so a rebuilt GLB keeps the source's rig
hash, and write fresh meshes, materials, images and animations beside them.
"""
import json
import struct

import numpy as np

COMPONENT = {5120: np.int8, 5121: np.uint8, 5122: np.int16, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}
COMPONENT_ID = {np.dtype(v): k for k, v in COMPONENT.items()}
WIDTH = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}
TYPE_FOR = {1: "SCALAR", 2: "VEC2", 3: "VEC3", 4: "VEC4", 16: "MAT4"}
ARRAY_BUFFER, ELEMENT_ARRAY_BUFFER = 34962, 34963


def read(path):
    """The JSON chunk and the BIN chunk of a GLB."""
    data = open(path, "rb").read()
    magic, version, _ = struct.unpack_from("<III", data, 0)
    assert magic == 0x46546C67 and version == 2, path
    length, kind = struct.unpack_from("<II", data, 12)
    assert kind == 0x4E4F534A
    doc = json.loads(data[20:20 + length])
    offset = 20 + length
    binary = b""
    if offset < len(data):
        blength, bkind = struct.unpack_from("<II", data, offset)
        assert bkind == 0x004E4942
        binary = data[offset + 8:offset + 8 + blength]
    return doc, binary


def accessor(doc, binary, index):
    """One accessor's values as a numpy array (count x width), copied."""
    acc = doc["accessors"][index]
    view = doc["bufferViews"][acc["bufferView"]]
    dtype = np.dtype(COMPONENT[acc["componentType"]])
    width = WIDTH[acc["type"]]
    start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
    stride = view.get("byteStride", 0) or dtype.itemsize * width
    count = acc["count"]
    if stride == dtype.itemsize * width:
        flat = np.frombuffer(binary, dtype=dtype, count=count * width, offset=start)
        return flat.reshape(count, width).copy()
    rows = [np.frombuffer(binary, dtype=dtype, count=width, offset=start + i * stride) for i in range(count)]
    return np.array(rows, dtype=dtype)


def image_bytes(doc, binary, index):
    view = doc["bufferViews"][doc["images"][index]["bufferView"]]
    start = view.get("byteOffset", 0)
    return binary[start:start + view["byteLength"]]


class Writer:
    """Builds one GLB: a JSON document plus a single packed BIN buffer."""

    def __init__(self, generator):
        self.doc = {"asset": {"version": "2.0", "generator": generator}, "buffers": [], "bufferViews": [], "accessors": []}
        self.chunks = []
        self.length = 0

    def _view(self, payload, target=None):
        pad = (-self.length) % 4
        if pad:
            self.chunks.append(b"\0" * pad)
            self.length += pad
        view = {"buffer": 0, "byteOffset": self.length, "byteLength": len(payload)}
        if target:
            view["target"] = target
        self.chunks.append(payload)
        self.length += len(payload)
        self.doc["bufferViews"].append(view)
        return len(self.doc["bufferViews"]) - 1

    def accessor(self, array, target=None, bounds=False, normalized=False):
        """Adds an accessor for a 2-D array (count x width), exactly as its dtype stores it."""
        array = np.ascontiguousarray(array)
        if array.ndim == 1:
            array = array.reshape(-1, 1)
        acc = {
            "bufferView": self._view(array.tobytes(), target),
            "componentType": COMPONENT_ID[array.dtype],
            "count": int(array.shape[0]),
            "type": TYPE_FOR[array.shape[1]],
        }
        if normalized:
            acc["normalized"] = True
        if bounds:
            acc["min"] = [float(v) for v in array.min(axis=0)]
            acc["max"] = [float(v) for v in array.max(axis=0)]
        self.doc["accessors"].append(acc)
        return len(self.doc["accessors"]) - 1

    def image(self, png, name):
        self.doc.setdefault("images", []).append({"bufferView": self._view(png), "mimeType": "image/png", "name": name})
        return len(self.doc["images"]) - 1

    def write(self, path):
        pad = (-self.length) % 4
        binary = b"".join(self.chunks) + b"\0" * pad
        self.doc["buffers"] = [{"byteLength": len(binary)}]
        text = json.dumps(self.doc, separators=(",", ":")).encode()
        text += b" " * ((-len(text)) % 4)
        total = 12 + 8 + len(text) + 8 + len(binary)
        with open(path, "wb") as out:
            out.write(struct.pack("<III", 0x46546C67, 2, total))
            out.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
            out.write(struct.pack("<II", len(binary), 0x004E4942) + binary)


def copy_rig(source_doc, source_bin, writer, mesh_index):
    """Copies the source's nodes, scene and first skin; the skinned node points at mesh_index."""
    writer.doc["nodes"] = json.loads(json.dumps(source_doc["nodes"]))
    for node in writer.doc["nodes"]:
        if "mesh" in node:
            node["mesh"] = mesh_index
    writer.doc["scenes"] = source_doc["scenes"]
    writer.doc["scene"] = source_doc.get("scene", 0)
    skin = dict(source_doc["skins"][0])
    skin["inverseBindMatrices"] = writer.accessor(accessor(source_doc, source_bin, skin["inverseBindMatrices"]))
    writer.doc["skins"] = [skin]


def copy_animations(source_doc, source_bin, writer, names):
    """Copies the named animations, keyframes as stored."""
    out = []
    by_name = {a["name"]: a for a in source_doc["animations"]}
    for name in names:
        anim = by_name[name]
        samplers = []
        for sampler in anim["samplers"]:
            samplers.append({
                "input": writer.accessor(accessor(source_doc, source_bin, sampler["input"]), bounds=True),
                "output": writer.accessor(accessor(source_doc, source_bin, sampler["output"])),
                "interpolation": sampler.get("interpolation", "LINEAR"),
            })
        out.append({"name": name, "channels": anim["channels"], "samplers": samplers})
    writer.doc["animations"] = writer.doc.get("animations", []) + out
