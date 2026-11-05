#!/usr/bin/env python3
"""Writes a creature's ragdoll (#10621): the bones and links `Dynamics.CreateRagdoll` is given.

    python3 art/creatures/ragdoll.py [species ...]

Run it after build_creature.py, with no species for all of them. It reads the built GLB and needs
no Blender. Every family names its joints alike, so one table of joint names serves them all, and
the sizes come from the mesh:

- A bone spans its joint to its simulated child. A bone with none spans the skin it carries,
  which takes in the joints it leaves to the clips: a cannon reaches past its hoof to the ground.
- A radius is how far the bone's skin lies from that span, so the body rests on its own surface.
- A body's mass is its capsules' volume at DENSITY. Each bone's share goes by the root of its
  volume: true shares put forty times a cannon's mass on the chest, which stretches the joints.
- A link's axis is the animal's side axis in the child joint's frame, so a limit reads the same
  on every rig. A positive turn swings a hanging bone backward.
"""
import json
import struct
import sys
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parents[2]
CREATURES = ROOT / "content" / "game" / "creatures"

# Joints left to the clips: they follow their parent as they are.
UNSIMULATED = ("root", "jaw", "ear", "hoof", "fhoof", "foot", "hand", "claw")
# Where a bone with several simulated children points.
SPANS_TO = {"hips": "spine", "chest": "neck"}
# The bone that takes the killing blow.
HIT_BONE = "chest"

SIDE_AXIS = np.array([1.0, 0.0, 0.0])
BONE_AXIS = [0.0, 1.0, 0.0]

# swing, twist as a cone; or least and most as a hinge about the side axis.
CONES = {
    "spine": (0.3, 0.15),
    "chest": (0.3, 0.15),
    "neck": (0.7, 0.3),
    "head": (0.6, 0.3),
    "tail": (0.7, 0.3),
    "trunk": (0.7, 0.3),
    "thigh": (1.0, 0.25),
    "upperarm": (0.9, 0.25),
}
HINGES = {
    "shin": (-0.1, 2.2),
    "cannon": (-2.2, 0.1),
    "forearm": (-2.2, 0.1),
    "fcannon": (-0.1, 2.3),
}

SKIN_PERCENTILE = 70
REACH_PERCENTILE = 95
SMALLEST_RADIUS = 0.04
SMALLEST_LENGTH = 0.05
DENSITY = 900.0
# Joint damping, as a share of the child's mass and the square of its length: limbs settle.
DAMPING_SHARE = 2.0


def family_name(joint):
    """A joint's name without its side or its number: `thigh.L` is a thigh, `tail2` a tail."""
    return joint.split(".")[0].rstrip("0123456789")


def read_glb(path):
    data = path.read_bytes()
    length = struct.unpack("<I", data[12:16])[0]
    document = json.loads(data[20 : 20 + length])
    return document, data[20 + length + 8 :]


def accessor(document, binary, index):
    item = document["accessors"][index]
    view = document["bufferViews"][item["bufferView"]]
    width = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[item["type"]]
    kind = {5121: np.uint8, 5123: np.uint16, 5125: np.uint32, 5126: np.float32}[item["componentType"]]
    start = view.get("byteOffset", 0) + item.get("byteOffset", 0)
    stride = view.get("byteStride", width * np.dtype(kind).itemsize)
    rows = np.ndarray((item["count"], width), kind, binary, start, (stride, np.dtype(kind).itemsize))
    return np.array(rows, dtype=np.float64 if kind == np.float32 else np.int64)


def local_matrix(node):
    x, y, z, w = node.get("rotation", [0, 0, 0, 1])
    rotation = np.array(
        [
            [1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
            [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
            [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)],
        ]
    )
    matrix = np.eye(4)
    matrix[:3, :3] = rotation * np.array(node.get("scale", [1, 1, 1]))
    matrix[:3, 3] = node.get("translation", [0, 0, 0])
    return matrix


class Rig:
    """A GLB's joints at rest, and which of them carries each vertex."""

    def __init__(self, path):
        document, binary = read_glb(path)
        nodes = document["nodes"]
        skin = document["skins"][0]
        parents = {child: index for index, node in enumerate(nodes) for child in node.get("children", [])}

        def world(index):
            matrix = local_matrix(nodes[index])
            while index in parents:
                index = parents[index]
                matrix = local_matrix(nodes[index]) @ matrix
            return matrix

        self.names = [nodes[index]["name"] for index in skin["joints"]]
        by_node = dict(zip(skin["joints"], self.names))
        self.parent = {by_node[i]: by_node.get(parents.get(i)) for i in skin["joints"]}
        self.world = {by_node[i]: world(i) for i in skin["joints"]}

        positions, carriers = [], []
        for primitive in (p for mesh in document["meshes"] for p in mesh["primitives"]):
            attributes = primitive["attributes"]
            positions.append(accessor(document, binary, attributes["POSITION"]))
            joints = accessor(document, binary, attributes["JOINTS_0"])
            weights = accessor(document, binary, attributes["WEIGHTS_0"])
            carriers.append(joints[np.arange(len(joints)), weights.argmax(axis=1)])
        self.positions = np.concatenate(positions)
        self.carrier = np.concatenate(carriers)

    def children(self, joint):
        return [name for name in self.names if self.parent[name] == joint]

    def position(self, joint):
        return self.world[joint][:3, 3]

    def rotation(self, joint):
        return self.world[joint][:3, :3]

    def skin(self, joints):
        """The vertices these joints carry."""
        indices = [self.names.index(joint) for joint in joints]
        return self.positions[np.isin(self.carrier, indices)]


def simulated(joint):
    return family_name(joint) not in UNSIMULATED


def carried(rig, joint):
    """A joint and everything below it that the clips keep: what its body stands for."""
    found = [joint]
    for child in rig.children(joint):
        if not simulated(child):
            found += carried(rig, child)
    return found


def span_end(rig, joint):
    """The simulated child a bone spans to, or None for a bone that ends in skin."""
    children = [child for child in rig.children(joint) if simulated(child)]
    if not children:
        return None
    return SPANS_TO.get(joint, children[0])


def distance_to_span(points, start, end):
    along = end - start
    share = np.clip((points - start) @ along / (along @ along), 0, 1)
    return np.linalg.norm(points - (start + share[:, None] * along), axis=1)


def measure(rig, joint):
    """A bone's end joint, the length it reaches when it has none, and its radius."""
    start = rig.position(joint)
    end_joint = span_end(rig, joint)
    points = rig.skin(carried(rig, joint))
    if end_joint is not None:
        end = rig.position(end_joint)
        length = 0.0
    else:
        direction = rig.rotation(joint)[:, 1]
        reach = float(np.percentile((points - start) @ direction, REACH_PERCENTILE)) if len(points) else 0.0
        length = max(reach, SMALLEST_LENGTH)
        end = start + direction * length
    radius = SMALLEST_RADIUS
    if len(points):
        radius = max(float(np.percentile(distance_to_span(points, start, end), SKIN_PERCENTILE)), SMALLEST_RADIUS)
    if end_joint is None:
        length = max(length - radius, SMALLEST_LENGTH)
    return end_joint, length, radius, float(np.linalg.norm(end - start))


def capsule_volume(radius, span):
    return np.pi * radius * radius * (span + 4 * radius / 3)


def build(species):
    rig = Rig(CREATURES / species / f"{species}.glb")
    joints = [joint for joint in rig.names if simulated(joint)]
    measured = {joint: measure(rig, joint) for joint in joints}
    volumes = {joint: capsule_volume(measured[joint][2], measured[joint][3]) for joint in joints}
    total = DENSITY * sum(volumes.values())
    shares = {joint: float(np.sqrt(volume)) for joint, volume in volumes.items()}
    whole = sum(shares.values())

    bones = []
    for joint in joints:
        end, length, radius, _ = measured[joint]
        bones.append(
            {
                "joint": joint,
                "end": end,
                "length": round(length, 3),
                "radius": round(radius, 3),
                "mass": round(total * shares[joint] / whole, 1),
            }
        )

    links = []
    for index, joint in enumerate(joints):
        parent = rig.parent[joint]
        if parent not in joints:
            continue
        kind = family_name(joint)
        mass, span = bones[index]["mass"], measured[joint][3]
        link = {"parent": joints.index(parent), "child": index, "damping": round(DAMPING_SHARE * mass * span * span, 1)}
        if kind in HINGES:
            axis = rig.rotation(joint).T @ SIDE_AXIS
            least, most = HINGES[kind]
            link |= {"axis": [round(float(value), 4) for value in axis], "least": least, "most": most}
        else:
            swing, twist = CONES[kind]
            link |= {"axis": BONE_AXIS, "swing": swing, "twist": twist}
        links.append(link)

    return {
        "$comment": "Written by art/creatures/ragdoll.py from the built GLB. Lengths and radii are metres, masses kilograms, limits radians, damping N m s per radian.",
        "species": species,
        "hitBone": joints.index(HIT_BONE),
        "bones": bones,
        "links": links,
    }


def main():
    species = sys.argv[1:] or sorted(path.parent.name for path in CREATURES.glob("*/*.glb"))
    for name in species:
        ragdoll = build(name)
        path = CREATURES / name / f"{name}.ragdoll.json"
        path.write_text(json.dumps(ragdoll, indent=2) + "\n")
        mass = sum(bone["mass"] for bone in ragdoll["bones"])
        print(f"{name}: {len(ragdoll['bones'])} bones, {len(ragdoll['links'])} links, {mass:.0f} kg")


if __name__ == "__main__":
    main()
