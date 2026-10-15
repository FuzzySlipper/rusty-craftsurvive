"""Fits a Tripo-rigged humanoid onto the shared UAL rig and splits its face slot (#10014).

usage: python3 -I art/characters/fit_to_ual.py <tripo-rigged.glb> <variant.json> <out-dir>

The Engine binds a clip pack only to a character whose rig matches it exactly, so a character
cannot keep its own skeleton: its mesh is moved onto UAL's rest pose and its weights onto UAL's
joints. Bone lengths therefore come from UAL; a variant changes girth, head and hands only.

1. Turn the Tripo model to face +Z and scale it so its head joint sits at UAL's.
2. Pose Tripo's own rig, by hierarchical joint rotations and its own skin weights, from its
   A-pose to UAL's T-pose directions: arms and legs. A variant may scale the head and the
   hands about their joints here (the stylised proportions).
3. Warp space smoothly (a biharmonic radial basis function with an affine part) so Tripo's posed
   joints, bone midpoints and a few extremity points land on UAL's.
4. Move each weight to the UAL joint named by MAP (twist bones fold into their limb), keep the
   four largest and normalise.
5. Split the face: forward-facing triangles of the head inside the variant's face box become a
   second material slot, with planar front-projected UVs. Writes the face's projection of the
   Tripo texture (face-reference.png) for painting the face set.

Writes payload.npz (slots body and face, as assemble_character.py reads) and fit.json.
"""
import io
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
import glb  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
# UAL1 itself holds the shared rig (read only; content/animations is user-owned).
RIG_SOURCE = ROOT / "content/animations/UAL1_Standard.glb"

# Tripo v1.0 biped joint -> UAL joint. Twist bones fold into the limb they twist.
MAP = {"Root": "root", "Hip": "pelvis", "Pelvis": "pelvis", "Waist": "spine_01", "Spine01": "spine_02", "Spine02": "spine_03",
       "NeckTwist01": "neck_01", "NeckTwist02": "neck_01", "Head": "Head"}
for side, s in (("L", "l"), ("R", "r")):
    MAP.update({
        f"{side}_Clavicle": f"clavicle_{s}", f"{side}_Upperarm": f"upperarm_{s}", f"{side}_UpperarmTwist01": f"upperarm_{s}",
        f"{side}_UpperarmTwist02": f"upperarm_{s}", f"{side}_Forearm": f"lowerarm_{s}", f"{side}_ForearmTwist01": f"lowerarm_{s}",
        f"{side}_ForearmTwist02": f"lowerarm_{s}", f"{side}_Hand": f"hand_{s}", f"{side}_Thigh": f"thigh_{s}",
        f"{side}_ThighTwist01": f"thigh_{s}", f"{side}_ThighTwist02": f"thigh_{s}", f"{side}_Calf": f"calf_{s}",
        f"{side}_CalfTwist01": f"calf_{s}", f"{side}_CalfTwist02": f"calf_{s}", f"{side}_Foot": f"foot_{s}", f"{side}_ToeBase": f"ball_{s}"})

# Step 2 aims: (Tripo joint, Tripo joint it points at, UAL joint, UAL joint it points at), parents first.
AIMS = []
for side, s in (("L", "l"), ("R", "r")):
    AIMS += [(f"{side}_Clavicle", f"{side}_Upperarm", f"clavicle_{s}", f"upperarm_{s}"),
             (f"{side}_Upperarm", f"{side}_Forearm", f"upperarm_{s}", f"lowerarm_{s}"),
             (f"{side}_Forearm", f"{side}_Hand", f"lowerarm_{s}", f"hand_{s}"),
             (f"{side}_Thigh", f"{side}_Calf", f"thigh_{s}", f"calf_{s}"),
             (f"{side}_Calf", f"{side}_Foot", f"calf_{s}", f"foot_{s}")]

# Step 3 correspondences: joints, plus bone midpoints that keep limbs straight.
PAIRS = [("Hip", "pelvis"), ("Spine02", "spine_03"), ("NeckTwist01", "neck_01"), ("Head", "Head")]
MIDS = [(("Hip", "Spine02"), ("pelvis", "spine_03"))]
for side, s in (("L", "l"), ("R", "r")):
    PAIRS += [(f"{side}_Clavicle", f"clavicle_{s}"), (f"{side}_Upperarm", f"upperarm_{s}"), (f"{side}_Forearm", f"lowerarm_{s}"),
              (f"{side}_Hand", f"hand_{s}"), (f"{side}_Thigh", f"thigh_{s}"), (f"{side}_Calf", f"calf_{s}"),
              (f"{side}_ToeBase", f"ball_{s}")]
    MIDS += [((f"{side}_Upperarm", f"{side}_Forearm"), (f"upperarm_{s}", f"lowerarm_{s}")),
             ((f"{side}_Forearm", f"{side}_Hand"), (f"lowerarm_{s}", f"hand_{s}")),
             ((f"{side}_Thigh", f"{side}_Calf"), (f"thigh_{s}", f"calf_{s}")),
             ((f"{side}_Calf", f"{side}_Foot"), (f"calf_{s}", f"foot_{s}"))]


def quat(q):
    x, y, z, w = q
    return np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                     [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                     [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])


def local(node):
    if "matrix" in node:
        return np.array(node["matrix"]).reshape(4, 4).T
    m = np.eye(4)
    m[:3, :3] = quat(node.get("rotation", [0, 0, 0, 1])) * np.array(node.get("scale", [1, 1, 1]))
    m[:3, 3] = node.get("translation", [0, 0, 0])
    return m


def world_matrices(doc):
    parent = {c: i for i, n in enumerate(doc["nodes"]) for c in n.get("children", [])}
    cache = {}

    def get(i):
        if i not in cache:
            cache[i] = (get(parent[i]) if i in parent else np.eye(4)) @ local(doc["nodes"][i])
        return cache[i]

    return [get(i) for i in range(len(doc["nodes"]))], parent


def rotation_between(a, b):
    a, b = a / np.linalg.norm(a), b / np.linalg.norm(b)
    v, c = np.cross(a, b), float(np.dot(a, b))
    if c < -0.999999:
        raise ValueError("opposite directions")
    k = np.array([[0, -v[2], v[1]], [v[2], 0, -v[0]], [-v[1], v[0], 0]])
    return np.eye(3) + k + k @ k / (1 + c)


def about(point, linear):
    m = np.eye(4)
    m[:3, :3] = linear
    m[:3, 3] = point - linear @ point
    return m


def rbf_warp(source, target, points):
    """Biharmonic (phi = r) interpolation with an affine part: maps source onto target exactly."""
    n = len(source)
    k = np.linalg.norm(source[:, None] - source[None], axis=2)
    p = np.hstack([np.ones((n, 1)), source])
    a = np.zeros((n + 4, n + 4))
    a[:n, :n], a[:n, n:], a[n:, :n] = k, p, p.T
    coeff = np.linalg.solve(a, np.vstack([target, np.zeros((4, 3))]))
    kp = np.linalg.norm(points[:, None] - source[None], axis=2)
    return kp @ coeff[:n] + np.hstack([np.ones((len(points), 1)), points]) @ coeff[n:]


def welded_normals(positions, triangles):
    """Area-weighted smooth normals, shared across UV seams (vertices at the same position)."""
    key = np.round(positions / 1e-5).astype(np.int64)
    _, group = np.unique(key, axis=0, return_inverse=True)
    group = group.ravel()
    face = np.cross(positions[triangles[:, 1]] - positions[triangles[:, 0]], positions[triangles[:, 2]] - positions[triangles[:, 0]])
    acc = np.zeros((group.max() + 1, 3))
    for corner in range(3):
        np.add.at(acc, group[triangles[:, corner]], face)
    normals = acc[group]
    length = np.linalg.norm(normals, axis=1, keepdims=True)
    return (normals / np.where(length > 0, length, 1)).astype(np.float32)


def weld(positions):
    key = np.round(positions / 1e-5).astype(np.int64)
    return np.unique(key, axis=0, return_inverse=True)[1].ravel()


def components(triangles, group):
    """Connected component of each triangle over welded vertices."""
    parent = np.arange(group.max() + 1)

    def find(x):
        while parent[x] != x:
            parent[x] = parent[parent[x]]
            x = parent[x]
        return x

    for a, b, c in group[triangles]:
        for u, v in ((a, b), (b, c)):
            ru, rv = find(u), find(v)
            if ru != rv:
                parent[ru] = rv
    return np.array([find(g) for g in group[triangles[:, 0]]])


def boundary_loops(triangles, group):
    """Loops of edges used by one triangle only, as lists of vertex indices (welded walk)."""
    directed = {}
    count = {}
    for tri in triangles:
        for i in range(3):
            a, b = tri[i], tri[(i + 1) % 3]
            key = (min(group[a], group[b]), max(group[a], group[b]))
            count[key] = count.get(key, 0) + 1
            directed[(group[a], group[b])] = (a, b)
    nxt = {}
    for (ga, gb), (a, b) in directed.items():
        if count[(min(ga, gb), max(ga, gb))] == 1:
            nxt[gb] = (ga, a)  # walk the hole against the triangle winding
    loops, seen = [], set()
    for start in list(nxt):
        if start in seen:
            continue
        loop, g = [], start
        while g in nxt and g not in seen:
            seen.add(g)
            g, vertex = nxt[g]
            loop.append(vertex)
        if g == start:
            loops.append(np.array(loop))
    return loops


def inflate(positions, triangles, face, anchored, group, iterations):
    """Pushes face vertices forward (+Z) out of hollows: each takes the larger of its own depth and
    its neighbours' mean. Vertices shared with anchored (visible body) triangles stay put, so the
    seam holds."""
    positions = positions.copy()
    body_groups = set(group[triangles[anchored]].ravel())
    neighbours = {}
    for a, b, c in group[triangles[face]]:
        for u, v in ((a, b), (b, c), (c, a)):
            neighbours.setdefault(u, set()).add(v)
            neighbours.setdefault(v, set()).add(u)
    movable = [g for g in neighbours if g not in body_groups]
    members = {}
    for vertex, g in enumerate(group):
        members.setdefault(g, []).append(vertex)
    depth = {g: positions[members[g][0], 2] for g in neighbours}
    for _ in range(iterations):
        new = {g: max(depth[g], np.mean([depth[n] for n in neighbours[g]])) for g in movable}
        depth.update(new)
    for g in movable:
        positions[members[g], 2] = depth[g]
    return positions


def front_visible(positions, triangles, candidate, rect, size=512, tolerance=0.003):
    """Whether each candidate triangle's centre is the front-most surface seen along -Z."""
    x0, x1, y0, y1 = rect
    p = positions[triangles]
    sx = (p[..., 0] - x0) / (x1 - x0) * size
    sy = (y1 - p[..., 1]) / (y1 - y0) * size
    zbuf = np.full((size, size), -np.inf)
    ys, xs = np.mgrid[0:size, 0:size] + 0.5
    for t in np.nonzero(candidate)[0]:
        a, b, c = np.stack([sx[t], sy[t]], 1)
        m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
        if abs(np.linalg.det(m)) < 1e-9:
            continue
        lo = np.floor(np.minimum(np.minimum(a, b), c)).astype(int).clip(0, size - 1)
        hi = np.ceil(np.maximum(np.maximum(a, b), c)).astype(int).clip(0, size - 1)
        sub = np.stack([xs[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1], ys[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]], -1)
        bary = np.einsum("ij,...j->...i", np.linalg.inv(m), sub - a)
        inside = (bary[..., 0] >= 0) & (bary[..., 1] >= 0) & (bary.sum(-1) <= 1)
        z = p[t, 0, 2] + bary[..., 0] * (p[t, 1, 2] - p[t, 0, 2]) + bary[..., 1] * (p[t, 2, 2] - p[t, 0, 2])
        region = zbuf[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        np.maximum(region, np.where(inside, z, -np.inf), out=region)
    cx = sx.mean(1).astype(int).clip(0, size - 1)
    cy = sy.mean(1).astype(int).clip(0, size - 1)
    return candidate & (p[..., 2].mean(1) >= zbuf[cy, cx] - tolerance)


def rasterise_face(uv_face, depth, uv_tex, triangles, texture, size):
    """Projects the Tripo texture through the face triangles onto a size x size face image."""
    tex = np.asarray(texture.convert("RGB"), dtype=np.float32)
    th, tw = tex.shape[:2]
    out = np.zeros((size, size, 3), np.float32)
    mask = np.zeros((size, size), bool)
    zbuf = np.full((size, size), -np.inf)
    ys, xs = np.mgrid[0:size, 0:size]
    px = np.stack([(xs + 0.5) / size, (ys + 0.5) / size], -1)
    for tri in triangles:
        a, b, c = uv_face[tri]
        m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
        if abs(np.linalg.det(m)) < 1e-12:
            continue
        lo = np.floor(np.minimum(np.minimum(a, b), c) * size).astype(int).clip(0, size - 1)
        hi = np.ceil(np.maximum(np.maximum(a, b), c) * size).astype(int).clip(0, size - 1)
        sub = px[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        bary = np.einsum("ij,...j->...i", np.linalg.inv(m), sub - a)
        inside = (bary[..., 0] >= -1e-4) & (bary[..., 1] >= -1e-4) & (bary.sum(-1) <= 1 + 1e-4)
        t = uv_tex[tri]
        uv = t[0] + bary[..., :1] * (t[1] - t[0]) + bary[..., 1:] * (t[2] - t[0])
        sx = (uv[..., 0] * tw).astype(int).clip(0, tw - 1)
        sy = (uv[..., 1] * th).astype(int).clip(0, th - 1)
        z = depth[tri[0]] + bary[..., 0] * (depth[tri[1]] - depth[tri[0]]) + bary[..., 1] * (depth[tri[2]] - depth[tri[0]])
        zs = zbuf[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        front = inside & (z > zs)
        zs[front] = z[front]
        region = out[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        region[front] = tex[sy, sx][front]
        mask[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1] |= inside
    image = Image.fromarray(out.clip(0, 255).astype(np.uint8))
    return image, mask


def main(source, variant_path, out_dir):
    variant = json.loads(Path(variant_path).read_text())
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    doc, binary = glb.read(source)
    worlds, parent_of = world_matrices(doc)
    nodes = doc["nodes"]
    skin = doc["skins"][0]
    joint_nodes = skin["joints"]
    names = [nodes[i]["name"] for i in joint_nodes]
    ibm = glb.accessor(doc, binary, skin["inverseBindMatrices"]).reshape(-1, 4, 4).transpose(0, 2, 1)
    prim = doc["meshes"][0]["primitives"][0]
    attr = prim["attributes"]
    pos = glb.accessor(doc, binary, attr["POSITION"]).astype(np.float64)
    uv = glb.accessor(doc, binary, attr["TEXCOORD_0"]).astype(np.float64)
    joints = glb.accessor(doc, binary, attr["JOINTS_0"]).astype(int)
    weights = glb.accessor(doc, binary, attr["WEIGHTS_0"]).astype(np.float64)
    weights /= weights.sum(1, keepdims=True)
    tris = glb.accessor(doc, binary, prim["indices"]).reshape(-1, 3).astype(int)
    material = doc["materials"][prim.get("material", 0)]
    texture = Image.open(io.BytesIO(glb.image_bytes(doc, binary, doc["textures"][material["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"])))
    basecolor = np.asarray(texture.convert("RGB"), np.float32)

    # Rest pose in world space, from the skin itself.
    skinning = np.array([worlds[n] @ ibm[j] for j, n in enumerate(joint_nodes)])
    homo = np.hstack([pos, np.ones((len(pos), 1))])
    blend = np.einsum("vk,vkij->vij", weights, skinning[joints])
    rest = np.einsum("vij,vj->vi", blend, homo)[:, :3]
    joint_rest = np.array([worlds[n][:3, 3] for n in joint_nodes])

    ual_doc, _ = glb.read(RIG_SOURCE)
    ual_worlds, _ = world_matrices(ual_doc)
    ual_names = [ual_doc["nodes"][i]["name"] for i in ual_doc["skins"][0]["joints"]]
    ual = {name: ual_worlds[i][:3, 3] for name, i in zip(ual_names, ual_doc["skins"][0]["joints"])}

    # 1. Face +Z: Tripo faces along its toes; scale so the head joint sits at UAL's.
    idx = {name: i for i, name in enumerate(names)}
    toe = joint_rest[idx["L_ToeBase"]] - joint_rest[idx["L_Foot"]] + joint_rest[idx["R_ToeBase"]] - joint_rest[idx["R_Foot"]]
    yaw = np.arctan2(toe[0], toe[2])
    turn = np.array([[np.cos(-yaw), 0, np.sin(-yaw)], [0, 1, 0], [-np.sin(-yaw), 0, np.cos(-yaw)]])
    scale = (ual["Head"][1] - 0) / joint_rest[idx["Head"]][1]
    base = np.eye(4)
    base[:3, :3] = turn * scale
    centre = joint_rest[idx["Hip"]].copy()
    centre[1] = 0
    base[:3, 3] = -base[:3, :3] @ centre
    world = {j: base @ worlds[n] for j, n in enumerate(joint_nodes)}

    # 2. Pose: hierarchical world rotations about each aimed joint, applied to its subtree.
    child_of = {}
    for j, n in enumerate(joint_nodes):
        p = parent_of.get(n)
        if p in joint_nodes:
            child_of.setdefault(joint_nodes.index(p), []).append(j)

    def subtree(j):
        yield j
        for c in child_of.get(j, []):
            yield from subtree(c)

    posed = {j: m.copy() for j, m in world.items()}
    for joint, aim, ual_joint, ual_aim in AIMS:
        j, a = idx[joint], idx[aim]
        current = posed[a][:3, 3] - posed[j][:3, 3]
        wanted = ual[ual_aim] - ual[ual_joint]
        move = about(posed[j][:3, 3], rotation_between(current, wanted))
        for k in subtree(j):
            posed[k] = move @ posed[k]
    for joint, factor in (("Head", variant.get("headScale", 1)), ("L_Hand", variant.get("handScale", 1)), ("R_Hand", variant.get("handScale", 1))):
        j = idx[joint]
        move = about(posed[j][:3, 3], np.eye(3) * factor)
        for k in subtree(j):
            posed[k] = move @ posed[k]
    pose_skin = np.array([posed[j] @ np.linalg.inv(world[j]) for j in range(len(names))])
    rest_h = np.hstack([(base[:3, :3] @ rest.T).T + base[:3, 3], np.ones((len(rest), 1))])
    blend = np.einsum("vk,vkij->vij", weights, pose_skin[joints])
    posed_pos = np.einsum("vij,vj->vi", blend, rest_h)[:, :3]
    posed_joint = {names[j]: posed[j][:3, 3] for j in range(len(names))}

    # 3. Warp posed joints onto UAL's: joints, bone midpoints, and extremities kept at their size.
    src, dst = [], []
    for t, u in PAIRS:
        src.append(posed_joint[t])
        dst.append(ual[u])
    for (t0, t1), (u0, u1) in MIDS:
        src.append((posed_joint[t0] + posed_joint[t1]) / 2)
        dst.append((ual[u0] + ual[u1]) / 2)
    # Tripo's foot joint sits on the sole under the ankle; UAL's is the ankle. Pair the point of
    # Tripo's shin at UAL's ankle fraction with the ankle, and keep the sole on the ground below.
    for side, s in (("L", "l"), ("R", "r")):
        calf, sole = posed_joint[f"{side}_Calf"], posed_joint[f"{side}_Foot"]
        fraction = (ual[f"calf_{s}"][1] - ual[f"foot_{s}"][1]) / ual[f"calf_{s}"][1]
        src.append(calf + fraction * (sole - calf))
        dst.append(ual[f"foot_{s}"])
        src.append(sole)
        dst.append(np.array([ual[f"foot_{s}"][0], sole[1], ual[f"foot_{s}"][2]]))
    head_top = posed_pos[np.argmax(posed_pos[:, 1])]
    src.append(head_top)
    dst.append(ual["Head"] + (head_top - posed_joint["Head"]))
    for side, s in (("L", "l"), ("R", "r")):
        hand = posed_joint[f"{side}_Hand"]
        near = np.linalg.norm(posed_pos - hand, axis=1) < 0.25 * variant.get("handScale", 1)
        tip = posed_pos[near][np.argmax(np.abs(posed_pos[near][:, 0]))]
        src.append(tip)
        dst.append(ual[f"hand_{s}"] + (tip - hand))
    final = rbf_warp(np.array(src), np.array(dst), posed_pos)

    # 4. Weights onto UAL joints.
    ual_index = {name: i for i, name in enumerate(ual_names)}
    remapped = np.zeros((len(final), len(ual_names)))
    for k in range(4):
        target = np.array([ual_index[MAP[names[j]]] for j in joints[:, k]])
        np.add.at(remapped, (np.arange(len(final)), target), weights[:, k])
    top = np.argsort(-remapped, axis=1)[:, :4]
    top_w = np.take_along_axis(remapped, top, 1)
    top_w /= top_w.sum(1, keepdims=True)

    # 5. Face slot. Inner face parts (a stray hair lock, teeth, eyeballs) go, the eye and mouth
    # openings close, and the face sheet is pushed out of its hollows, so painted expressions on
    # a front projection read from the side too. Then the forward-facing head triangles inside
    # the face box become the face slot.
    # Metres about UAL's head joint: x half-width, y low and high. A larger head carries its face
    # box with it, so one face texture set fits every variant.
    box = dict(variant["faceBox"])
    for key in ("halfWidth", "low", "high"):
        box[key] *= variant.get("headScale", 1)
    hj = ual["Head"]
    head_w = remapped[:, ual_index["Head"]]
    # The face box hangs from the nose tip, the head's front-most point: a painted face set stays
    # registered with the features however the body fit moves the head.
    near_nose = (head_w > 0.9) & (np.abs(final[:, 0] - hj[0]) < 0.03) & (final[:, 1] > hj[1] + 0.03) & (final[:, 1] < hj[1] + 0.25)
    nose = final[near_nose][np.argmax(final[near_nose][:, 2])]
    anchor = np.array([nose[0], nose[1], hj[2]])

    def in_box(p):
        return (np.abs(p[..., 0] - anchor[0]) < box["halfWidth"]) & (p[..., 1] > anchor[1] + box["low"]) & (p[..., 1] < anchor[1] + box["high"]) & (p[..., 2] > anchor[2])

    group = weld(final)
    comp = components(tris, group)
    keep = np.ones(len(tris), bool)
    for c, count in zip(*np.unique(comp, return_counts=True)):
        members = tris[comp == c].ravel()
        # Small head parts reaching into the face: hair locks hanging in front, teeth, eyeballs.
        if count < box.get("innerPartTriangles", 300) and head_w[members].min() > 0.5 and in_box(final[members]).any():
            keep &= comp != c
    tris = tris[keep]
    filled = []
    for loop in boundary_loops(tris, group):
        if len(loop) < 3 or not in_box(final[loop]).all():
            continue
        centre = final[loop].mean(0)
        nearest = loop[np.argmin(np.linalg.norm(final[loop] - centre, axis=1))]
        final = np.vstack([final, centre])
        uv = np.vstack([uv, uv[nearest]])
        top = np.vstack([top, top[nearest]])
        top_w = np.vstack([top_w, top_w[nearest]])
        head_w = np.append(head_w, head_w[nearest])
        group = np.append(group, group.max() + 1)
        new = len(final) - 1
        fan = np.array([[loop[i], loop[(i + 1) % len(loop)], new] for i in range(len(loop))])
        a, b, c = final[fan[:, 0]], final[fan[:, 1]], final[fan[:, 2]]
        if np.cross(b - a, c - a).sum(0)[2] < 0:
            fan = fan[:, [1, 0, 2]]
        filled.append(fan)
    if filled:
        tris = np.vstack([tris] + filled)
    normals = welded_normals(final, tris)
    centroid = final[tris].mean(1)
    tri_normal = normals[tris].mean(1)
    candidate = (head_w[tris].min(1) > box.get("minHeadWeight", 0.6)) & in_box(centroid)
    visible = front_visible(final, tris, candidate, (anchor[0] - box["halfWidth"], anchor[0] + box["halfWidth"], anchor[1] + box["low"], anchor[1] + box["high"]))
    # A hair lock joined to the main hair mesh hangs beside the cheek: its hair-coloured triangles
    # keep the body texture, so it reads as hair over the face rather than taking face paint.
    centre_uv = uv[tris].mean(1)
    shade = basecolor[(centre_uv[:, 1] * basecolor.shape[0]).astype(int).clip(0, basecolor.shape[0] - 1),
                      (centre_uv[:, 0] * basecolor.shape[1]).astype(int).clip(0, basecolor.shape[1] - 1)].mean(1)
    # Below the nose it may reach in towards the mouth; beside the eyes only past their outer
    # corners, so the dark lashes and irises stay face slot (else the body texture shows a second
    # pair of eyes).
    side = np.abs(centroid[:, 0] - anchor[0])
    height = centroid[:, 1] - anchor[1]
    lock = (shade < box.get("hairShade", 90)) & (
        ((side > box.get("lockInner", 0.035)) & (height < box.get("lockLow", -0.01)))
        | ((side > box.get("lockOuter", 0.07)) & (height < box.get("lockTop", 0.06))))
    face = candidate & visible & (tri_normal[:, 2] > box["minForward"]) & ~lock
    # Around the nostrils and between the lips every head triangle takes face paint, hidden or not:
    # left on the body texture, the generated lip line would double the painted mouth.
    face |= candidate & (side < box.get("mouthHalfWidth", 0.05)) & (height > box.get("mouthLow", -0.055)) & (height < box.get("mouthHigh", 0.01))
    face[len(tris) - sum(len(f) for f in filled):] = True
    # Hidden inner surfaces (the mouth's walls) may stretch as the sheet moves; visible body holds it.
    anchored = ~face & ~(candidate & ~visible)
    final = inflate(final, tris, face, anchored, group, box.get("inflateIterations", 6))
    normals = welded_normals(final, tris)
    payload = {}
    face_uv_rect = (anchor[0] - box["halfWidth"], anchor[0] + box["halfWidth"], anchor[1] + box["low"], anchor[1] + box["high"])

    def part(name, selection, uv_of):
        used, inverse = np.unique(tris[selection].ravel(), return_inverse=True)
        payload[f"{name}_positions"] = final[used].astype(np.float32)
        payload[f"{name}_normals"] = normals[used]
        payload[f"{name}_uvs"] = uv_of(used).astype(np.float32)
        payload[f"{name}_joints"] = top[used].astype(np.uint16)
        payload[f"{name}_weights"] = top_w[used].astype(np.float32)
        payload[f"{name}_indices"] = inverse.astype(np.uint32)
        return used, inverse.reshape(-1, 3)

    def face_uv(used):
        x0, x1, y0, y1 = face_uv_rect
        p = final[used]
        return np.stack([(p[:, 0] - x0) / (x1 - x0), 1 - (p[:, 1] - y0) / (y1 - y0)], 1)

    part("body", ~face, lambda used: uv[used])
    used, local_tris = part("face", face, face_uv)
    np.savez(out / "payload.npz", **payload)

    texture.save(out / "tripo-basecolor.png")
    reference, mask = rasterise_face(face_uv(used), final[used, 2], uv[used], local_tris, texture, 256)
    reference.save(out / "face-reference.png")
    Image.fromarray((mask * 255).astype(np.uint8)).save(out / "face-mask.png")

    # Texel density of the body at a given atlas size: texels per metre, area-weighted.
    body_tris = tris[~face]
    world_area = np.linalg.norm(np.cross(final[body_tris[:, 1]] - final[body_tris[:, 0]], final[body_tris[:, 2]] - final[body_tris[:, 0]]), axis=1) / 2
    u = uv[body_tris]
    d1, d2 = u[:, 1] - u[:, 0], u[:, 2] - u[:, 0]
    uv_area = np.abs(d1[:, 0] * d2[:, 1] - d1[:, 1] * d2[:, 0]) / 2
    texels_per_metre_per_px = np.sqrt(uv_area.sum() / world_area.sum())
    fit = {
        "source": str(source), "variant": variant, "turnDegrees": float(np.degrees(-yaw)), "scale": float(scale),
        "triangles": {"body": int((~face).sum()), "face": int(face.sum())},
        "faceRect": [float(v) for v in face_uv_rect],
        "noseTip": [round(float(v), 4) for v in nose],
        "bodyTexelsPerMetreAt": {str(px): round(float(texels_per_metre_per_px * px), 1) for px in (64, 128, 256, 1024)},
        "bodyArea": round(float(world_area.sum()), 3),
        "faceTexelsPerMetreAt": {str(px): round(px / (2 * box["halfWidth"]), 1) for px in (32, 64, 128)},
        "height": round(float(final[:, 1].max() - final[:, 1].min()), 3),
        "warpResidual": round(float(np.abs(rbf_warp(np.array(src), np.array(dst), np.array(src)) - np.array(dst)).max()), 6),
    }
    (out / "fit.json").write_text(json.dumps(fit, indent=2) + "\n")
    print(json.dumps(fit, indent=2))


if __name__ == "__main__":
    main(*sys.argv[1:4])
