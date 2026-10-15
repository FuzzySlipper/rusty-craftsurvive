"""Fits an MPFB2 body onto a target skeleton: UAL1's own rig or a frame derived from it (#10028).

The same idea as ../fit_to_ual.py, made simpler by MakeHuman's fixed topology:

1. MPFB's game_engine rig (the same joint names as UAL, after Unreal's mannequin) gives joint
   positions from the morphed body's joint cubes and MakeHuman's own skin weights per vertex.
2. Scale the body whole so its hips stand at the target's: its girth keeps its proportion.
   Pose that rig, with its own weights, onto the target skeleton's T-pose directions: spine,
   arms, legs and feet. The storybook proportions scale the head, hands and feet about their
   joints here.
3. Warp space (biharmonic RBF) so the posed joints, bone midpoints and extremities land on the
   target's joints.
4. Map MPFB's weights onto the target's joints by name. Fingers fold into the hand: the
   storybook hands are mittens, and big hands would not fit UAL's finger joints.
"""
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import glb  # noqa: E402
from fit_to_ual import about, rbf_warp, rotation_between, world_matrices  # noqa: E402

FINGERS = ("thumb", "index", "middle", "ring", "pinky")


def ual_name(bone):
    """MPFB game_engine bone -> UAL joint."""
    if bone == "Root":
        return "root"
    if bone == "head":
        return "Head"
    if bone.split("_")[0] in FINGERS:
        return "hand_" + bone[-1]
    return bone


# (MPFB bone, the MPFB bone it points at, target joint, the target joint it points at), parents first.
AIMS = [("pelvis", "spine_01", "pelvis", "spine_01"), ("spine_01", "spine_02", "spine_01", "spine_02"),
        ("spine_02", "spine_03", "spine_02", "spine_03"), ("spine_03", "neck_01", "spine_03", "neck_01"),
        ("neck_01", "head", "neck_01", "Head")]
for s in ("l", "r"):
    AIMS += [(f"clavicle_{s}", f"upperarm_{s}", f"clavicle_{s}", f"upperarm_{s}"),
             (f"upperarm_{s}", f"lowerarm_{s}", f"upperarm_{s}", f"lowerarm_{s}"),
             (f"lowerarm_{s}", f"hand_{s}", f"lowerarm_{s}", f"hand_{s}"),
             (f"thigh_{s}", f"calf_{s}", f"thigh_{s}", f"calf_{s}"),
             (f"calf_{s}", f"foot_{s}", f"calf_{s}", f"foot_{s}"),
             (f"foot_{s}", f"ball_{s}", f"foot_{s}", f"ball_{s}")]
PAIRS = ["pelvis", "spine_01", "spine_02", "spine_03", "neck_01", "head"]
LIMBS = []
for s in ("l", "r"):
    PAIRS += [f"{b}_{s}" for b in ("clavicle", "upperarm", "lowerarm", "hand", "thigh", "calf", "foot", "ball")]
    LIMBS += [(f"upperarm_{s}", f"lowerarm_{s}"), (f"lowerarm_{s}", f"hand_{s}"), (f"thigh_{s}", f"calf_{s}"), (f"calf_{s}", f"foot_{s}")]


def target_joints(rig_glb):
    """Joint name -> world rest position, and the skin's joint order, of a rig GLB."""
    doc, _ = glb.read(rig_glb)
    worlds, _ = world_matrices(doc)
    order = [doc["nodes"][i]["name"] for i in doc["skins"][0]["joints"]]
    return {name: worlds[i][:3, 3] for name, i in zip(order, doc["skins"][0]["joints"])}, order


# Bone -> the joint its axis runs to, on UAL's names, for girth scaling.
AXES = {"pelvis": "spine_01", "spine_01": "spine_02", "spine_02": "spine_03", "spine_03": "neck_01", "neck_01": "Head"}
for _s in ("l", "r"):
    AXES.update({f"clavicle_{_s}": f"upperarm_{_s}", f"upperarm_{_s}": f"lowerarm_{_s}", f"lowerarm_{_s}": f"hand_{_s}",
                 f"thigh_{_s}": f"calf_{_s}", f"calf_{_s}": f"foot_{_s}"})
GIRTH_GROUP = {"pelvis": "torso", "spine_01": "torso", "spine_02": "torso", "spine_03": "torso", "neck_01": "neck"}
for _s in ("l", "r"):
    GIRTH_GROUP.update({f"clavicle_{_s}": "torso", f"upperarm_{_s}": "arms", f"lowerarm_{_s}": "arms", f"thigh_{_s}": "legs", f"calf_{_s}": "legs"})


def girth(points, top, top_w, target, order, factors):
    """Scales flesh about the bone axes it hangs from: a rounder or thinner body on the same bones.
    factors: {"torso": 1.3, "arms": 1.2, "legs": 1.2}; the neck takes the torso's halfway."""
    if not factors:
        return points
    f = dict(factors)
    f.setdefault("neck", 1 + (f.get("torso", 1) - 1) * 0.5)
    out = np.zeros_like(points)
    for k in range(top.shape[1]):
        names = [order[j] for j in top[:, k]]
        a = np.array([target[n] for n in names])
        b = np.array([target[AXES.get(n, n)] for n in names])
        fac = np.array([f.get(GIRTH_GROUP.get(n, ""), 1.0) for n in names])
        d = b - a
        t = np.clip(np.einsum("ij,ij->i", points - a, d) / np.maximum(np.einsum("ij,ij->i", d, d), 1e-9), 0, 1)
        near = a + d * t[:, None]
        moved = np.where((np.einsum("ij,ij->i", d, d) > 1e-9)[:, None], near + (points - near) * fac[:, None], points)
        out += moved * top_w[:, k:k + 1]
    return out


def fit(base, positions, rig_glb, head_scale=1.0, hand_scale=1.0, foot_scale=1.0, girths=None):
    """Returns final positions (every base vertex), top-4 joint indices and weights into the
    target skin's joint order, the target joints, and fit facts."""
    # Keep the body's proportions: scale it whole so its hips stand at the target's, before the
    # warp moves joints. Otherwise a short, round body is stretched to the frame's height and its
    # girth, kept in metres, reads thinner.
    target, order = target_joints(rig_glb)
    hips = base.joints(positions)["thigh_l"][0][1]
    prescale = float(target["thigh_l"][1] / hips)
    positions = positions * prescale
    joints = base.joints(positions)
    bones = list(base.rig)
    index = {b: i for i, b in enumerate(bones)}

    # Per-vertex MPFB weights, dense over bones.
    dense = np.zeros((len(positions), len(bones)))
    for bone, (ids, w) in base.weights.items():
        dense[ids, index[bone]] += w
    total = dense.sum(1, keepdims=True)
    dense = np.where(total > 0, dense / np.where(total > 0, total, 1), 0)
    dense[total[:, 0] == 0, index["Root"]] = 1

    children = {}
    for b in bones:
        if base.parent(b):
            children.setdefault(base.parent(b), []).append(b)

    def subtree(b):
        yield b
        for c in children.get(b, []):
            yield from subtree(c)

    move = {b: np.eye(4) for b in bones}
    head_of = {b: joints[b][0] for b in bones}

    def posed(b, point):
        return (move[b] @ np.append(point, 1))[:3]

    for bone, aim, tj, taim in AIMS:
        current = posed(aim, head_of[aim]) - posed(bone, head_of[bone])
        wanted = target[taim] - target[tj]
        turn = about(posed(bone, head_of[bone]), rotation_between(current, wanted))
        for b in subtree(bone):
            move[b] = turn @ move[b]
    for bone, factor in [("head", head_scale)] + [(f"{p}_{s}", f) for s in "lr" for p, f in (("hand", hand_scale), ("foot", foot_scale))]:
        grow = about(posed(bone, head_of[bone]), np.eye(3) * factor)
        for b in subtree(bone):
            move[b] = grow @ move[b]

    stack = np.array([move[b] for b in bones])
    homo = np.hstack([positions, np.ones((len(positions), 1))])
    blend = np.einsum("vb,bij->vij", dense, stack)
    posed_pos = np.einsum("vij,vj->vi", blend, homo)[:, :3]
    pj = {b: posed(b, head_of[b]) for b in bones}
    tail = {b: posed(b, joints[b][1]) for b in bones}

    src, dst = [], []
    for b in PAIRS:
        src.append(pj[b])
        dst.append(target[ual_name(b)])
    for a, b in LIMBS:
        src.append((pj[a] + pj[b]) / 2)
        dst.append((target[ual_name(a)] + target[ual_name(b)]) / 2)
    # Extremities keep their own (scaled) size beyond the last joint.
    for b in ["head"] + [f"{p}_{s}" for s in "lr" for p in ("hand", "ball")]:
        src.append(tail[b])
        dst.append(target[ual_name(b)] + (tail[b] - pj[b]))
    final = rbf_warp(np.array(src), np.array(dst), posed_pos)

    target_index = {name: i for i, name in enumerate(order)}
    remapped = np.zeros((len(positions), len(order)))
    for b in bones:
        remapped[:, target_index[ual_name(b)]] += dense[:, index[b]]
    top = np.argsort(-remapped, axis=1)[:, :4]
    top_w = np.take_along_axis(remapped, top, 1)
    top_w /= top_w.sum(1, keepdims=True)
    final = girth(final, top, top_w, target, order, girths)

    facts = {
        "prescale": round(prescale, 3),
        "mpfbHeight": round(float(positions[:13380, 1].max() - positions[:13380, 1].min()) / prescale, 3),
        "fittedHeight": round(float(final[:13380, 1].max() - final[:13380, 1].min()), 3),
        "segments": {name: [round(float(np.linalg.norm(joints[a][0] - joints[b][0])), 3),
                            round(float(np.linalg.norm(target[ual_name(a)] - target[ual_name(b)])), 3)]
                     for name, (a, b) in {"thigh": ("thigh_l", "calf_l"), "shin": ("calf_l", "foot_l"),
                                          "torso": ("pelvis", "neck_01"), "neck": ("neck_01", "head"),
                                          "upperarm": ("upperarm_l", "lowerarm_l"), "forearm": ("lowerarm_l", "hand_l")}.items()},
        "headToNeck": round(float(np.linalg.norm(tail["head"] - pj["head"])), 3),
    }
    return final, top, top_w, dense, index, target, order, facts
