"""Generate a procedural tree as a CraftSurvive prop mesh with leaf cards (#9685).

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

Textures are drawn from the palette ramps (content/style/palette.json): KIND-bark.png and
KIND-leaves.png in content/game/textures/trees/, RGBA (the Engine admits only RGBA PNGs). They are
the same for every seed of a kind.

Output is the product prop-mesh JSON (Y up, metres; see PropMesh) with per-part UVs and the
texture each role samples:
    {"name", "height", "bounds", "textures": {"bark", "leaves"},
     "parts": [{"role", "positions", "normals", "uvs", "colors", "indices"}]}

    python3 scripts/generate-tree.py oak --seed 1 --name oak-1
"""
import argparse
import json
import math
import random
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
CONTENT = ROOT / "content" / "game"
MODELS = CONTENT / "models" / "trees"
TEXTURES = "textures/trees"
PALETTE = ROOT / "content" / "style" / "palette.json"
DECIMALS = 4
UP = np.array([0.0, 1.0, 0.0])
GOLDEN_ANGLE = math.pi * (3 - math.sqrt(5))

# Bark texture: TEXELS_AROUND texels around a trunk of about BARK_TILE_METRES circumference,
# BARK_TEXELS_UP texels up the tile's height (square texels on the trunk).
BARK_TEXELS_AROUND, BARK_TEXELS_UP, BARK_TILE_METRES = 32, 64, 1.0
# Leaf atlas: ATLAS_CELLS x ATLAS_CELLS clusters of LEAF_CELL pixels.
ATLAS_CELLS, LEAF_CELL = 2, 128

# Per kind: the trunk, then each branching level's children (count, where along the parent they
# start and end, angle from the parent's axis, length in metres before scaling, radius relative to
# the parent's at that point, sides, wobble, rise (+up / -droop) and segments), then the leaves
# (cards per twig, from how far along it, card size in metres, the crown-normal weight).
KINDS = {
    "oak": {
        "height": 9.0,
        "bark": "oak",
        "leaves": "oak",
        "trunk": {"length": 3.6, "radius": 0.38, "sides": 7, "segments": 3, "wobble": 0.08, "rise": 0.0,
                  "tip": 0.6, "flare": 1.4},
        "levels": [
            {"count": (5, 6), "start": 0.55, "end": 1.0, "angle": (35, 55), "length": (4.2, 5.2), "shrink": 0.1,
             "radius": 0.6, "sides": 6, "segments": 4, "wobble": 0.16, "rise": 0.1, "tip": 0.35, "even": True},
            {"count": (4, 6), "start": 0.25, "end": 1.0, "angle": (30, 60), "length": (1.8, 2.6), "shrink": 0.3,
             "radius": 0.55, "sides": 4, "segments": 3, "wobble": 0.3, "rise": 0.1, "tip": 0.3},
        ],
        "cards": {"per_twig": (6, 9), "from": 0.15, "size": (1.8, 2.6), "crown_normal": 0.75, "spread": 0.45,
                  "hang": 0.0},
    },
    "birch": {
        "height": 10.0,
        "bark": "birch",
        "leaves": "birch",
        "trunk": {"length": 9.6, "radius": 0.19, "sides": 7, "segments": 6, "wobble": 0.05, "rise": 0.0,
                  "tip": 0.2, "flare": 1.25},
        "levels": [
            {"count": (15, 19), "start": 0.3, "end": 0.98, "angle": (30, 55), "length": (2.2, 3.2), "shrink": 0.7,
             "radius": 0.45, "sides": 4, "segments": 3, "wobble": 0.22, "rise": -0.04, "tip": 0.3},
            {"count": (2, 4), "start": 0.3, "end": 1.0, "angle": (25, 50), "length": (0.9, 1.5), "shrink": 0.3,
             "radius": 0.6, "sides": 3, "segments": 2, "wobble": 0.3, "rise": -0.18, "tip": 0.4},
        ],
        "cards": {"per_twig": (4, 6), "from": 0.1, "size": (1.2, 1.7), "crown_normal": 0.7, "spread": 0.35,
                  "hang": 0.55},
    },
}

# Leaf cluster drawing per kind: the ramp, the lighter ramp the most lit leaves come from (those
# lit above "light_from"), leaf length and width in pixels, leaves per cluster, how many of the
# atlas's cells carry a few autumn-turned leaves, and the darkest point of the ramp a leaf uses
# (the crown's own shading and self-shadowing in the game darken it further).
LEAF_STYLES = {
    "oak": {"ramp": "foliage", "light": "meadow", "accent": "autumn", "length": (13, 18), "width": (8, 11),
            "leaves": (46, 60), "turned_cells": 1, "floor": 0.55, "light_from": 0.7},
    "birch": {"ramp": "meadow", "light": "meadow", "accent": "autumn", "length": (10, 14), "width": (6, 8),
              "leaves": (60, 78), "turned_cells": 1, "floor": 0.4, "light_from": 0.8},
}


def arguments():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("kind", choices=sorted(KINDS))
    parser.add_argument("--seed", type=int, default=1)
    parser.add_argument("--name", help="mesh name (default KIND-pSEED)")
    parser.add_argument("--height", type=float, help="metres to the crown's top (default the kind's)")
    parser.add_argument("--no-textures", action="store_true", help="leave the kind's textures as they are")
    return parser.parse_args()


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
    for _ in range(segments):
        d = normalise(d + random_unit(rng) * spec["wobble"] + UP * spec["rise"])
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


def leaf_cards(rng, kind, branches, crown_top):
    cards = kind["cards"]
    twigs = [b for b in branches if b.level == len(kind["levels"])]
    anchors = []
    for twig in twigs:
        for _ in range(rng.randint(*cards["per_twig"])):
            t = rng.uniform(cards["from"], 1.0)
            point, axis, _ = twig.at(t)
            anchors.append((point, axis))
    centre = np.mean([p for p, _ in anchors], axis=0)
    reach = max(np.linalg.norm((p - centre) * np.array([1, 0.8, 1])) for p, _ in anchors) or 1.0
    trunk_reach = max(math.hypot(p[0], p[2]) for p, _ in anchors) or 1.0
    positions, normals, uvs, colors, indices = [], [], [], [], []
    for point, axis in anchors:
        size = rng.uniform(*cards["size"])
        outward = normalise(point - centre)
        centre_of_card = point + random_unit(rng) * size * cards["spread"] * 0.5
        facing = normalise(random_unit(rng) + outward * 0.6)
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
        sway = 0.35 + 0.5 * min(1.0, math.hypot(centre_of_card[0], centre_of_card[2]) / trunk_reach)
        cell = rng.randrange(ATLAS_CELLS * ATLAS_CELLS)
        u0, v0 = (cell % ATLAS_CELLS) / ATLAS_CELLS, (cell // ATLAS_CELLS) / ATLAS_CELLS
        u1, v1 = u0 + 1 / ATLAS_CELLS, v0 + 1 / ATLAS_CELLS
        first = len(positions)
        half_a, half_b = across * size / 2, along * size / 2
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


def build(kind_name, seed, height):
    kind = KINDS[kind_name]
    rng = random.Random(f"{kind_name}-{seed}")
    trunk = kind["trunk"]
    branches = []
    lean = normalise(UP + random_unit(rng) * 0.06)
    grow(rng, kind, np.array([0.0, -0.4, 0.0]), lean, trunk["length"], trunk["radius"], 0, branches)
    crown_top = max(p[1] for b in branches for p in b.points)
    bark = {"positions": [], "normals": [], "uvs": [], "colors": [], "indices": []}
    uv_metres = BARK_TILE_METRES * BARK_TEXELS_UP / BARK_TEXELS_AROUND
    for branch in branches:
        p, n, uv, idx = tube(branch, uv_metres)
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
    lp, ln, luv, lc, li = leaf_cards(rng, kind, branches, crown_top)
    leaves = {"positions": lp, "normals": ln, "uvs": luv, "colors": lc, "indices": li}
    top = max(p[1] for p in bark["positions"] + leaves["positions"])
    scale = height / top
    low, high = [math.inf] * 3, [-math.inf] * 3
    parts = []
    for role, part in (("bark", bark), ("leaves", leaves)):
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
    return parts, low, high


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


def oak_bark(palette, rng):
    w, h = BARK_TEXELS_AROUND, BARK_TEXELS_UP
    ramp = palette["bark"]
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


BARKS = {"birch": birch_bark, "oak": oak_bark}


def leaf_polygon(cx, cy, length, width, angle):
    """A pointed leaf: an ellipse-like outline sharpened at both ends."""
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
            draw.polygon(leaf_polygon(x, y, length, width, angle), fill=tuple(int(v) for v in colour) + (255,))
            # A darker underside edge gives each leaf a readable shape inside the mass.
            shadow = tuple(int(v) for v in ramp_colour(ramp, max(0.0, shade - 0.3))) + (255,)
            ex, ey = x + math.cos(angle + math.pi / 2) * width * 0.3, y + math.sin(angle + math.pi / 2) * width * 0.3
            draw.line([(ex - math.cos(angle) * length * 0.3, ey - math.sin(angle) * length * 0.3),
                       (ex + math.cos(angle) * length * 0.3, ey + math.sin(angle) * length * 0.3)], fill=shadow, width=1)
        atlas.paste(layer, ((cell % ATLAS_CELLS) * LEAF_CELL, (cell // ATLAS_CELLS) * LEAF_CELL))
    return atlas


def write_textures(kind_name):
    kind = KINDS[kind_name]
    palette = ramps()
    folder = CONTENT / TEXTURES
    folder.mkdir(parents=True, exist_ok=True)
    bark = BARKS[kind["bark"]](palette, random.Random(f"bark-{kind['bark']}"))
    Image.fromarray(np.clip(bark, 0, 255).astype(np.uint8), "RGB").convert("RGBA").save(folder / f"{kind['bark']}-bark.png")
    leaf_atlas(palette, LEAF_STYLES[kind["leaves"]], random.Random(f"leaves-{kind['leaves']}")).save(
        folder / f"{kind['leaves']}-leaves.png")


def main():
    args = arguments()
    kind = KINDS[args.kind]
    name = args.name or f"{args.kind}-p{args.seed}"
    height = args.height or kind["height"]
    if not args.no_textures:
        write_textures(args.kind)
    parts, low, high = build(args.kind, args.seed, height)
    asset = {
        "name": name,
        "height": height,
        "bounds": {"min": [round(v, DECIMALS) for v in low], "max": [round(v, DECIMALS) for v in high]},
        "textures": {"bark": f"{TEXTURES}/{kind['bark']}-bark.png", "leaves": f"{TEXTURES}/{kind['leaves']}-leaves.png"},
        "parts": parts,
    }
    output = MODELS / f"{name}.prop-mesh.json"
    output.write_text(json.dumps(asset, separators=(",", ":")))
    counts = ", ".join(f"{p['role']} {len(p['indices']) // 3}" for p in parts)
    print(f"generate-tree {output.name}: {counts} triangles; seed {args.seed}")


if __name__ == "__main__":
    main()
