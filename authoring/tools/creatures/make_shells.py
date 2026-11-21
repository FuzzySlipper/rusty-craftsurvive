"""Makes a creature's rigid worn pieces (#10956): its shells and what it holds, each a prop mesh in the frame of the joint
the product hangs it from, so it moves with the rig without being skinned.

usage: python3 -I authoring/tools/creatures/make_shells.py <species.json> <built.glb> <out dir> [--content-root content/game]

The species' `shells` block names each piece: the joint it hangs from and its shape, in the species' own
[side, forward, up] metres. A shell is a cap over the body: an ellipse in plan (`centre` forward, `half` [side,
forward]) whose rim rises and falls round it (`rim` heights at its sides, front and back) up to a crown (`top`).
`fullness` (2, a paraboloid, by default) flattens its top and flares its rim.
Its paint is drawn here at the game's 64 texels/m: ivory with growth lines, a jade band at the rim, and amber
insets where `insets` asks. Where the body would come through its underside (the spine's spikes, the hood) the shell
swells over it by `clearance`, eased over its neighbours, and the run says by how much. A shell with `mesh` is a generated one instead (its prepared folder beside the species file, painted from its sheet by
paint_from_views.py), stretched to its `box` and raised until the body clears its top. A `held` piece is a short curved blade, or with `"shape": "crescent"` a ring open on one side whose core is a separate
`glow` part (the implement-carrier's, #10957); `light` gives it a light that rides with it. A `held` blade is a short curved blade in a
hand's grip. Each piece is written as <name>.prop-mesh.json with <name>.png beside it, and `pieces.json` lists them
with their joints, for the product.
"""
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

TEXELS_PER_METRE = 64
# The joints whose skin a shell must clear: the trunk, neck and head, not the limbs.
TRUNK = {"root", "hips", "spine", "chest", "neck", "head"}
# The paint, in the body's own reduced palette where it has one.
IVORY = np.array((200, 180, 156))
IVORY_LINE = np.array((176, 150, 126))
IVORY_DARK = np.array((176, 143, 120))
IVORY_EDGE = np.array((214, 198, 178))
JADE = np.array((138, 164, 126))
JADE_DARK = np.array((98, 126, 94))
PLUM_DARK = np.array((95, 38, 53))
PLUM_DEEP = np.array((66, 26, 37))
AMBER = np.array((177, 86, 24))
AMBER_LIGHT = np.array((226, 146, 48))
BLADE = np.array((198, 194, 182))
BLADE_EDGE = np.array((232, 230, 222))
BLADE_GRIP = np.array((112, 65, 57))
# Where the jade band runs, as a share of the way from crown to rim.
BAND = (0.9, 0.975)
# The rings of a shell's surface as shares from crown to rim (closer towards the rim, where it curves most), and its
# segments round the plan.
RING_SHARES = np.array((0.0, 0.18, 0.34, 0.48, 0.6, 0.7, 0.78, 0.85, 0.9, 0.94, 0.97, 0.99, 1.0))
SEGMENTS = 48
# The painted rows of a shell's texture: the outside above, the underside and rim below.
OUTSIDE_SHARE = 0.8
SEED = 10956


def read_glb(path):
    data = Path(path).read_bytes()
    length = struct.unpack("<I", data[12:16])[0]
    doc = json.loads(data[20:20 + length])
    binary = data[20 + length + 8:]

    def accessor(index):
        acc = doc["accessors"][index]
        view = doc["bufferViews"][acc["bufferView"]]
        count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4, "MAT4": 16}[acc["type"]]
        dtype = {5126: np.float32, 5123: np.uint16, 5125: np.uint32, 5121: np.uint8}[acc["componentType"]]
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        return np.frombuffer(binary, dtype=dtype, count=acc["count"] * count, offset=start).reshape(acc["count"], count)

    skin = doc["skins"][0]
    names = [doc["nodes"][i]["name"] for i in skin["joints"]]
    binds = accessor(skin["inverseBindMatrices"])
    inverse = {name: binds[i].reshape(4, 4).T for i, name in enumerate(names)}
    attributes = doc["meshes"][0]["primitives"][0]["attributes"]
    positions = accessor(attributes["POSITION"])
    joints, weights = accessor(attributes["JOINTS_0"]), accessor(attributes["WEIGHTS_0"])
    # Each vertex's strongest joint: a shell swells over the trunk, not the limbs that move under its rim.
    strongest = [names[joints[i, int(np.argmax(weights[i]))]] for i in range(len(positions))]
    trunk = np.array([name in TRUNK for name in strongest])
    return inverse, positions[trunk]


def to_gltf(side, forward, up):
    """Species metres to glTF model space: x left, y up, z forward."""
    return np.stack([side, up, forward], axis=-1)


def rim_height(rim, theta):
    """The rim's height round the plan: `side` at the flanks, easing to `front` ahead and `back` behind."""
    ahead, behind = np.maximum(0.0, np.sin(theta)) ** 2, np.maximum(0.0, -np.sin(theta)) ** 2
    return rim["side"] + (rim["front"] - rim["side"]) * ahead + (rim["back"] - rim["side"]) * behind


def surface(spec, top, r, theta):
    """A point of the shell's outside at share r from crown to rim, round the plan by theta (0 at the left flank, pi/2 ahead)."""
    side = spec["half"][0] * r * np.cos(theta)
    forward = spec["centre"] + spec["half"][1] * r * np.sin(theta)
    low = rim_height(spec["rim"], theta)
    up = low + (top - low) * profile(spec, r)
    return side, forward, up


def profile(spec, r):
    """How far up from rim to crown the shell is at share r: flatter on top and flaring at the rim as `fullness` rises
    (2 is a paraboloid); never vertical at the rim, as the sheet's shells flare."""
    return 1.0 - np.minimum(r, 1.0) ** spec.get("fullness", 2.0)


def body_envelope(spec, body, side, forward):
    """The highest the body reaches near each plan point (within `reach` metres across), or -inf where it is not there."""
    reach = spec.get("reach", 0.1)
    bx, bup, bf = body[:, 0], body[:, 1], body[:, 2]
    near = ((side[..., None] - bx) ** 2 + (forward[..., None] - bf) ** 2) < reach ** 2
    return np.where(near, bup, -np.inf).max(axis=-1)


def shell_heights(spec, body, name):
    """The outside's height at each ring and segment: the smooth cap from rim to crown, lifted wherever the body
    would come through its underside (the spine's spikes, the hood), and eased so a lift is a swell, not a spike."""
    r, theta = np.meshgrid(RING_SHARES, np.linspace(0.0, 2 * math.pi, SEGMENTS + 1), indexing="ij")
    side, forward, up = surface(spec, spec["top"], r, theta)
    floor = body_envelope(spec, body, side, forward) + spec.get("clearance", 0.03) + spec.get("thickness", 0.04)
    low = rim_height(spec["rim"], theta)
    floor = np.where(floor > low, floor, -np.inf)
    lifted = np.maximum(up, floor)
    for _ in range(spec.get("ease", 6)):
        eased = lifted.copy()
        eased[1:-1] = 0.5 * lifted[1:-1] + 0.125 * (lifted[:-2] + lifted[2:] + np.roll(lifted[1:-1], 1, axis=1) + np.roll(lifted[1:-1], -1, axis=1))
        eased[0] = eased[0].mean()
        lifted = np.maximum(np.maximum(eased, floor), up)
    lifted[:, -1] = lifted[:, 0]
    rise = float((lifted - up).max())
    if rise > 0.005:
        print(f"{name}: lifted up to {rise:.2f} m where the body comes near")
    return side, forward, lifted


def paint_shell(spec, width, height, rng):
    """The shell's texture: u round the plan, v from crown (top) to rim, the outside above and its underside below."""
    image = np.zeros((height, width, 3))
    outside = int(height * OUTSIDE_SHARE)
    for row in range(outside):
        r = (row + 0.5) / outside
        for column in range(width):
            colour = IVORY + rng.integers(-5, 6, 3)
            if (r * 9.0) % 1.0 < 0.12 and r < BAND[0]:
                colour = IVORY_LINE
            if rng.random() < 0.012:
                colour = IVORY_DARK
            if BAND[0] <= r < BAND[1]:
                colour = JADE + rng.integers(-4, 5, 3)
                if r < BAND[0] + 0.02 or r >= BAND[1] - 0.02:
                    colour = JADE_DARK
            if r >= BAND[1]:
                colour = IVORY_EDGE
            image[row, column] = colour
    for inset in range(spec.get("insets", 0)):
        u = (inset + 0.5) / spec["insets"]
        theta = 2 * math.pi * u
        # Insets keep to the flanks and back, clear of the face.
        if math.sin(theta) > 0.6:
            continue
        centre_r = 0.62 + 0.1 * ((inset % 2) * 2 - 1)
        cx, cy = u * width, centre_r * outside
        radius = 0.08 * TEXELS_PER_METRE
        for row in range(max(0, int(cy - radius - 2)), min(outside, int(cy + radius + 3))):
            for column in range(int(cx - radius - 2), int(cx + radius + 3)):
                d = math.hypot((column % width) - cx if abs(column - cx) < width / 2 else column - cx, row - cy)
                c = column % width
                if d < radius - 1.5:
                    image[row, c] = AMBER_LIGHT if d < radius * 0.35 else AMBER
                elif d < radius:
                    image[row, c] = PLUM_DEEP
    for row in range(outside, height):
        for column in range(width):
            image[row, column] = PLUM_DARK + rng.integers(-4, 5, 3)
    return np.clip(image, 0, 255).astype(np.uint8)


def shell_mesh(spec, body, name):
    """Outside, underside and rim of a shell as species-metre points with UVs: (points, uvs, triangles)."""
    thickness = spec.get("thickness", 0.04)
    side, forward, heights = shell_heights(spec, body, name)
    points, uvs, triangles = [], [], []
    rings, columns = heights.shape

    def grid(lower, v_of):
        first = len(points)
        for i in range(rings):
            for j in range(columns):
                points.append((side[i, j], forward[i, j], heights[i, j] - (thickness if lower else 0.0)))
                uvs.append((j / (columns - 1), v_of(RING_SHARES[i])))
        for i in range(rings - 1):
            for j in range(columns - 1):
                a = first + i * columns + j
                b, c, d = a + 1, a + columns, a + columns + 1
                triangles.extend([(a, c, b), (b, c, d)] if not lower else [(a, b, c), (b, d, c)])
        return first

    outer = grid(False, lambda r: r * OUTSIDE_SHARE)
    inner = grid(True, lambda r: OUTSIDE_SHARE + 0.5 * (1 - OUTSIDE_SHARE))
    # The rim: a band from the outside's last ring to the underside's, painted as the band's edge.
    for j in range(columns - 1):
        a = outer + (rings - 1) * columns + j
        c = inner + (rings - 1) * columns + j
        triangles.extend([(a, a + 1, c), (a + 1, c + 1, c)])
    return np.array(points), np.array(uvs), triangles, float(heights.max())


def read_prepared(path):
    """A prepared, charted mesh (prepare_mesh.py's body.glb): its points in species metres, UVs and triangles. glTF to
    species swaps two axes, so each triangle is turned over to keep its outside out."""
    data = Path(path).read_bytes()
    length = struct.unpack("<I", data[12:16])[0]
    doc = json.loads(data[20:20 + length])
    binary = data[20 + length + 8:]

    def accessor(index):
        acc = doc["accessors"][index]
        view = doc["bufferViews"][acc["bufferView"]]
        count = {"SCALAR": 1, "VEC2": 2, "VEC3": 3}[acc["type"]]
        dtype = {5126: np.float32, 5123: np.uint16, 5125: np.uint32}[acc["componentType"]]
        start = view.get("byteOffset", 0) + acc.get("byteOffset", 0)
        return np.frombuffer(binary, dtype=dtype, count=acc["count"] * count, offset=start).reshape(acc["count"], count)

    primitive = doc["meshes"][0]["primitives"][0]
    gltf = accessor(primitive["attributes"]["POSITION"]).astype(float)
    uvs = accessor(primitive["attributes"]["TEXCOORD_0"]).astype(float)
    triangles = [(int(a), int(c), int(b)) for a, b, c in accessor(primitive["indices"]).reshape(-1, 3)]
    return np.stack([gltf[:, 0], gltf[:, 2], gltf[:, 1]], axis=1), uvs, triangles


def fitted_mesh(spec, body, name, source):
    """A generated shell (`mesh`: its prepared folder) stretched to its `box` ([side half-width, [back, front], [rim,
    crown]] in species metres) and lifted where the body would come through its top by `clearance`."""
    points, uvs, triangles = read_prepared(source / "body.glb")
    half, (back, front), (rim, crown) = spec["box"]
    low, high = points.min(axis=0), points.max(axis=0)
    target_low, target_high = np.array([-half, back, rim]), np.array([half, front, crown])
    points = target_low + (points - low) / (high - low) * (target_high - target_low)
    # The shell's top over each of the trunk's points: its highest point within a hand's breadth of it in plan.
    side, up, forward = body[:, 0], body[:, 1], body[:, 2]
    under = (np.abs(side) < half) & (forward > back) & (forward < front) & (up > rim)
    lift = 0.0
    reach = spec.get("reach", 0.08)
    for x, f, u in zip(side[under], forward[under], up[under]):
        near = (np.abs(points[:, 0] - x) < reach) & (np.abs(points[:, 1] - f) < reach)
        if near.any():
            lift = max(lift, u + spec.get("clearance", 0.03) - points[near, 2].max())
    if lift > 0.005:
        print(f"{name}: raised {lift:.2f} m so the body clears its top")
        points[:, 2] += lift
    return points, uvs, triangles


def blade_mesh(spec, grip, along, up):
    """A short curved blade: a flat crescent rising from the grip along `along`, its back curving towards `up`, and a
    grip wrapped in plum hide. Points in species metres."""
    length, width, thickness = spec.get("length", 0.48), spec.get("width", 0.07), spec.get("thickness", 0.012)
    side = np.cross(along, up)
    points, uvs, triangles = [], [], []
    steps = 10
    for face, offset in ((0, thickness / 2), (1, -thickness / 2)):
        first = len(points)
        for i in range(steps + 1):
            s = i / steps
            bend = 0.22 * length * s * s
            centre = grip + along * (s * length) + up * bend
            half = width * (1.0 - 0.9 * s ** 1.5) * (0.5 if s < 0.25 else 1.0)
            for edge in (-1.0, 1.0):
                points.append(centre + up * (edge * half) + side * offset)
                uvs.append(((0.05 + 0.9 * s), 0.25 + 0.5 * (edge + 1) / 2 if s >= 0.25 else 0.05 + 0.1 * (edge + 1) / 2))
        for i in range(steps):
            a = first + 2 * i
            quad = [(a, a + 2, a + 1), (a + 1, a + 2, a + 3)]
            triangles.extend(quad if face == 0 else [(x, z, y) for x, y, z in quad])
    return np.array(points), np.array(uvs), triangles


def paint_blade(width, height):
    image = np.zeros((height, width, 3))
    for row in range(height):
        v = (row + 0.5) / height
        for column in range(width):
            u = (column + 0.5) / width
            if v < 0.2 or u < 0.05 + 0.9 * 0.25 and v < 0.25:
                image[row, column] = BLADE_GRIP
            else:
                image[row, column] = BLADE_EDGE if v > 0.68 else BLADE
    return image.astype(np.uint8)


def normals_of(points, triangles):
    normals = np.zeros_like(points)
    for a, b, c in triangles:
        n = np.cross(points[b] - points[a], points[c] - points[a])
        normals[[a, b, c]] += n
    # A point every triangle about it meets edge-on (a shell's crown, where a ring closes to a point) has no normal of
    # its own: it takes that of the points at the same place, as the Engine refuses a mesh with one it cannot normalise.
    for index in np.flatnonzero(np.linalg.norm(normals, axis=1) < 1e-9):
        same = np.linalg.norm(points - points[index], axis=1) < 1e-6
        normals[index] = normals[same].sum(axis=0)
    length = np.linalg.norm(normals, axis=1, keepdims=True)
    if (length < 1e-9).any():
        raise SystemExit("a piece has a point with no normal")
    return normals / length


def to_joint(points, joint, inverse):
    """Species-metre points in a joint's own frame (glTF axes, metres)."""
    model = to_gltf(points[:, 0], points[:, 1], points[:, 2])
    frame = inverse[joint]
    return (frame[:3, :3] @ model.T).T + frame[:3, 3]


def write_piece(out, name, joint, inverse, points, uvs, triangles, image, content_root, extra=()):
    """Writes the piece in its joint's frame (glTF axes, metres), with its texture. `extra` adds parts of other roles
    as (role, points, uvs, triangles, image): a held implement's glowing core is its `glow` part."""
    parts, textures, count = [], {}, 0
    for role, part_points, part_uvs, part_triangles, part_image in [("shell", points, uvs, triangles, image), *extra]:
        # Species metres to glTF swaps two axes, which mirrors the space: each triangle's winding turns over with it.
        part_triangles = [(a, c, b) for a, b, c in part_triangles]
        local = to_joint(part_points, joint, inverse)
        normals = normals_of(local, part_triangles)
        file = f"{name}.png" if role == "shell" else f"{name}-{role}.png"
        # The Engine admits RGBA PNGs only.
        Image.fromarray(part_image).convert("RGBA").save(out / file)
        textures[role] = str((out / file).resolve().relative_to(Path(content_root).resolve()))
        flat = lambda array: [round(float(v), 5) for v in np.asarray(array).ravel()]
        parts.append({"role": role, "positions": flat(local), "normals": flat(normals), "uvs": flat(part_uvs),
                      "colors": [1.0] * (4 * len(local)), "indices": [int(i) for tri in part_triangles for i in tri]})
        count += len(part_triangles)
    span = np.array(parts[0]["positions"]).reshape(-1, 3)
    span = span.max(axis=0) - span.min(axis=0)
    (out / f"{name}.prop-mesh.json").write_text(json.dumps({"name": name, "height": float(span[1]), "parts": parts,
                                                            "textures": textures}))
    print(f"{name}: {count} triangles on {joint}, texture {image.shape[1]}x{image.shape[0]}")
    return {"piece": f"{textures['shell'][:-len('.png')]}.prop-mesh.json", "joint": joint, "triangles": count}


def crescent_mesh(spec, grip, along, up):
    """A held crescent (#10957, the implement-carrier's): a thick ring of pale grown material open on one side, hanging
    below the grip in the plane of `along` (forward) and `up`, and its core, a lens filling the hollow, as its own part
    for the product to make glow. Points in species metres: (ring points, uvs, triangles, core points, uvs, triangles)."""
    radius, tube = spec.get("radius", 0.2), spec.get("tube", 0.035)
    opening = math.radians(spec.get("openingDegrees", 70.0))
    # The gap faces forward and up, the way the sheet holds it; the ring hangs from its top.
    gap_at = math.radians(spec.get("gapDegrees", 35.0))
    centre = grip - up * radius
    side = np.cross(along, up)
    arc, ring = 28, 8
    points, uvs, triangles = [], [], []
    for i in range(arc + 1):
        a = gap_at + opening / 2 + (2 * math.pi - opening) * i / arc
        radial = along * math.cos(a) + up * math.sin(a)
        # The horns thin to points at the gap.
        taper = min(1.0, 0.25 + 1.5 * min(i, arc - i) / arc)
        for j in range(ring):
            b = 2 * math.pi * j / ring
            points.append(centre + radial * (radius + tube * taper * math.cos(b)) + side * (tube * taper * math.sin(b)))
            uvs.append((i / arc, j / ring))
    for i in range(arc):
        for j in range(ring):
            a0, a1 = i * ring + j, i * ring + (j + 1) % ring
            b0, b1 = a0 + ring, a1 + ring
            triangles += [(a0, b0, a1), (a1, b0, b1)]
    core_points, core_uvs, core_triangles = [centre + side * 0.012, centre - side * 0.012], [(0.5, 0.5), (0.5, 0.5)], []
    rim = 16
    core = spec.get("core", 0.62) * radius
    for k in range(rim):
        a = 2 * math.pi * k / rim
        core_points.append(centre + (along * math.cos(a) + up * math.sin(a)) * core)
        core_uvs.append((0.5 + 0.5 * math.cos(a), 0.5 + 0.5 * math.sin(a)))
    for k in range(rim):
        a, b = 2 + k, 2 + (k + 1) % rim
        core_triangles += [(0, a, b), (1, b, a)]
    return (np.array(points), np.array(uvs), triangles, np.array(core_points), np.array(core_uvs), core_triangles)


def paint_crescent(width, height, rng):
    """The ring: ivory with a few growth lines and a jade band along its inner edge."""
    image = np.zeros((height, width, 3))
    for row in range(height):
        for column in range(width):
            colour = IVORY + rng.integers(-5, 6, 3)
            if row in (height // 2, height // 2 - 1):
                colour = JADE
            elif column % 9 == 0:
                colour = IVORY_LINE
            image[row, column] = colour
    return np.clip(image, 0, 255).astype(np.uint8)


def paint_core(size):
    """The core: amber, brightest at its heart, its rim a deep plum."""
    image = np.zeros((size, size, 3))
    for row in range(size):
        for column in range(size):
            d = math.hypot(column + 0.5 - size / 2, row + 0.5 - size / 2) / (size / 2)
            image[row, column] = PLUM_DEEP if d > 0.9 else (AMBER_LIGHT if d < 0.45 else AMBER)
    return image.astype(np.uint8)


def main(argv):
    species = json.loads(Path(argv[0]).read_text())
    inverse, body = read_glb(argv[1])
    out = Path(argv[2])
    content_root = argv[argv.index("--content-root") + 1] if "--content-root" in argv else "content/game"
    out.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(SEED)
    spec = species["shells"]
    pieces = {}
    sources = Path(argv[0]).parent
    for name, shell in spec["pieces"].items():
        if "mesh" in shell:
            source = sources / shell["mesh"]
            points, uvs, triangles = fitted_mesh(shell, body, name, source)
            image = np.asarray(Image.open(source / "body-64.png").convert("RGB"))
            pieces[name] = write_piece(out, name, shell["joint"], inverse, points, uvs, triangles, image, content_root)
            continue
        points, uvs, triangles, top = shell_mesh(shell, body, name)
        a, b = shell["half"]
        perimeter = math.pi * (3 * (a + b) - math.sqrt((3 * a + b) * (a + 3 * b)))
        width = max(32, int(round(perimeter * TEXELS_PER_METRE / 8)) * 8)
        height = max(32, int(round((max(a, b) + top - shell["rim"]["side"]) * TEXELS_PER_METRE / OUTSIDE_SHARE / 8)) * 8)
        image = paint_shell(shell, width, height, rng)
        pieces[name] = write_piece(out, name, shell["joint"], inverse, points, uvs, triangles, image, content_root)
    held = {}
    for name, piece in spec.get("held", {}).items():
        grip = np.array(piece["grip"], dtype=float)
        along = np.array(piece["along"], dtype=float)
        along /= np.linalg.norm(along)
        up = np.array(piece["up"], dtype=float)
        up -= along * np.dot(up, along)
        up /= np.linalg.norm(up)
        if piece.get("shape") == "crescent":
            points, uvs, triangles, core_points, core_uvs, core_triangles = crescent_mesh(piece, grip, along, up)
            held[name] = write_piece(out, name, piece["joint"], inverse, points, uvs, triangles, paint_crescent(48, 8, rng),
                                     content_root, extra=[("glow", core_points, core_uvs, core_triangles, paint_core(16))])
        else:
            points, uvs, triangles = blade_mesh(piece, grip, along, up)
            held[name] = write_piece(out, name, piece["joint"], inverse, points, uvs, triangles, paint_blade(32, 8), content_root)
        if "light" in piece:
            # A light that rides with the piece, at the piece's own point in its joint's frame (the crescent's core).
            light = dict(piece["light"])
            at = grip - up * piece.get("radius", 0.2) if piece.get("shape") == "crescent" else grip
            light["at"] = [round(float(v), 4) for v in to_joint(at[None, :], piece["joint"], inverse)[0]]
            held[name]["light"] = light
    looks = {look: [pieces[name] for name in names] for look, names in spec["looks"].items()}
    (out / "pieces.json").write_text(json.dumps({"looks": looks, "held": held}, indent=2) + "\n")
    print(f"wrote {out / 'pieces.json'}: looks {', '.join(looks)}")


if __name__ == "__main__":
    main(sys.argv[1:])
