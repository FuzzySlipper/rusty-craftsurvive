"""Generate the flora catalog's trees and plants as CraftSurvive prop meshes (#9685, #10032).

Every species is data in content/game/flora/catalog.json: its form (trunk, branching levels, leaf
cards, fronds or a card clump), its bark and foliage styles, its height, its seeded variants and,
for the game, its role and ecology. With no arguments this builds every species' variants (and the
far variant of every tree), and the textures each one samples; naming species builds only those.
A species may start from another (`like`) and change parts of it (`set`, merged key by key).

Image-to-mesh crowns come out as lumpy shells. Instead, a tree here is built like an older game's:

1. a seeded branch skeleton: a tapered trunk and recursive limbs and twigs, each a low-sided tube
   UV-mapped to a small tiling bark texture (nearest-filtered in the game, so its texels read as
   chunky, of a piece with the leaves);
2. leaves as many flat cards along the outer twigs, each showing one cell of a painted
   leaf-cluster atlas, alpha-cut in the game. Card normals lean outward from the crown's centre
   (and up, toward the sky), so the crown lights as one soft mass rather than as scattered planes;
   each card is both windings for a one-sided material, so its back keeps that normal; cards deeper
   inside the crown are darker (vertex colour), for volume;
3. the wind weight in each vertex colour's alpha, which the Engine's flutter scales by: bark 0,
   leaves rising from the trunk axis to the crown's edge, with a card's far corners moving most.

Other forms (#9685): a pine's flat needle sprays along whorls of level branches; a dead tree's
bare limbs; a palm's curved trunk crowned with fronds, and a fern's fronds from the ground (each
frond a drooping strip showing a painted frond); and a bush's leaf cards on short stems, whose
wind weight rises from the root; and a flower clump (#9831), upright cards crossed at even angles, each
showing one cell of an atlas of painted clump sprites (scripts/make-grass-atlas.py, chosen with --cells), its
tip leaning and moving with the wind, as the grass clumps do. Bushes, ferns and flowers are written to
models/scatter/. Deadwood (#10034): a fallen log is a trunk lying along X ("lie"), with broken
limbs and capped ends ("caps"), and a stump a short capped trunk; both keep their authored metres
("scale": 1) rather than being scaled to a height. A fallen trunk tall enough to block the way also
writes "colliders": an upright-sided box per trunk segment ({"centre", "half", "yaw"}, metres).

With --far it also writes NAME-far: the distant-band variant (#9677) from the same skeleton and
scale, so the silhouette holds at the switch: only the trunk and first limbs, four-sided, and every
FAR_KEEP-th leaf card enlarged by FAR_GROW (fronds in two segments).

Textures are drawn from the palette ramps (content/style/palette.json): BARK-bark.png and
FOLIAGE-leaves.png in content/game/textures/trees/, named for the catalog's bark and foliage styles,
RGBA (the Engine admits only RGBA PNGs). They are the same for every seed of a species.

Output is the product prop-mesh JSON (Y up, metres; see PropMesh) with per-part UVs and the
texture each role samples:
    {"name", "height", "bounds", "textures": {"bark", "leaves"},
     "parts": [{"role", "positions", "normals", "uvs", "colors", "indices"}]}

    python3 scripts/generate-flora.py                 # every species in the catalog
    python3 scripts/generate-flora.py oak birch       # those species' variants and textures
    python3 scripts/generate-flora.py --sheet flora.png   # and a contact sheet of every variant (Blender)
    python3 scripts/generate-flora.py oak --seed 1 --name broadleaf --height 1.0 --far-only --folder map --no-textures
        # one asset outside the catalog's variants (the world map's clutter)
"""
import argparse
import json
import math
import random
import subprocess
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
CONTENT = ROOT / "content" / "game"
MODELS = CONTENT / "models"
TEXTURES = "textures/trees"
PALETTE = ROOT / "content" / "style" / "palette.json"
CATALOG = CONTENT / "flora" / "catalog.json"
DECIMALS = 4
UP = np.array([0.0, 1.0, 0.0])
GOLDEN_ANGLE = math.pi * (3 - math.sqrt(5))

# Bark texture: TEXELS_AROUND texels around a trunk of about BARK_TILE_METRES circumference,
# BARK_TEXELS_UP texels up the tile's height (square texels on the trunk).
BARK_TEXELS_AROUND, BARK_TEXELS_UP, BARK_TILE_METRES = 32, 64, 1.0
# The far variant keeps one leaf card in FAR_KEEP, enlarged FAR_GROW times, on limbs of FAR_SIDES.
FAR_KEEP, FAR_GROW, FAR_SIDES, FAR_FROND_SEGMENTS = 3, 1.6, 4, 2
# Leaf atlas: ATLAS_CELLS x ATLAS_CELLS clusters of LEAF_CELL pixels.
ATLAS_CELLS, LEAF_CELL = 2, 128
# Frond atlas: FROND_CELLS fronds side by side, each FROND_WIDTH x FROND_LENGTH pixels, base at the bottom.
FROND_CELLS, FROND_WIDTH, FROND_LENGTH = 2, 64, 256

# The catalog's species, each resolved to a full form: the trunk, then each branching level's children
# (count, where along the parent they start and end, angle from the parent's axis, length in metres
# before scaling, radius relative to the parent's at that point, sides, wobble, rise (+up / -droop) and
# segments), then the leaves (cards per twig, from how far along it, card size in metres, the
# crown-normal weight), fronds or a card clump. Foliage styles say how a leaf atlas is drawn: the ramp,
# the lighter ramp the most lit leaves come from (those lit above "light_from"), leaf length and width in
# pixels, leaves per cluster, how many of the atlas's cells carry a few autumn-turned leaves, and the
# darkest point of the ramp a leaf uses; needle sprays and fronds have their own sizes.


def merged(base, change):
    """A copy of base with change laid over it, dictionaries merged key by key."""
    out = dict(base)
    for key, value in change.items():
        out[key] = merged(out[key], value) if isinstance(value, dict) and isinstance(out.get(key), dict) else value
    return out


def load_catalog():
    catalog = json.loads(CATALOG.read_text())
    raw = catalog["species"]

    def resolve(name, seen=()):
        if name in seen:
            raise ValueError(f"species {name} inherits from itself")
        entry = raw[name]
        base = resolve(entry["like"], seen + (name,)) if "like" in entry else {}
        own = {k: v for k, v in entry.items() if k not in ("like", "set")}
        return merged(merged(base, own), entry.get("set", {}))

    return {name: resolve(name) for name in raw}, catalog["foliage"], catalog["barks"]


KINDS, LEAF_STYLES, BARK_STYLES = load_catalog()


def arguments():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("species", nargs="*", help="species to build (default every species in the catalog)")
    parser.add_argument("--seed", type=int, help="one asset: this seed of the one species named")
    parser.add_argument("--name", help="one asset: mesh name (default SPECIES-pSEED)")
    parser.add_argument("--height", type=float, help="one asset: metres to the crown's top (default the species')")
    parser.add_argument("--no-textures", action="store_true", help="leave the textures as they are")
    parser.add_argument("--far", action="store_true", help="one asset: also write NAME-far, the distant-band variant")
    parser.add_argument("--far-only", action="store_true", help="one asset: write only the distant-band variant, as NAME (the world map's clutter, #9831)")
    parser.add_argument("--folder", help="one asset: models subfolder to write to (default the species': trees, or scatter)")
    parser.add_argument("--frond-width", type=float, default=1.0,
                        help="one asset: multiply a palm's or fern's frond width (the map's palms, seen from far, need broad fronds)")
    parser.add_argument("--cells", help="one asset, flowers: comma-separated atlas cells (0-3) the cards show")
    parser.add_argument("--sheet", help="also write a contact sheet of the species' variants to this PNG (Blender)")
    parser.add_argument("--no-build", action="store_true", help="with --sheet: draw the sheet from the meshes as they are")
    args = parser.parse_args()
    unknown = [name for name in args.species if name not in KINDS]
    if unknown:
        parser.error(f"not in the catalog: {', '.join(unknown)}")
    args.single = any(value is not None for value in (args.seed, args.name, args.height, args.folder, args.cells)) \
        or args.far or args.far_only or args.frond_width != 1.0
    if args.single and len(args.species) != 1:
        parser.error("one asset (--seed, --name, ...) needs exactly one species")
    return args


def ramps():
    return {name: [tuple(int(h[i:i + 2], 16) for i in (1, 3, 5)) for h in values]
            for name, values in json.loads(PALETTE.read_text())["ramps"].items()}


def normalise(v):
    length = np.linalg.norm(v)
    return v / length if length > 1e-9 else v


def perpendicular(axis):
    helper = np.array([1.0, 0.0, 0.0]) if abs(axis[0]) < 0.9 else np.array([0.0, 0.0, 1.0])
    u = normalise(np.cross(axis, helper))
    return u, np.cross(axis, u)


def random_unit(rng):
    while True:
        v = np.array([rng.uniform(-1, 1), rng.uniform(-1, 1), rng.uniform(-1, 1)])
        if 0.01 < np.dot(v, v) <= 1:
            return normalise(v)


class Branch:
    def __init__(self, points, radii, level, sides):
        self.points, self.radii, self.level, self.sides = points, radii, level, sides
        self.lengths = np.concatenate([[0.0], np.cumsum([np.linalg.norm(b - a) for a, b in zip(points, points[1:])])])

    def at(self, t):
        """Point, axis and radius a fraction t along the branch."""
        target = t * self.lengths[-1]
        i = min(int(np.searchsorted(self.lengths, target, side="right")) - 1, len(self.points) - 2)
        i = max(i, 0)
        span = self.lengths[i + 1] - self.lengths[i]
        f = (target - self.lengths[i]) / span if span > 0 else 0.0
        point = self.points[i] + (self.points[i + 1] - self.points[i]) * f
        radius = self.radii[i] + (self.radii[i + 1] - self.radii[i]) * f
        return point, normalise(self.points[i + 1] - self.points[i]), radius


def grow(rng, kind, origin, direction, length, radius, level, branches):
    spec = kind["trunk"] if level == 0 else kind["levels"][level - 1]
    segments = spec["segments"]
    points, d = [origin], direction
    # A lying trunk wanders only sideways: up or down, it would float off or sink into the ground.
    level_only = np.array([1.0, 0.0, 1.0]) if spec.get("lie") else np.ones(3)
    for _ in range(segments):
        d = normalise(d + random_unit(rng) * spec["wobble"] * level_only + UP * spec["rise"])
        points.append(points[-1] + d * length / segments)
    radii = [radius * (1 - (1 - spec["tip"]) * i / segments) for i in range(segments + 1)]
    if level == 0:
        radii[0] *= spec["flare"]
    branch = Branch(points, radii, level, spec["sides"])
    branches.append(branch)
    if level >= len(kind["levels"]):
        return
    child = kind["levels"][level]
    count = rng.randint(*child["count"])
    azimuth = rng.uniform(0, 2 * math.pi)
    for k in range(count):
        t = child["start"] + (child["end"] - child["start"]) * (k + rng.uniform(0.2, 0.8)) / count
        point, axis, r = branch.at(t)
        # Spaced evenly round the parent ("even", for a balanced crown) or by the golden angle.
        azimuth += (2 * math.pi / count if child.get("even") else GOLDEN_ANGLE) + rng.uniform(-0.3, 0.3)
        angle = math.radians(rng.uniform(*child["angle"]))
        u, v = perpendicular(axis)
        side = u * math.cos(azimuth) + v * math.sin(azimuth)
        out = normalise(axis * math.cos(angle) + side * math.sin(angle))
        size = rng.uniform(*child["length"]) * (1 - child["shrink"] * t)
        grow(rng, kind, point, out, size, r * child["radius"], level + 1, branches)


def tube(branch, uv_metres):
    """A low-sided tube along the branch with parallel-transported rings and a duplicated UV seam."""
    positions, normals, uvs, indices = [], [], [], []
    sides = branch.sides
    axis = normalise(branch.points[1] - branch.points[0])
    u, v = perpendicular(axis)
    around = max(1, round(2 * math.pi * branch.radii[0] / BARK_TILE_METRES))
    for i, (point, radius) in enumerate(zip(branch.points, branch.radii)):
        if i > 0:
            new_axis = normalise(point - branch.points[i - 1])
            if i < len(branch.points) - 1:
                new_axis = normalise(new_axis + normalise(branch.points[i + 1] - point))
            # Transport the ring frame onto the new axis without twisting it.
            u = normalise(u - new_axis * np.dot(u, new_axis))
            v = np.cross(new_axis, u)
        for j in range(sides + 1):
            a = 2 * math.pi * j / sides
            n = u * math.cos(a) + v * math.sin(a)
            positions.append(point + n * radius)
            normals.append(n)
            uvs.append((around * j / sides, branch.lengths[i] / uv_metres))
    for i in range(len(branch.points) - 1):
        for j in range(sides):
            a, b = i * (sides + 1) + j, (i + 1) * (sides + 1) + j
            indices += [a, b, b + 1, a, b + 1, a + 1]
    return positions, normals, uvs, indices


def cap(branch, positions, end):
    """A flat cap closing a tube's "base" or "tip" ring (a cut log's or a stump's face), both windings."""
    ring = 0 if end == "base" else len(branch.points) - 1
    sides = branch.sides
    rim = positions[ring * (sides + 1): ring * (sides + 1) + sides]
    centre = branch.points[ring]
    axis = normalise(branch.points[1] - branch.points[0]) if end == "base" else normalise(branch.points[-1] - branch.points[-2])
    normal = -axis if end == "base" else axis
    cap_positions = [centre] + list(rim)
    normals = [normal] * len(cap_positions)
    # The cut face samples a small patch of the bark's lightest rows: rings of pale wood.
    uvs = [(0.5, 0.02)] + [(0.5 + 0.4 * math.cos(2 * math.pi * j / sides), 0.02 + 0.015 * math.sin(2 * math.pi * j / sides))
                           for j in range(sides)]
    indices = []
    for j in range(sides):
        a, b = 1 + j, 1 + (j + 1) % sides
        indices += [0, a, b, 0, b, a]
    return cap_positions, normals, uvs, indices


def leaf_cards(rng, kind, branches, crown_top, far=False):
    cards = kind["cards"]
    twigs = [b for b in branches if b.level == len(kind["levels"])]
    anchors = []
    longest = max(twig.lengths[-1] for twig in twigs)
    for twig in twigs:
        # With "size_by_reach" (pine), a short twig carries smaller cards, keeping a tapering
        # silhouette; otherwise every card is full size.
        reach_scale = 0.35 + 0.65 * twig.lengths[-1] / longest if cards.get("size_by_reach") else 1.0
        for _ in range(rng.randint(*cards["per_twig"])):
            t = rng.uniform(cards["from"], 1.0)
            point, axis, _ = twig.at(t)
            anchors.append((point, axis, reach_scale))
    if far:
        anchors = [(point, axis, reach_scale * FAR_GROW) for point, axis, reach_scale in anchors[::FAR_KEEP]]
    centre = np.mean([p for p, _, _ in anchors], axis=0)
    reach = max(np.linalg.norm((p - centre) * np.array([1, 0.8, 1])) for p, _, _ in anchors) or 1.0
    trunk_reach = max(math.hypot(p[0], p[2]) for p, _, _ in anchors) or 1.0
    positions, normals, uvs, colors, indices = [], [], [], [], []
    for point, axis, reach_scale in anchors:
        size = rng.uniform(*cards["size"]) * reach_scale
        outward = normalise(point - centre)
        centre_of_card = point + random_unit(rng) * size * cards["spread"] * 0.5
        jitter = random_unit(rng)
        if cards.get("roll"):
            # Sprays (pine) lie along their branch, each turned to a random angle about it, so a
            # whorl reads as a layer of needles from the side as well as from below.
            u, v = perpendicular(axis)
            roll = math.atan2(jitter[1], jitter[0])
            facing = normalise(u * math.cos(roll) + v * math.sin(roll) + UP * 0.3)
        else:
            facing = normalise(jitter + outward * 0.6)
        # The card's long axis follows the twig, hanging toward the ground for drooping kinds.
        along = normalise(axis * (1 - cards["hang"]) - UP * cards["hang"] + random_unit(rng) * 0.35)
        across = normalise(np.cross(facing, along))
        along = np.cross(across, facing)
        # Leaning up as well, so the crown takes the sky's light and not only the sun's.
        shade = normalise(outward * cards["crown_normal"] + facing * (1 - cards["crown_normal"]) + UP * 0.4)
        depth = min(1.0, np.linalg.norm((centre_of_card - centre) * np.array([1, 0.8, 1])) / reach)
        low = max(0.0, 1 - centre_of_card[1] / crown_top)
        tone = 0.86 + 0.14 * depth - 0.06 * low
        tone *= rng.uniform(0.9, 1.0)
        sway = (0.15 + 0.85 * min(1.0, max(0.0, centre_of_card[1]) / crown_top) if cards.get("rooted")
                else 0.35 + 0.5 * min(1.0, math.hypot(centre_of_card[0], centre_of_card[2]) / trunk_reach))
        cell = rng.randrange(ATLAS_CELLS * ATLAS_CELLS)
        u0, v0 = (cell % ATLAS_CELLS) / ATLAS_CELLS, (cell // ATLAS_CELLS) / ATLAS_CELLS
        u1, v1 = u0 + 1 / ATLAS_CELLS, v0 + 1 / ATLAS_CELLS
        first = len(positions)
        half_a, half_b = across * size / 2, along * size * cards.get("aspect", 1.0) / 2
        corners = [centre_of_card - half_a - half_b, centre_of_card + half_a - half_b,
                   centre_of_card + half_a + half_b, centre_of_card - half_a + half_b]
        for corner, uv in zip(corners, [(u0, v1), (u1, v1), (u1, v0), (u0, v0)]):
            positions.append(corner)
            normals.append(shade)
            uvs.append(uv)
            reach_out = min(1.0, np.linalg.norm(corner - point) / size)
            colors.append((tone, tone, tone, min(1.0, sway + 0.2 * reach_out)))
        # Both windings, so each side is drawn by a one-sided material with the crown's normal; a
        # two-sided material would flip the normal on the card's back and darken half the crown.
        indices += [first, first + 1, first + 2, first, first + 2, first + 3,
                    first, first + 2, first + 1, first, first + 3, first + 2]
    return positions, normals, uvs, colors, indices


def frond_strips(rng, spec, origin):
    """Drooping strips radiating from origin, each showing one painted frond, base to tip."""
    positions, normals, uvs, colors, indices = [], [], [], [], []
    count = rng.randint(*spec["count"])
    azimuth = rng.uniform(0, 2 * math.pi)
    segments = spec["segments"]
    for _ in range(count):
        azimuth += 2 * math.pi / count + rng.uniform(-0.25, 0.25)
        elevation = math.radians(rng.uniform(*spec["elevation"]))
        length, width = rng.uniform(*spec["length"]), rng.uniform(*spec["width"])
        d = np.array([math.cos(elevation) * math.cos(azimuth), math.sin(elevation), math.cos(elevation) * math.sin(azimuth)])
        side = normalise(normalise(np.cross(d, UP)) + UP * rng.uniform(-0.2, 0.2))
        normal = normalise(np.cross(side, d))
        if normal[1] < 0:
            normal = -normal
        normal = normalise(normal + UP * 0.5)
        spine = [origin.copy()]
        for _ in range(segments):
            d = normalise(d - UP * spec["droop"])
            spine.append(spine[-1] + d * length / segments)
        cell = rng.randrange(FROND_CELLS)
        u0, u1 = cell / FROND_CELLS, (cell + 1) / FROND_CELLS
        tone = rng.uniform(0.88, 1.0)
        first = len(positions)
        for i, point in enumerate(spine):
            t = i / segments
            for sign, u in ((-1, u0), (1, u1)):
                positions.append(point + side * sign * width / 2)
                normals.append(normal)
                uvs.append((u, 1 - t))
                colors.append((tone, tone, tone, spec["root_sway"] + (1 - spec["root_sway"]) * t))
        for i in range(segments):
            a, b = first + 2 * i, first + 2 * i + 2
            # Both windings, as for leaf cards.
            indices += [a, a + 1, b + 1, a, b + 1, b, a, b + 1, a + 1, a, b, b + 1]
    return positions, normals, uvs, colors, indices


def card_clump(rng, spec, cells):
    """Upright cards crossed at even angles about the origin, each showing one cell of a 2x2 sprite atlas,
    its tip leaning a little and moving with the wind (weight 0 at the root, 1 at the tip)."""
    positions, normals, uvs, colors, indices = [], [], [], [], []
    count = rng.randint(*spec["cards"])
    turn = rng.uniform(0, math.pi)
    for k in range(count):
        angle = turn + math.pi * k / count + rng.uniform(-0.15, 0.15)
        across = np.array([math.cos(angle), 0.0, math.sin(angle)])
        facing = np.array([-math.sin(angle), 0.0, math.cos(angle)])
        root = np.array([rng.uniform(-1, 1), 0.0, rng.uniform(-1, 1)]) * spec["spread"]
        size = rng.uniform(*spec["size"])
        lean = random_unit(rng) * np.array([1.0, 0.0, 1.0]) * size * spec["lean"]
        cell = cells[k % len(cells)]
        u0, v0 = (cell % 2) / 2, (cell // 2) / 2
        u1, v1 = u0 + 0.5, v0 + 0.5
        shade = normalise(UP * 0.7 + facing * 0.3)
        first = len(positions)
        for corner, uv, weight in ((root - across * size / 2, (u0, v1), 0.0), (root + across * size / 2, (u1, v1), 0.0),
                                   (root + across * size / 2 + UP * size + lean, (u1, v0), 1.0),
                                   (root - across * size / 2 + UP * size + lean, (u0, v0), 1.0)):
            positions.append(corner)
            normals.append(shade)
            uvs.append(uv)
            colors.append((1.0, 1.0, 1.0, weight))
        indices += [first, first + 1, first + 2, first, first + 2, first + 3,
                    first, first + 2, first + 1, first, first + 3, first + 2]
    return positions, normals, uvs, colors, indices


def build(kind_name, seed, height, far=False, scale=None, cells=None, frond_width=1.0):
    kind = KINDS[kind_name]
    rng = random.Random(f"{kind_name}-{seed}")
    trunk = kind["trunk"]
    branches = []
    if trunk is not None:
        root = np.array([0.0, -kind.get("sink", 0.4), 0.0])
        stems = trunk.get("stems", 1)
        if trunk.get("lie"):
            # A fallen trunk (#10034): it lies along X, half sunk, its root end at -X.
            root = np.array([-trunk["length"] / 2, trunk["radius"] * (1 - 2 * kind.get("sink", 0.4)), 0.0])
            along = normalise(np.array([1.0, 0.0, 0.0]) + random_unit(rng) * trunk.get("lean", 0.06) * np.array([0.0, 0.0, 1.0]))
            grow(rng, kind, root, along, trunk["length"], trunk["radius"], 0, branches)
        elif stems == 1:
            lean = normalise(UP + random_unit(rng) * trunk.get("lean", 0.06))
            grow(rng, kind, root, lean, trunk["length"], trunk["radius"], 0, branches)
        else:
            # Stems spread round the root at "splay" from upright, each a little shorter and thinner.
            azimuth = rng.uniform(0, 2 * math.pi)
            for _ in range(stems):
                azimuth += 2 * math.pi / stems + rng.uniform(-0.4, 0.4)
                splay = math.radians(rng.uniform(*trunk["splay"]))
                out = np.array([math.cos(azimuth) * math.sin(splay), math.cos(splay), math.sin(azimuth) * math.sin(splay)])
                share = rng.uniform(0.75, 1.0)
                grow(rng, kind, root, normalise(out + random_unit(rng) * trunk.get("lean", 0.06)),
                     trunk["length"] * share, trunk["radius"] * share, 0, branches)
    crown_top = max((p[1] for b in branches for p in b.points), default=1.0)
    bark = {"positions": [], "normals": [], "uvs": [], "colors": [], "indices": []}
    uv_metres = BARK_TILE_METRES * BARK_TEXELS_UP / BARK_TEXELS_AROUND
    # The far variant draws only the trunk and its first limbs, four-sided.
    drawn = [b for b in branches if not far or b.level <= max(0, min(1, len(kind["levels"]) - 1))]
    for branch in drawn if kind["bark"] else []:
        if far:
            branch.sides = min(branch.sides, FAR_SIDES)
        p, n, uv, idx = tube(branch, uv_metres)
        if branch.level == 0:
            for end in kind.get("caps", []):
                cp, cn, cuv, cidx = cap(branch, p, end)
                idx = idx + [len(p) + i for i in cidx]
                p, n, uv = p + cp, n + cn, uv + cuv
        first = len(bark["positions"])
        bark["positions"] += p
        bark["normals"] += n
        bark["uvs"] += uv
        bark["indices"] += [first + i for i in idx]
    # Bark is darker at the foot (soil and moss) and white elsewhere; it holds still (alpha 0).
    for p in bark["positions"]:
        foot = min(1.0, max(0.0, p[1] / 1.4))
        tone = 0.6 + 0.4 * foot
        bark["colors"].append((tone, tone, tone, 0.0))
    leaves = {"positions": [], "normals": [], "uvs": [], "colors": [], "indices": []}
    pieces = []
    if kind["cards"]:
        pieces.append(leaf_cards(rng, kind, branches, crown_top, far))
    if kind.get("fronds"):
        fronds = kind["fronds"]
        origin = branches[0].points[-1] if fronds["at"] == "top" else np.array([0.0, -0.05, 0.0])
        fronds = dict(fronds, width=tuple(w * frond_width for w in fronds["width"]))
        pieces.append(frond_strips(rng, dict(fronds, segments=FAR_FROND_SEGMENTS) if far else fronds, origin))
    if kind.get("clump"):
        pieces.append(card_clump(rng, kind["clump"], cells or kind["clump"]["cells"]))
    for lp, ln, luv, lc, li in pieces:
        first = len(leaves["positions"])
        leaves["positions"] += lp
        leaves["normals"] += ln
        leaves["uvs"] += luv
        leaves["colors"] += lc
        leaves["indices"] += [first + i for i in li]
    if scale is None:
        # A form drawn in metres (a log or a stump, #10034) keeps them; others are scaled to their height.
        scale = kind.get("scale") or height / max(p[1] for p in bark["positions"] + leaves["positions"])
    low, high = [math.inf] * 3, [-math.inf] * 3
    parts = []
    for role, part in (("bark", bark), ("leaves", leaves)):
        if not part["indices"]:
            continue
        positions = []
        for p in part["positions"]:
            q = [float(c) * scale for c in p]
            for axis in range(3):
                low[axis] = min(low[axis], q[axis])
                high[axis] = max(high[axis], q[axis])
            positions += [round(c, DECIMALS) for c in q]
        parts.append({
            "role": role,
            "positions": positions,
            "normals": [round(float(c), DECIMALS) for n in part["normals"] for c in n],
            "uvs": [round(float(c), DECIMALS) for uv in part["uvs"] for c in uv],
            "colors": [round(float(c), DECIMALS) for col in part["colors"] for c in col],
            "indices": part["indices"],
        })
    return parts, low, high, scale, trunk_boxes(kind, branches, scale)


def trunk_boxes(kind, branches, scale):
    """A deadwood trunk's collision (#10034): an upright-sided box per trunk segment, in the mesh's metres.

    A round trunk's top is gentle enough to walk up once a step has lifted the walker, so a trunk meant to
    block is given straight sides: each box reaches the trunk's radius either side and above its axis.
    """
    if kind.get("colliders") != "trunk" or not branches:
        return []
    trunk = branches[0]
    boxes = []
    for i in range(len(trunk.points) - 1):
        a, b = trunk.points[i] * scale, trunk.points[i + 1] * scale
        radius = max(trunk.radii[i], trunk.radii[i + 1]) * scale
        along = b - a
        boxes.append({
            "centre": [round(float(c), DECIMALS) for c in (a + b) / 2],
            "half": [round(float(np.linalg.norm(along[[0, 2]])) / 2, DECIMALS), round(float(radius), DECIMALS), round(float(radius), DECIMALS)],
            # Turned about +Y from +X, as the game turns a record: +X goes to (cos, 0, -sin).
            "yaw": round(math.atan2(-float(along[2]), float(along[0])), DECIMALS),
        })
    return boxes


# ---- textures -------------------------------------------------------------------------------


def wrap_noise(rng, width, height, scale):
    """Tiling value noise: random lattice values, bilinear, wrapping on both axes."""
    cells_x, cells_y = max(1, width // scale), max(1, height // scale)
    lattice = np.array([[rng.random() for _ in range(cells_x)] for _ in range(cells_y)])
    ys, xs = np.mgrid[0:height, 0:width]
    fx, fy = xs / width * cells_x, ys / height * cells_y
    x0, y0 = np.floor(fx).astype(int), np.floor(fy).astype(int)
    tx, ty = fx - x0, fy - y0
    tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)
    x1, y1 = (x0 + 1) % cells_x, (y0 + 1) % cells_y
    x0, y0 = x0 % cells_x, y0 % cells_y
    top = lattice[y0, x0] * (1 - tx) + lattice[y0, x1] * tx
    bottom = lattice[y1, x0] * (1 - tx) + lattice[y1, x1] * tx
    return top * (1 - ty) + bottom * ty


def ramp_colour(ramp, value):
    """A ramp sampled at value 0..1 (stepped between its five colours, for a painted look)."""
    index = min(len(ramp) - 1, max(0, int(round(value * (len(ramp) - 1)))))
    return np.array(ramp[index], dtype=float)


def birch_bark(palette, rng):
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    light = palette["snow"]
    tone = 0.55 + 0.45 * wrap_noise(rng, w, h, 8) * 0.6 + 0.25 * wrap_noise(rng, w, h, 4) * 0.6
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(light[1:], tone[y, x])
    dark = np.array(palette["stone"][0], dtype=float)
    grey = np.array(palette["stone"][2], dtype=float)
    # Lenticels: short dark horizontal dashes, and a few broad dark scars.
    for _ in range(26):
        y, x, length = rng.randrange(h), rng.randrange(w), rng.randint(3, 9)
        colour = dark if rng.random() < 0.7 else grey
        for i in range(length):
            image[y, (x + i) % w] = colour
            if length > 6 and rng.random() < 0.5:
                image[(y + 1) % h, (x + i) % w] = colour
    for _ in range(3):
        y, x = rng.randrange(h), rng.randrange(w)
        for dy in range(rng.randint(2, 4)):
            for dx in range(rng.randint(4, 9) - dy):
                image[(y + dy) % h, (x + dx + dy) % w] = dark
    return image


def oak_bark(palette, rng, ramp_name="bark"):
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette[ramp_name]
    tone = 0.35 + 0.4 * wrap_noise(rng, w, h, 8) + 0.2 * wrap_noise(rng, w, h, 4)
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:], tone[y, x])
    deep = (np.array(ramp[0], dtype=float) + np.array(ramp[1], dtype=float)) / 2
    ridge = np.array(ramp[3], dtype=float)
    # Fissures: dark lines wandering up the tile in long runs, with a lit ridge beside them, so
    # the bark reads as vertical plates rather than a checker.
    for start in range(0, w, 6):
        x = start + rng.randint(0, 2)
        for y in range(h):
            if rng.random() < 0.15:
                x += rng.choice((-1, 1))
            if rng.random() < 0.9:
                image[y, x % w] = deep
            if rng.random() < 0.35:
                image[y, (x + 1) % w] = ridge
    return image


def pine_bark(palette, rng):
    """Reddish plates in staggered rows, split by dark cracks, each lit along its top edge."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = [((np.array(b) * 2 + np.array(a)) / 3).tolist() for a, b in zip(palette["autumn"], palette["bark"])]
    tone = 0.3 + 0.45 * wrap_noise(rng, w, h, 8)
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:4], tone[y, x])
    crack, lit = np.array(ramp[0]), np.array(ramp[3])
    y = 0
    while y < h:
        rows = rng.randint(5, 9)
        x = rng.randrange(w)
        while x < w + 32:
            width = rng.randint(6, 11)
            for dx in range(width):
                image[y % h, (x + dx) % w] = crack
                if rng.random() < 0.6:
                    image[(y + 1) % h, (x + dx) % w] = lit
            image[(y + rng.randint(1, rows - 1)) % h, (x + width) % w] = crack
            for dy in range(rows):
                image[(y + dy) % h, (x + width) % w] = crack
            x += width + 1
        y += rows
    return image


def palm_bark(palette, rng):
    """Rings of old frond bases: a dark seam every few texels, a lit lip under it, faint diagonal scars."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette["sandstone"]
    earth = palette["earth"]
    tone = 0.25 + 0.4 * wrap_noise(rng, w, h, 8)
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = (ramp_colour(ramp[:3], tone[y, x]) + ramp_colour(earth[2:], tone[y, x])) / 2
    seam, lip = np.array(earth[1]), np.array(ramp[2])
    for y in range(0, h, 6 + rng.randint(0, 1)):
        offset = rng.randint(-1, 1)
        for x in range(w):
            wave = int(round(math.sin(2 * math.pi * x / w * 2) * 1)) + offset
            image[(y + wave) % h, x] = seam
            image[(y + wave + 1) % h, x] = lip
            if (x + y // 6) % 5 == 0:
                image[(y + wave + 3) % h, x] = seam * 0.6 + image[(y + wave + 3) % h, x] * 0.4
    return image


def smooth_bark(palette, rng, style):
    """Smooth bark (beech, alder, young poplar): a soft grey, faint horizontal marks and a few dark rings."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette[style.get("ramp", "stone")]
    tone = 0.45 + 0.3 * wrap_noise(rng, w, h, 16) + 0.12 * wrap_noise(rng, w, h, 4)
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:], tone[y, x])
    mark = np.array(ramp[1], dtype=float)
    for _ in range(style.get("marks", 14)):
        y, x, length = rng.randrange(h), rng.randrange(w), rng.randint(2, 6)
        for i in range(length):
            image[y, (x + i) % w] = mark
    return image


def corky_bark(palette, rng, style):
    """Thick, spongy cork in deep blocky ridges (cork oak), warm grey-brown with dark furrows."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette[style.get("ramp", "earth")]
    image = np.zeros((h, w, 3))
    tone = 0.45 + 0.35 * wrap_noise(rng, w, h, 8)
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[2:], tone[y, x])
    furrow = np.array(ramp[0], dtype=float)
    y = 0
    while y < h:
        rows = rng.randint(6, 12)
        x = rng.randrange(w)
        while x < w + 16:
            width = rng.randint(4, 8)
            for dy in range(rows):
                image[(y + dy) % h, (x + width) % w] = furrow
                image[(y + dy) % h, (x + width + 1) % w] = furrow
            for dx in range(width):
                image[y % h, (x + dx) % w] = furrow
            x += width + 2
        y += rows
    return image


def spiral_bark(palette, rng, style):
    """Fissures running up the trunk on a slant (sweet chestnut)."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette[style.get("ramp", "bark")]
    tone = 0.35 + 0.4 * wrap_noise(rng, w, h, 8)
    image = np.zeros((h, w, 3))
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:], tone[y, x])
    deep, ridge = np.array(ramp[0], dtype=float), np.array(ramp[3], dtype=float)
    slant = style.get("slant", 0.25)
    for start in range(0, w, 5):
        for y in range(h):
            x = int(start + y * slant + rng.choice((0, 0, 1)))
            image[y, x % w] = deep
            if rng.random() < 0.4:
                image[y, (x + 1) % w] = ridge
    return image


def gnarled_bark(palette, rng, style):
    """Old twisted wood (olive): grey, with dark hollows and knots and pale worn ridges."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette[style.get("ramp", "stone")]
    image = np.zeros((h, w, 3))
    tone = 0.3 + 0.5 * wrap_noise(rng, w, h, 8) * wrap_noise(rng, w, h, 16)
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:], tone[y, x] + 0.15 * math.sin((x + y * 0.4) / 3))
    dark = np.array(ramp[0], dtype=float)
    for _ in range(style.get("hollows", 6)):
        cx, cy, rx, ry = rng.randrange(w), rng.randrange(h), rng.randint(1, 3), rng.randint(2, 5)
        for dy in range(-ry, ry + 1):
            for dx in range(-rx, rx + 1):
                if (dx / max(1, rx)) ** 2 + (dy / ry) ** 2 <= 1:
                    image[(cy + dy) % h, (cx + dx) % w] = dark
    return image


def stringy_bark(palette, rng, style):
    """Long fibrous strips peeling up the trunk (juniper), reddish with dark seams."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = [((np.array(b) + np.array(a)) / 2).tolist() for a, b in zip(palette["autumn"], palette["bark"])]
    image = np.zeros((h, w, 3))
    for x in range(w):
        strip = rng.uniform(0.25, 0.75)
        for y in range(h):
            image[y, x] = ramp_colour(ramp[1:], strip + 0.15 * math.sin(y / rng.uniform(4, 9)))
    seam = np.array(ramp[0], dtype=float)
    for start in range(0, w, 3):
        x = start + rng.randint(0, 1)
        for y in range(h):
            if rng.random() < 0.08:
                x += rng.choice((-1, 1))
            if rng.random() < 0.8:
                image[y, x % w] = seam
    return image


def glass_bark(palette, rng, style):
    """Fantastical (#10032): wood grown half into glass, pale teal facets split by bright seams."""
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp, light = palette[style.get("ramp", "teal")], palette["snow"]
    image = np.zeros((h, w, 3))
    tone = wrap_noise(rng, w, h, 8)
    for y in range(h):
        for x in range(w):
            image[y, x] = ramp_colour(ramp[1:], 0.3 + 0.6 * round(tone[y, x] * 3) / 3)
    seam = np.array(light[3], dtype=float)
    for _ in range(style.get("seams", 10)):
        x, y = rng.randrange(w), rng.randrange(h)
        dx, dy = rng.choice((-1, 1)), rng.choice((1, 2))
        for i in range(rng.randint(6, 16)):
            image[(y + i * dy) % h, (x + i * dx) % w] = seam
    return image


# Bark patterns by name; a style may give the ramp a pattern draws from (fissured: weathered grey wood
# is the oak's fissured plates in the stone ramp).
BARKS = {"lenticel": lambda palette, rng, style: birch_bark(palette, rng),
         "fissured": lambda palette, rng, style: oak_bark(palette, rng, style.get("ramp", "bark")),
         "plated": lambda palette, rng, style: pine_bark(palette, rng),
         "ringed": lambda palette, rng, style: palm_bark(palette, rng),
         "smooth": smooth_bark, "corky": corky_bark, "spiral": spiral_bark, "gnarled": gnarled_bark,
         "stringy": stringy_bark, "glass": glass_bark}


def leaf_polygon(cx, cy, length, width, angle, shape="pointed"):
    """A leaf's outline: pointed (an ellipse-like outline sharpened at both ends), round, or a shard
    (an angular, glassy sliver)."""
    if shape == "round":
        return [(cx + math.cos(a) * length / 2 * math.cos(angle) - math.sin(a) * width / 2 * math.sin(angle),
                 cy + math.cos(a) * length / 2 * math.sin(angle) + math.sin(a) * width / 2 * math.cos(angle))
                for a in (2 * math.pi * k / 10 for k in range(10))]
    if shape == "shard":
        outline = ((-0.5, 0.0), (-0.1, 0.45), (0.5, 0.05), (0.15, -0.4))
        return [(cx + x * length * math.cos(angle) - y * width * math.sin(angle),
                 cy + x * length * math.sin(angle) + y * width * math.cos(angle)) for x, y in outline]
    points = []
    for k in range(10):
        a = 2 * math.pi * k / 10
        lx = math.cos(a) * length / 2
        ly = math.sin(a) * width / 2 * (abs(math.cos(a)) ** 0.0) * (1 - 0.35 * abs(math.cos(a)) ** 3)
        points.append((cx + lx * math.cos(angle) - ly * math.sin(angle),
                       cy + lx * math.sin(angle) + ly * math.cos(angle)))
    return points


def leaf_atlas(palette, style, rng):
    size = LEAF_CELL * ATLAS_CELLS
    atlas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ramp = palette[style["ramp"]]
    twig = tuple(palette["bark"][1]) + (255,)
    turned = set(rng.sample(range(ATLAS_CELLS * ATLAS_CELLS), style["turned_cells"]))
    for cell in range(ATLAS_CELLS * ATLAS_CELLS):
        layer = Image.new("RGBA", (LEAF_CELL, LEAF_CELL), (0, 0, 0, 0))
        draw = ImageDraw.Draw(layer)
        c = LEAF_CELL / 2
        # Twigs radiating from the cluster's centre, under the leaves.
        tips = []
        for k in range(rng.randint(4, 6)):
            a = k * 2 * math.pi / 5 + rng.uniform(-0.4, 0.4)
            r = LEAF_CELL * rng.uniform(0.28, 0.4)
            tip = (c + math.cos(a) * r, c + math.sin(a) * r)
            tips.append(tip)
            draw.line([(c, c), tip], fill=twig, width=2)
        leaves = []
        for _ in range(rng.randint(*style["leaves"])):
            a, r = rng.uniform(0, 2 * math.pi), LEAF_CELL * 0.4 * math.sqrt(rng.random())
            x, y = c + math.cos(a) * r, c + math.sin(a) * r
            # Leaves nearer the cluster's centre and top sit in front and catch more light.
            front = 1 - r / (LEAF_CELL * 0.4)
            lit = min(1.0, max(0.0, 0.45 * front + 0.4 * (1 - y / LEAF_CELL) + rng.uniform(-0.15, 0.25)))
            leaves.append((front + rng.uniform(-0.3, 0.3), x, y, a, lit))
        for _, x, y, a, lit in sorted(leaves):
            length, width = rng.uniform(*style["length"]), rng.uniform(*style["width"])
            shade = style["floor"] + (1 - style["floor"]) * lit
            lighter = shade > style["light_from"]
            colour = ramp_colour(palette[style["light"]] if lighter else ramp, shade - 0.2 if lighter else shade)
            if cell in turned and rng.random() < 0.12:
                colour = ramp_colour(palette[style["accent"]], rng.uniform(0.5, 1.0))
            angle = a + rng.uniform(-0.6, 0.6)
            draw.polygon(leaf_polygon(x, y, length, width, angle, style.get("shape", "pointed")),
                         fill=tuple(int(v) for v in colour) + (255,))
            # A darker underside edge gives each leaf a readable shape inside the mass.
            shadow = tuple(int(v) for v in ramp_colour(ramp, max(0.0, shade - 0.3))) + (255,)
            ex, ey = x + math.cos(angle + math.pi / 2) * width * 0.3, y + math.sin(angle + math.pi / 2) * width * 0.3
            draw.line([(ex - math.cos(angle) * length * 0.3, ey - math.sin(angle) * length * 0.3),
                       (ex + math.cos(angle) * length * 0.3, ey + math.sin(angle) * length * 0.3)], fill=shadow, width=1)
        if style.get("blossom"):
            blossoms(draw, palette, style["blossom"], rng)
        atlas.paste(layer, ((cell % ATLAS_CELLS) * LEAF_CELL, (cell // ATLAS_CELLS) * LEAF_CELL))
    return atlas


def blossoms(draw, palette, spec, rng):
    """Flowers or berries over a cluster: small dots in the spec's ramp, each with a darker lower edge."""
    ramp = palette[spec["ramp"]]
    c = LEAF_CELL / 2
    for _ in range(rng.randint(*spec["count"])):
        a, r = rng.uniform(0, 2 * math.pi), LEAF_CELL * 0.38 * math.sqrt(rng.random())
        x, y, size = c + math.cos(a) * r, c + math.sin(a) * r, rng.uniform(*spec["size"])
        shade = rng.uniform(*spec["range"])
        draw.ellipse([x - size, y - size, x + size, y + size], fill=tuple(int(v) for v in ramp_colour(ramp, shade)) + (255,))
        draw.arc([x - size, y - size, x + size, y + size], 20, 160,
                 fill=tuple(int(v) for v in ramp_colour(ramp, max(0.0, shade - 0.35))) + (255,))


def needle_atlas(palette, style, rng):
    """Pine sprays: a twig from the cell's base toward its top with side twigs, all bristling with
    short needles angled toward the tip; darker beneath and lighter at the tips."""
    size = LEAF_CELL * ATLAS_CELLS
    atlas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    ramp, light = palette[style["ramp"]], palette[style["light"]]
    twig = tuple(palette["bark"][1]) + (255,)
    for cell in range(ATLAS_CELLS * ATLAS_CELLS):
        layer = Image.new("RGBA", (LEAF_CELL, LEAF_CELL), (0, 0, 0, 0))
        draw = ImageDraw.Draw(layer)
        base = (LEAF_CELL / 2 + rng.uniform(-6, 6), LEAF_CELL - 6)
        tip = (LEAF_CELL / 2 + rng.uniform(-14, 14), 10)
        twigs = [(base, tip)]
        sides = rng.randint(*style["side_twigs"])
        for k in range(sides):
            t = 0.12 + 0.75 * k / sides + rng.uniform(-0.04, 0.04)
            at = (base[0] + (tip[0] - base[0]) * t, base[1] + (tip[1] - base[1]) * t)
            side = -1 if k % 2 else 1
            reach = LEAF_CELL * rng.uniform(0.3, 0.42) * (1 - 0.45 * t)
            twigs.append((at, (at[0] + side * reach, at[1] - reach * rng.uniform(0.4, 0.8))))
        for a, b in twigs:
            draw.line([a, b], fill=twig, width=2)
        for layer_pass in range(2):
            for a, b in twigs:
                steps = int(math.dist(a, b) / 2)
                for i in range(steps):
                    t = i / max(1, steps)
                    x, y = a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t
                    heading = math.atan2(b[1] - a[1], b[0] - a[0])
                    for sign in (-1, 1):
                        length = rng.uniform(*style["needle"]) * (1 - 0.35 * t)
                        angle = heading + sign * rng.uniform(0.6, 1.0)
                        lit = min(1.0, max(0.0, 0.3 + 0.45 * t + 0.2 * layer_pass + rng.uniform(-0.2, 0.2)))
                        colour = ramp_colour(light, lit - 0.3) if lit > 0.85 else ramp_colour(ramp, 0.15 + lit * 0.7)
                        draw.line([(x, y), (x + math.cos(angle) * length, y + math.sin(angle) * length)],
                                  fill=tuple(int(v) for v in colour) + (255,), width=3 if layer_pass == 0 else 2)
        atlas.paste(layer, ((cell % ATLAS_CELLS) * LEAF_CELL, (cell // ATLAS_CELLS) * LEAF_CELL))
    return atlas


def frond_atlas(palette, style, rng):
    """Fronds side by side, base at the bottom: a rib up the middle and paired leaflets angled
    toward the tip, longest a third of the way up; lighter toward the tip."""
    atlas = Image.new("RGBA", (FROND_WIDTH * FROND_CELLS, FROND_LENGTH), (0, 0, 0, 0))
    draw = ImageDraw.Draw(atlas)
    ramp, light = palette[style["ramp"]], palette[style["light"]]
    # The rib in the frond's own darkest green, so up close it reads as part of the leaf, not a stick.
    rib = tuple(int(v) for v in ramp_colour(ramp, 0.0)) + (255,)
    for cell in range(FROND_CELLS):
        cx = cell * FROND_WIDTH + FROND_WIDTH / 2
        y = FROND_LENGTH - 4
        while y > 6:
            t = 1 - y / FROND_LENGTH
            reach = style["leaflet"] * math.sin(math.pi * min(1.0, 0.15 + t * 0.95)) ** 0.7
            for sign in (-1, 1):
                angle = math.radians(rng.uniform(*style["angle"]))
                ex, ey = cx + sign * reach * math.cos(angle), y - reach * math.sin(angle)
                nx, ny = -math.sin(angle) * style["leaflet_width"] / 2, -math.cos(angle) * style["leaflet_width"] / 2
                lit = min(1.0, max(0.0, 0.3 + 0.5 * t + rng.uniform(-0.2, 0.2)))
                colour = ramp_colour(light, lit - 0.2) if lit > 0.8 else ramp_colour(ramp, 0.25 + lit * 0.7)
                draw.polygon([(cx, y), (cx + sign * nx * 0.4 + (ex - cx) * 0.5, y + ny + (ey - y) * 0.5),
                              (ex, ey), (cx - sign * nx * 0.4 + (ex - cx) * 0.5, y - ny + (ey - y) * 0.5)],
                             fill=tuple(int(v) for v in colour) + (255,))
            y -= style["spacing"] + rng.uniform(-1, 1)
        draw.line([(cx, FROND_LENGTH - 2), (cx, 6)], fill=rib, width=1)
    return atlas


LEAF_FORMS = {"cluster": leaf_atlas, "needles": needle_atlas, "frond": frond_atlas}


def write_textures(kind_name):
    kind = KINDS[kind_name]
    palette = ramps()
    folder = CONTENT / TEXTURES
    folder.mkdir(parents=True, exist_ok=True)
    if kind["bark"]:
        style = BARK_STYLES[kind["bark"]]
        bark = BARKS[style["pattern"]](palette, random.Random(f"bark-{kind['bark']}"), style)
        Image.fromarray(np.clip(bark, 0, 255).astype(np.uint8), "RGB").convert("RGBA").save(folder / f"{kind['bark']}-bark.png")
    if kind["leaves"] and LEAF_STYLES[kind["leaves"]].get("form") != "sprites":
        style = LEAF_STYLES[kind["leaves"]]
        LEAF_FORMS[style.get("form", "cluster")](palette, style, random.Random(f"leaves-{kind['leaves']}")).save(
            folder / f"{kind['leaves']}-leaves.png")


def variant_name(species, kind, seed):
    """Trees are SPECIES-pSEED, beside their SPECIES-pSEED-far; scatter is SPECIES-SEED."""
    return f"{species}-p{seed}" if kind["role"] == "tree" else f"{species}-{seed}"


def build_one(args):
    species = args.species[0]
    kind = KINDS[species]
    seed = args.seed or 1
    name = args.name or f"{species}-p{seed}"
    height = args.height or kind["height"]
    if not args.no_textures:
        write_textures(species)
    cells = [int(c) for c in args.cells.split(",")] if args.cells else None
    parts, low, high, scale, boxes = build(species, seed, height, cells=cells, frond_width=args.frond_width)
    folder = args.folder or kind.get("folder", "trees")
    if not args.far_only:
        write(kind, name, height, parts, low, high, seed, folder, boxes)
    if args.far or args.far_only:
        # Same skeleton and scale as the near tree; only what is drawn of it changes.
        parts, low, high, _, _ = build(species, seed, height, far=True, scale=scale, frond_width=args.frond_width)
        write(kind, name if args.far_only else f"{name}-far", height, parts, low, high, seed, folder)


def build_catalog(names, textures):
    """Every variant of each species, and each tree's far variant, from the catalog."""
    for species in names:
        base = KINDS[species]
        if textures:
            write_textures(species)
        for variant in base["variants"]:
            kind = merged(base, variant.get("set", {}))
            KINDS[species] = kind
            try:
                seed = variant["seed"]
                name = variant.get("name", variant_name(species, base, seed))
                folder = kind.get("folder", "trees")
                parts, low, high, scale, boxes = build(species, seed, kind["height"])
                write(kind, name, kind["height"], parts, low, high, seed, folder, boxes)
                if kind["role"] == "tree":
                    parts, low, high, _, _ = build(species, seed, kind["height"], far=True, scale=scale)
                    write(kind, f"{name}-far", kind["height"], parts, low, high, seed, folder)
            finally:
                KINDS[species] = base


# The contact sheet: a row per species, a cell per variant, labelled with its name and triangle count.
SHEET_LABEL = 22
SHEET_BACKGROUND = (40, 44, 48, 255)
SHEET_TEXT = (230, 230, 224, 255)
BLENDER = "/usr/local/bin/blender"


def contact_sheet(names, output):
    rows = []
    for species in names:
        kind = KINDS[species]
        folder = MODELS / kind.get("folder", "trees")
        rows.append([folder / f"{variant.get('name', variant_name(species, kind, variant['seed']))}.prop-mesh.json"
                     for variant in kind["variants"]])
    with tempfile.TemporaryDirectory() as renders:
        meshes = [str(mesh) for row in rows for mesh in row]
        subprocess.run([BLENDER, "-b", "--python", str(ROOT / "scripts" / "preview-prop-mesh.py"), "--", "--batch", renders, *meshes],
                       check=True, stdout=subprocess.DEVNULL)
        cell = Image.open(next(Path(renders).glob("*.png"))).size[0]
        columns = max(len(row) for row in rows)
        sheet = Image.new("RGBA", (cell * columns, (cell + SHEET_LABEL) * len(rows)), SHEET_BACKGROUND)
        draw = ImageDraw.Draw(sheet)
        for r, row in enumerate(rows):
            for c, mesh in enumerate(row):
                asset = json.loads(mesh.read_text())
                x, y = c * cell, r * (cell + SHEET_LABEL)
                sheet.paste(Image.open(Path(renders) / f"{asset['name']}.png").convert("RGBA"), (x, y + SHEET_LABEL))
                triangles = sum(len(part["indices"]) // 3 for part in asset["parts"])
                draw.text((x + 6, y + 5), f"{asset['name']}  {asset['height']:g} m  {triangles} tris", fill=SHEET_TEXT)
    sheet.save(output)
    print(f"generate-flora contact sheet {output}: {sum(len(row) for row in rows)} variants")


def main():
    args = arguments()
    names = args.species or list(KINDS)
    if args.single:
        build_one(args)
    elif not args.no_build:
        build_catalog(names, not args.no_textures)
    if args.sheet:
        contact_sheet(names, args.sheet)


def write(kind, name, height, parts, low, high, seed, folder, colliders=None):
    asset = {
        "name": name,
        "height": height,
        "bounds": {"min": [round(v, DECIMALS) for v in low], "max": [round(v, DECIMALS) for v in high]},
        "textures": {role: f"{TEXTURES}/{kind[role]}-{role}.png" for role in ("bark", "leaves")
                     if any(part["role"] == role for part in parts)},
        "parts": parts,
    }
    if colliders:
        asset["colliders"] = colliders
    output = MODELS / folder / f"{name}.prop-mesh.json"
    output.write_text(json.dumps(asset, separators=(",", ":")))
    counts = ", ".join(f"{p['role']} {len(p['indices']) // 3}" for p in parts)
    print(f"generate-flora {output.name}: {counts} triangles; seed {seed}")


if __name__ == "__main__":
    main()
