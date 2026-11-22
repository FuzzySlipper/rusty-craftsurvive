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
hand's grip. A piece with `"shape": "pods"` or `"husks"` is the brood bearer's six pods (#10961), sealed or split, their
glow its `glow` part. A `held` piece with `"shape": "silhouette"` is a flat blade cut from its sheet's side view, its jade
slots its `glow` part (the chime grazer's crest, #10962). Each piece is written as <name>.prop-mesh.json with <name>.png beside it, and `pieces.json` lists them
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


def silhouette_mesh(spec, sources):
    """A flat grown blade cut from its sheet's side view (#10962, the chime grazer's crest): the view's silhouette as a
    grid of `cell` metres, `length` metres long along the view's width, swelling to `thickness` within `round` of its
    edge and closing to a knife edge at it (each face with its own edge points), both faces painted from the view at the game's texels. The view's left is
    forward and its top up; its foot (the middle of its lowest rows) stands at `base`. Its jade slots, where the view is
    jade (`glowHue`), are a `glow` part just proud of each face. Returns (points, uvs, triangles, image, glow) in species
    metres, glow being (points, uvs, triangles, image)."""
    from scipy import ndimage
    view = np.asarray(Image.open(sources / spec["image"]).convert("RGB")).astype(float)
    height, width = view.shape[:2]
    ground = np.median(np.concatenate([view[:6, :6].reshape(-1, 3), view[:6, -6:].reshape(-1, 3)]), axis=0)
    inside = ndimage.binary_fill_holes(ndimage.binary_opening(np.abs(view - ground).sum(axis=2) > 30, iterations=2))
    rows, columns = np.nonzero(inside)
    metres = spec["length"] / (columns.max() - columns.min() + 1)
    foot_rows = rows >= rows.max() - max(2, int(0.03 * (rows.max() - rows.min())))
    foot = np.array([columns[foot_rows].mean(), rows.max()])
    # Distance to the edge, in metres, for the swell.
    edge = ndimage.distance_transform_edt(inside) * metres
    step = max(1, int(round(spec.get("cell", 0.02) / metres)))
    grid_y, grid_x = np.arange(0, height, step), np.arange(0, width, step)
    used = np.zeros((len(grid_y), len(grid_x)), bool)
    for j in range(len(grid_y) - 1):
        for i in range(len(grid_x) - 1):
            used[j, i] = inside[grid_y[j]:grid_y[j + 1] + 1, grid_x[i]:grid_x[i + 1] + 1].mean() > 0.5
    # Nodes of the used cells; a node on the outline of the used cells closes the two faces. A cell with no corner inside
    # the outline (a strip one cell wide, at the tip) would fold flat when the outline is eased, so it goes.
    while True:
        corner = np.zeros((len(grid_y), len(grid_x)), int)
        cells = np.argwhere(used)
        for j, i in cells:
            corner[j:j + 2, i:i + 2] += 1
        nodes = np.argwhere(corner > 0)
        outline = {(j, i) for j, i in nodes if corner[j, i] < 4 or j == 0 or i == 0}
        thin = [(j, i) for j, i in cells if all(n in outline for n in ((j, i), (j, i + 1), (j + 1, i), (j + 1, i + 1)))]
        if not thin:
            break
        for j, i in thin:
            used[j, i] = False
    # The outline steps along the grid: ease each outline node toward its outline neighbours, so the edge runs smooth.
    position = {(j, i): np.array([float(grid_x[i]), float(grid_y[j])]) for j, i in nodes}
    neighbours = {n: [m for m in ((n[0] + 1, n[1]), (n[0] - 1, n[1]), (n[0], n[1] + 1), (n[0], n[1] - 1)) if m in outline]
                  for n in outline}
    for _ in range(spec.get("smooth", 4)):
        # A convex corner (one cell's) stays put: eased, its cell's triangle would fold flat.
        eased = {n: 0.5 * position[n] + 0.5 * np.mean([position[m] for m in near], axis=0)
                 if len(near) == 2 and corner[n] > 1 else position[n] for n, near in neighbours.items()}
        position.update(eased)
    base = np.array(spec["base"], dtype=float)
    half, swell = spec.get("thickness", 0.12) / 2, spec.get("round", 0.05)
    points, uvs, index = [], [], {}
    for face, sign in ((0, 1.0), (1, -1.0)):
        for j, i in nodes:
            key = (j, i)
            # Each face keeps its own outline points, where the two meet: shared, their normals would cancel.
            x, y = position[key]
            d = 0.0 if key in outline else edge[min(height - 1, int(y)), min(width - 1, int(x))]
            z = sign * half * math.sqrt(min(1.0, d / swell))
            index[(face, key)] = len(points)
            points.append(base + np.array([z, -(x - foot[0]) * metres, (foot[1] - y) * metres]))
            uvs.append((x / width, y / height))
    triangles = []
    for j, i in cells:
        a, b, c, d = (j, i), (j, i + 1), (j + 1, i), (j + 1, i + 1)
        for face in (0, 1):
            p = [index[(face, n)] for n in (a, b, c, d)]
            quad = [(p[0], p[2], p[1]), (p[1], p[2], p[3])]
            triangles += quad if face == 0 else [(x, z, y) for x, y, z in quad]
    points = np.array(points)
    # The paint at the game's texels: the view box-averaged down, no blur beyond that. The backdrop takes the nearest
    # inside colour first, so the edge texels are not greyed by it.
    _, (near_y, near_x) = ndimage.distance_transform_edt(~inside, return_indices=True)
    view = view[near_y, near_x]
    texels = (max(8, int(round(width * metres * TEXELS_PER_METRE))), max(8, int(round(height * metres * TEXELS_PER_METRE))))
    image = np.asarray(Image.fromarray(view.astype(np.uint8)).resize(texels, Image.BOX))
    # The glow: the cells whose paint is jade, a hair proud of each face.
    rgb = image.astype(float) / 255
    peak, low = rgb.max(axis=2), rgb.min(axis=2)
    chroma = peak - low
    hue = np.degrees(np.arctan2(math.sqrt(3) * (rgb[..., 1] - rgb[..., 2]), 2 * rgb[..., 0] - rgb[..., 1] - rgb[..., 2])) % 360
    hue_low, hue_high = spec.get("glowHue", [70, 170])
    jade = (hue >= hue_low) & (hue <= hue_high) & (chroma > spec.get("glowChroma", 0.08))
    glow_image = np.where(jade[..., None], np.clip(image.astype(float) * spec.get("glowGain", 1.0), 0, 255), 0).astype(np.uint8)
    glow_points, glow_uvs, glow_triangles = [], [], []
    lift = spec.get("glowLift", 0.004)
    for j, i in cells:
        x, y = (grid_x[i] + grid_x[i + 1]) / 2, (grid_y[j] + grid_y[j + 1]) / 2
        if not jade[min(texels[1] - 1, int(y / height * texels[1])), min(texels[0] - 1, int(x / width * texels[0]))]:
            continue
        for face, sign in ((0, 1.0), (1, -1.0)):
            corners = [index[(face, n)] for n in ((j, i), (j, i + 1), (j + 1, i), (j + 1, i + 1))]
            first = len(glow_points)
            for c in corners:
                glow_points.append(points[c] + np.array([sign * lift, 0, 0]))
                glow_uvs.append(uvs[c])
            quad = [(first, first + 2, first + 1), (first + 1, first + 2, first + 3)]
            glow_triangles += quad if face == 0 else [(x, z, y) for x, y, z in quad]
    print(f"{spec['image']}: {len(cells)} cells of {step * metres:.3f} m, {len(outline)} on its edge, {len(glow_triangles) // 2} glow quads")
    return points, np.array(uvs), triangles, image, (np.array(glow_points), np.array(glow_uvs), glow_triangles, glow_image)


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


def pod_ellipsoid(centre, radius, height, rings=5, segments=10):
    """One closed pod, a squat ellipsoid standing in its cup: (points, uvs, triangles) in species metres."""
    points, uvs, triangles = [], [], []
    for i in range(rings + 1):
        polar = math.pi * i / rings
        for j in range(segments):
            a = 2 * math.pi * j / segments
            points.append(centre + np.array((radius * math.sin(polar) * math.cos(a), radius * math.sin(polar) * math.sin(a),
                                             height * math.cos(polar))))
            uvs.append((j / segments, i / rings))
    for i in range(rings):
        for j in range(segments):
            a0, a1 = i * segments + j, i * segments + (j + 1) % segments
            b0, b1 = a0 + segments, a1 + segments
            triangles += [(a0, a1, b0), (a1, b1, b0)]
    return points, uvs, triangles


def husk_petals(centre, radius, height, petals=4, steps=4, width=4):
    """A split pod's husk (#10961): its petals peeled back from the cup's rim, each rising a little, then curling out and
    over until its tip hangs outward, open to the glow inside. Each petal is a curved strip with both faces drawn:
    (points, uvs, triangles) in species metres."""
    points, uvs, triangles = [], [], []
    curl = height * 0.55
    for k in range(petals):
        heading = 2 * math.pi * (k + 0.25) / petals
        out = np.array((math.cos(heading), math.sin(heading), 0.0))
        across = np.array((-math.sin(heading), math.cos(heading), 0.0))
        up = np.array((0.0, 0.0, 1.0))
        rim = centre + out * radius * 0.9
        start = len(points)
        for i in range(steps + 1):
            s = i / steps
            # Round a circle of radius `curl` beside the rim, from straight up at the rim to past level, outward.
            theta = math.pi * (1.0 - 1.15 * s)
            along = out * curl + (out * math.cos(theta) + up * math.sin(theta)) * curl
            half = radius * 0.7 * math.sin(math.pi * (0.2 + 0.8 * s)) * (1.0 - 0.4 * s) + 0.01
            for j in range(width):
                w = j / (width - 1) * 2 - 1
                points.append(rim + along + across * (half * w))
                uvs.append((j / (width - 1), s))
        # The back face has points of its own, so the two faces' normals do not cancel.
        count = (steps + 1) * width
        points += points[start:start + count]
        uvs += uvs[start:start + count]
        for i in range(steps):
            for j in range(width - 1):
                a0, a1 = start + i * width + j, start + i * width + j + 1
                b0, b1 = a0 + width, a1 + width
                triangles += [(a0, a1, b0), (a1, b1, b0)]
                triangles += [(a0 + count, b0 + count, a1 + count), (a1 + count, b0 + count, b1 + count)]
    return points, uvs, triangles


def paint_pod(width, height, rng):
    """A sealed pod: glowing amber with a few darker veins running up it."""
    image = np.zeros((height, width, 3))
    for row in range(height):
        for column in range(width):
            colour = AMBER_LIGHT if row < height * 0.7 else AMBER
            if column % 4 == 0 and 1 <= row < height - 1:
                colour = AMBER
            image[row, column] = colour + rng.integers(-6, 7, 3)
    return np.clip(image, 0, 255).astype(np.uint8)


def paint_husk(width, height, rng):
    """A husk's petal: ivory outside with growth lines, its tip plum where it split."""
    image = np.zeros((height, width, 3))
    for row in range(height):
        for column in range(width):
            colour = IVORY + rng.integers(-5, 6, 3)
            if row >= height - 2:
                colour = PLUM_DARK
            elif column in (0, width - 1):
                colour = IVORY_DARK
            elif row % 5 == 0:
                colour = IVORY_LINE
            image[row, column] = colour
    return np.clip(image, 0, 255).astype(np.uint8)


def pods_piece(spec, rng):
    """The brood bearer's pods (#10961), all six as one piece: `shape` "pods" seals each cup with a glowing pod (its glow
    part); "husks" is the same cups split, the petals curled back and a lens of glow left in each. `cups` lists each
    cup's centre in species metres, `radius` and `height` its pod's size. Returns (points, uvs, triangles, image, extra)."""
    split = spec["shape"] == "husks"
    radius, height = spec.get("radius", 0.16), spec.get("height", 0.13)
    shell, glow = ([], [], []), ([], [], [])
    for cup in spec["cups"]:
        centre = np.array(cup, dtype=float)
        if split:
            parts = [(shell, husk_petals(centre, radius, height)),
                     (glow, pod_ellipsoid(centre - np.array((0, 0, height * 0.55)), radius * 0.8, height * 0.35))]
        else:
            # A sealed pod's seam, a thin ivory collar where it meets its cup, carries the piece's own paint.
            parts = [(glow, pod_ellipsoid(centre, radius, height)),
                     (shell, pod_ellipsoid(centre - np.array((0, 0, height * 0.75)), radius * 1.04, height * 0.18))]
        for into, (points, uvs, triangles) in parts:
            base = len(into[0])
            into[0].extend(points)
            into[1].extend(uvs)
            into[2].extend((a + base, b + base, c + base) for a, b, c in triangles)
    image = paint_husk(8, 16, rng) if split else paint_crescent(16, 8, rng)
    core = paint_core(16) if split else paint_pod(16, 16, rng)
    extra = [("glow", np.array(glow[0]), np.array(glow[1]), glow[2], core)]
    return np.array(shell[0]), np.array(shell[1]), shell[2], image, extra


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
        if shell.get("shape") in ("pods", "husks"):
            points, uvs, triangles, image, extra = pods_piece(shell, rng)
            pieces[name] = write_piece(out, name, shell["joint"], inverse, points, uvs, triangles, image, content_root, extra=extra)
            continue
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
        grip = np.array(piece.get("grip", piece.get("base")), dtype=float)
        along = np.array(piece.get("along", [0, 1, 0]), dtype=float)
        along /= np.linalg.norm(along)
        up = np.array(piece.get("up", [0, 0, 1]), dtype=float)
        up -= along * np.dot(up, along)
        up /= np.linalg.norm(up)
        if piece.get("shape") == "silhouette":
            points, uvs, triangles, image, glow = silhouette_mesh(piece, sources)
            extra = [("glow", *glow)] if len(glow[2]) else []
            held[name] = write_piece(out, name, piece["joint"], inverse, points, uvs, triangles, image, content_root, extra=extra)
        elif piece.get("shape") == "crescent":
            points, uvs, triangles, core_points, core_uvs, core_triangles = crescent_mesh(piece, grip, along, up)
            held[name] = write_piece(out, name, piece["joint"], inverse, points, uvs, triangles, paint_crescent(48, 8, rng),
                                     content_root, extra=[("glow", core_points, core_uvs, core_triangles, paint_core(16))])
        else:
            points, uvs, triangles = blade_mesh(piece, grip, along, up)
            held[name] = write_piece(out, name, piece["joint"], inverse, points, uvs, triangles, paint_blade(32, 8), content_root)
        if "light" in piece:
            # A light that rides with the piece, at the piece's own point in its joint's frame (the crescent's core).
            light = dict(piece["light"])
            at = (np.array(piece["lightAt"], dtype=float) if "lightAt" in piece
                  else grip - up * piece.get("radius", 0.2) if piece.get("shape") == "crescent" else grip)
            light["at"] = [round(float(v), 4) for v in to_joint(at[None, :], piece["joint"], inverse)[0]]
            held[name]["light"] = light
    looks = {look: [pieces[name] for name in names] for look, names in spec["looks"].items()}
    (out / "pieces.json").write_text(json.dumps({"looks": looks, "held": held}, indent=2) + "\n")
    print(f"wrote {out / 'pieces.json'}: looks {', '.join(looks)}")


if __name__ == "__main__":
    main(sys.argv[1:])
