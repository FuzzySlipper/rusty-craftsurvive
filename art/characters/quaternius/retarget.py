"""Retargets UAL1 clips onto a Quaternius character's rig and writes its clip pack (#10029).

usage: python3 -I art/characters/quaternius/retarget.py <ual.glb> <character.glb> <pack.glb>

Quaternius's Universal Base Characters use UAL's joint names and hierarchy but their own bone
lengths and rest rotations (up to about 23 degrees apart at the neck). The Engine binds a clip
pack only to an identical rig, so the pack is the character's own GLB (same nodes, skin and
inverse binds) plus the retargeted clips.

Each joint's motion is transferred in world space, relative to its rest pose:
    G_target(t) = G_source(t) * inverse(G_source_rest) * G_target_rest   (rotations only)
then converted back to a local rotation under the target's animated parent. Bone lengths stay
the character's. The pelvis keeps UAL's movement, scaled by the ratio of pelvis heights so
the feet stay on the ground; the root is left at rest (the clips are in place).
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import glb  # noqa: E402

# UAL clip -> the lowercase id the product plays (Engine graphs accept only lowercase ids).
CLIPS = {"Idle_Loop": "idle", "Walk_Loop": "walk", "Idle_Talking_Loop": "talk"}
PELVIS = "pelvis"


def quat_to_matrix(q):
    x, y, z, w = q
    return np.array([
        [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
    ])


def matrix_to_quat(m):
    t = np.trace(m)
    if t > 0:
        s = np.sqrt(t + 1) * 2
        q = [(m[2, 1] - m[1, 2]) / s, (m[0, 2] - m[2, 0]) / s, (m[1, 0] - m[0, 1]) / s, s / 4]
    elif m[0, 0] > m[1, 1] and m[0, 0] > m[2, 2]:
        s = np.sqrt(1 + m[0, 0] - m[1, 1] - m[2, 2]) * 2
        q = [s / 4, (m[0, 1] + m[1, 0]) / s, (m[0, 2] + m[2, 0]) / s, (m[2, 1] - m[1, 2]) / s]
    elif m[1, 1] > m[2, 2]:
        s = np.sqrt(1 + m[1, 1] - m[0, 0] - m[2, 2]) * 2
        q = [(m[0, 1] + m[1, 0]) / s, s / 4, (m[1, 2] + m[2, 1]) / s, (m[0, 2] - m[2, 0]) / s]
    else:
        s = np.sqrt(1 + m[2, 2] - m[0, 0] - m[1, 1]) * 2
        q = [(m[0, 2] + m[2, 0]) / s, (m[1, 2] + m[2, 1]) / s, s / 4, (m[1, 0] - m[0, 1]) / s]
    q = np.array(q)
    return q / np.linalg.norm(q)


def orthonormal(m):
    u, _, vt = np.linalg.svd(m)
    return u @ vt


class Rig:
    """A GLB's node hierarchy with rest transforms, indexed by node name."""

    def __init__(self, doc):
        self.nodes = doc["nodes"]
        self.parent = {c: i for i, n in enumerate(self.nodes) for c in n.get("children", [])}
        self.index = {n.get("name"): i for i, n in enumerate(self.nodes)}
        self.joints = [self.nodes[j]["name"] for j in doc["skins"][0]["joints"]]

    def rest(self, i):
        n = self.nodes[i]
        r = quat_to_matrix(n.get("rotation", [0, 0, 0, 1]))
        s = np.diag(n.get("scale", [1, 1, 1]))
        return np.array(n.get("translation", [0, 0, 0]), float), r, s

    def order(self):
        """Node indices, parents before children."""
        seen, out = set(), []

        def visit(i):
            if i in seen:
                return
            if i in self.parent:
                visit(self.parent[i])
            seen.add(i)
            out.append(i)

        for i in range(len(self.nodes)):
            visit(i)
        return out

    def globals(self, rotations=None, translations=None):
        """World matrices (4x4) with optional local rotation / translation overrides by node index."""
        rotations, translations = rotations or {}, translations or {}
        world = {}
        for i in self.order():
            t, r, s = self.rest(i)
            m = np.eye(4)
            m[:3, :3] = rotations.get(i, r) @ s
            m[:3, 3] = translations.get(i, t)
            world[i] = (world[self.parent[i]] if i in self.parent else np.eye(4)) @ m
        return world


def sample(times, keys, values, kind):
    """Linear (or slerp) samples of a channel at times."""
    out = []
    for t in times:
        k = int(np.clip(np.searchsorted(keys, t, side="right") - 1, 0, len(keys) - 1))
        if k == len(keys) - 1 or keys[k + 1] == keys[k]:
            out.append(values[k])
            continue
        f = (t - keys[k]) / (keys[k + 1] - keys[k])
        a, b = values[k], values[k + 1]
        if kind == "rotation":
            if np.dot(a, b) < 0:
                b = -b
            q = a * (1 - f) + b * f
            out.append(q / np.linalg.norm(q))
        else:
            out.append(a * (1 - f) + b * f)
    return np.array(out)


def retarget(src_doc, src_bin, tgt_doc):
    src, tgt = Rig(src_doc), Rig(tgt_doc)
    src_rest, tgt_rest = src.globals(), tgt.globals()
    pelvis_ratio = tgt_rest[tgt.index[PELVIS]][1, 3] / src_rest[src.index[PELVIS]][1, 3]
    by_name = {a["name"]: a for a in src_doc["animations"]}
    clips = []
    for ual, clip_id in CLIPS.items():
        anim = by_name[ual]
        channels = {}
        times = set()
        for ch in anim["channels"]:
            sampler = anim["samplers"][ch["sampler"]]
            keys = glb.accessor(src_doc, src_bin, sampler["input"]).ravel()
            values = glb.accessor(src_doc, src_bin, sampler["output"]).astype(float)
            channels[(ch["target"]["node"], ch["target"]["path"])] = (keys, values)
            if ch["target"]["path"] == "rotation":
                times.update(keys.tolist())
        times = np.array(sorted(times))
        src_rot = {n: sample(times, k, v, "rotation") for (n, p), (k, v) in channels.items() if p == "rotation"}
        src_pel = channels.get((src.index[PELVIS], "translation"))
        pelvis_src = sample(times, *src_pel, "translation") if src_pel else None
        out_rot = {name: [] for name in tgt.joints}
        out_pelvis = []
        for f in range(len(times)):
            rots = {n: quat_to_matrix(q[f]) for n, q in src_rot.items()}
            trans = {src.index[PELVIS]: pelvis_src[f]} if pelvis_src is not None else {}
            g_src = src.globals(rots, trans)
            g_tgt = {}
            local_tgt = {}
            for i in tgt.order():
                name = tgt.nodes[i].get("name")
                parent = g_tgt.get(tgt.parent[i], np.eye(4)) if i in tgt.parent else np.eye(4)
                t, r, s = tgt.rest(i)
                if name in tgt.joints and name in src.index:
                    j = src.index[name]
                    rs = orthonormal(g_src[j][:3, :3])
                    rs_rest = orthonormal(src_rest[j][:3, :3])
                    rt_rest = orthonormal(tgt_rest[i][:3, :3])
                    world_rot = rs @ rs_rest.T @ rt_rest
                    r = orthonormal(parent[:3, :3]).T @ world_rot
                    local_tgt[i] = r
                if name == PELVIS and pelvis_src is not None:
                    src_parent = src_rest[src.parent[src.index[PELVIS]]]
                    delta_world = src_parent[:3, :3] @ (pelvis_src[f] - src.rest(src.index[PELVIS])[0])
                    tgt_parent = tgt_rest[tgt.parent[i]]
                    t = t + np.linalg.solve(tgt_parent[:3, :3], delta_world * pelvis_ratio)
                    out_pelvis.append(t)
                m = np.eye(4)
                m[:3, :3] = r @ s
                m[:3, 3] = t
                g_tgt[i] = parent @ m
            for i, r in local_tgt.items():
                out_rot[tgt.nodes[i]["name"]].append(matrix_to_quat(r))
        clips.append((clip_id, times, out_rot, np.array(out_pelvis) if out_pelvis else None))
    return clips, tgt


def append_clips(doc, binary, clips, tgt):
    """Adds clips to a GLB document, appending their accessors to the BIN chunk."""
    doc = json.loads(json.dumps(doc))
    chunks = [binary]
    length = len(binary)

    def accessor(array, bounds=False):
        nonlocal length
        array = np.ascontiguousarray(array, dtype=np.float32)
        if array.ndim == 1:
            array = array.reshape(-1, 1)
        pad = (-length) % 4
        chunks.append(b"\0" * pad)
        length += pad
        doc["bufferViews"].append({"buffer": 0, "byteOffset": length, "byteLength": array.nbytes})
        chunks.append(array.tobytes())
        length += array.nbytes
        acc = {"bufferView": len(doc["bufferViews"]) - 1, "componentType": 5126, "count": int(array.shape[0]),
               "type": glb.TYPE_FOR[array.shape[1]]}
        if bounds:
            acc["min"], acc["max"] = [float(array.min())], [float(array.max())]
        doc["accessors"].append(acc)
        return len(doc["accessors"]) - 1

    # The pack carries only the retargeted clips: a clip id already on the character (its
    # "rest") collides in the Engine's association (EffectiveClipCollision).
    animations = doc["animations"] = []
    for clip_id, times, rots, pelvis in clips:
        times = times - times[0]
        input_acc = accessor(times, bounds=True)
        samplers, channels = [], []
        for name, quats in rots.items():
            if not quats:
                continue
            samplers.append({"input": input_acc, "output": accessor(np.array(quats)), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": tgt.index[name], "path": "rotation"}})
        if pelvis is not None:
            samplers.append({"input": input_acc, "output": accessor(pelvis), "interpolation": "LINEAR"})
            channels.append({"sampler": len(samplers) - 1, "target": {"node": tgt.index[PELVIS], "path": "translation"}})
        animations.append({"name": clip_id, "samplers": samplers, "channels": channels})
    pad = (-length) % 4
    out_bin = b"".join(chunks) + b"\0" * pad
    doc["buffers"] = [{"byteLength": len(out_bin)}]
    return doc, out_bin


def strip_images(doc):
    """The pack only lends the Engine its rig and clips: its materials keep no textures (the images'
    bytes stay unreferenced in the copied BIN chunk only until repack drops them)."""
    doc = json.loads(json.dumps(doc))
    for material in doc.get("materials", []):
        material.pop("normalTexture", None)
        material.pop("occlusionTexture", None)
        material.pop("emissiveTexture", None)
        material.get("pbrMetallicRoughness", {}).pop("baseColorTexture", None)
        material.get("pbrMetallicRoughness", {}).pop("metallicRoughnessTexture", None)
    image_views = {i["bufferView"] for i in doc.get("images", []) if "bufferView" in i}
    for key in ("images", "textures", "samplers"):
        doc.pop(key, None)
    doc["_drop_views"] = sorted(image_views)
    return doc


def repack(doc, binary):
    """Rewrites the BIN chunk without the dropped buffer views (image bytes)."""
    drop = set(doc.pop("_drop_views", []))
    views, chunks, length, remap = [], [], 0, {}
    for i, view in enumerate(doc["bufferViews"]):
        if i in drop:
            continue
        pad = (-length) % 4
        chunks.append(b"\0" * pad)
        length += pad
        data = binary[view.get("byteOffset", 0):view.get("byteOffset", 0) + view["byteLength"]]
        remap[i] = len(views)
        views.append({**view, "byteOffset": length})
        chunks.append(data)
        length += len(data)
    for acc in doc["accessors"]:
        acc["bufferView"] = remap[acc["bufferView"]]
    doc["bufferViews"] = views
    out = b"".join(chunks) + b"\0" * ((-length) % 4)
    doc["buffers"] = [{"byteLength": len(out)}]
    return doc, out


def write(path, doc, binary):
    if "_drop_views" in doc:
        doc, binary = repack(doc, binary)
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * ((-len(text)) % 4)
    with open(path, "wb") as out:
        out.write(struct.pack("<III", 0x46546C67, 2, 12 + 8 + len(text) + 8 + len(binary)))
        out.write(struct.pack("<II", len(text), 0x4E4F534A) + text)
        out.write(struct.pack("<II", len(binary), 0x004E4942) + binary)


def main(ual, character, pack):
    src_doc, src_bin = glb.read(ual)
    tgt_doc, tgt_bin = glb.read(character)
    clips, tgt = retarget(src_doc, src_bin, tgt_doc)
    doc, binary = append_clips(strip_images(tgt_doc), tgt_bin, clips, tgt)
    doc["asset"]["generator"] = f"{doc['asset'].get('generator', '')}; UAL1 clips retargeted by art/characters/quaternius/retarget.py"
    write(pack, doc, binary)
    print("pack", pack, [(c[0], len(c[1])) for c in clips], "pelvis ratio applied")


if __name__ == "__main__":
    main(*sys.argv[1:4])
