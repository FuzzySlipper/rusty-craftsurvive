#!/usr/bin/env python3
"""Builds the gulper's body from its sheet's parts, with no generator (#10987).

usage: python3 -I authoring/tools/creatures/make_gulper.py <species.json> <body.glb> [--paint <body-64.png>]

The gulper is almost all mouth: two long jaw rails hinged at the back, a cheek membrane stretched between them on each side,
a violet throat, a small body behind the hinge and a long ribbon tail. A generated mesh closes the jaws and fuses the
membranes, so the body is built here from the species' `gulper` block, in its rest pose half open, so the membranes
have their span and stretch or fold as the jaw opens and shuts (build_creature.py's `gape` weights: each point forward
of the hinge follows the upper and lower jaw by where it lies between them).

Every part is its own piece with its own place in one paint drawn here at the game's 64 texels/m: pearly rails with
lavender lips, pink-violet membranes ribbed like a fan from the hinge, a throat deep violet at the back, ivory needles,
a pearly body with two dark pits, and a tail pearly down its middle with violet edges. The upper rail's teeth and the
lower's are painted in their own blocks, so the build can give each set wholly to its jaw.

Points are the species' [side, forward, up] metres; the GLB is glTF's frame (x the left side, y up, z forward), so it
goes to build_creature.py --mesh as it is.
"""
import json
import math
import sys
from pathlib import Path

import numpy as np
from PIL import Image

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sheet_flyer import write_glb  # noqa: E402

TEXELS_PER_METRE = 64
ATLAS = (512, 256)
# Where each part's paint lies in the atlas, in texels: [x0, y0, x1, y1].
REGIONS = {
    "upper": (0, 0, 160, 32), "lower": (0, 32, 160, 64),
    "membrane.L": (0, 64, 128, 160), "membrane.R": (128, 64, 256, 160),
    "body": (160, 0, 256, 64), "throat": (256, 0, 320, 64), "tail": (256, 64, 400, 96),
    "teeth.upper": (400, 0, 408, 8), "teeth.lower": (408, 0, 416, 8),
}
PEARL = np.array((226, 220, 214)); PEARL_SHADE = np.array((188, 180, 182)); LAVENDER = np.array((176, 148, 204))
MEMBRANE = np.array((206, 170, 202)); MEMBRANE_RIB = np.array((150, 108, 162)); MEMBRANE_EDGE = np.array((122, 86, 150))
THROAT_DEEP = np.array((58, 32, 92)); THROAT_LIGHT = np.array((142, 110, 186))
TOOTH = np.array((238, 232, 222)); TOOTH_ROOT = np.array((204, 192, 180)); PIT = np.array((58, 44, 70))
RIM = np.array((122, 86, 162))
RAIL_RINGS, RAIL_SEGMENTS = 28, 12
MEMBRANE_ALONG, MEMBRANE_ACROSS = 26, 12
BODY_RINGS, BODY_SEGMENTS = 14, 16
TAIL_ALONG = 40
TOOTH_SIDES = 4
# The membranes and the tail are two faces this far apart, so both sides draw.
SKIN_METRES = 0.004
SEED = 10987


class Mesh:
    def __init__(self):
        self.positions, self.uvs, self.triangles = [], [], []

    def point(self, species_point, uv):
        s, f, u = species_point
        self.positions.append((s, u, f))
        self.uvs.append(uv)
        return len(self.positions) - 1

    def quad(self, a, b, c, d, flip=False):
        """a b on one edge, c d on the next, wound so the face looks out along (b - a) x (c - a) unless flipped."""
        tris = [(a, b, c), (b, d, c)]
        self.triangles += [(x, z, y) for x, y, z in tris] if flip else tris

    def grid(self, rows, flip=False, wrap=False):
        for i in range(len(rows) - 1):
            row, nxt = rows[i], rows[i + 1]
            count = len(row) if wrap else len(row) - 1
            for j in range(count):
                k = (j + 1) % len(row)
                self.quad(row[j], row[k], nxt[j], nxt[k], flip)


def uv_in(region, x, y):
    """A [0, 1] place within a region of the atlas, as glTF uv."""
    x0, y0, x1, y1 = REGIONS[region]
    return ((x0 + (x1 - x0) * min(max(x, 0.0), 1.0)) / ATLAS[0], (y0 + (y1 - y0) * min(max(y, 0.0), 1.0)) / ATLAS[1])


def unit(v):
    v = np.asarray(v, dtype=float)
    return v / np.linalg.norm(v)


def rails(spec):
    hinge = np.array(spec["hinge"], dtype=float)
    half = math.radians(spec["restOpenDegrees"] / 2)
    length = spec["jawMetres"]
    out = {}
    for name, sign in (("upper", 1.0), ("lower", -1.0)):
        along = unit((0.0, math.cos(half), sign * math.sin(half)))
        inward = unit((0.0, math.sin(half), -sign * math.cos(half)))  # toward the other rail, across the gape
        out[name] = (hinge, along, inward, length)
    return out


def rail_size(spec, t):
    w0, w1 = spec["railHalfWidth"]
    d0, d1 = spec["railHalfDepth"]
    return w0 + (w1 - w0) * t, d0 + (d1 - d0) * t


def build_rail(mesh, spec, name, rail):
    hinge, along, inward, length = rail
    side = np.array((1.0, 0.0, 0.0))
    rows = []
    for i in range(RAIL_RINGS + 1):
        t = i / RAIL_RINGS
        centre = hinge + along * (t * length)
        w, d = rail_size(spec, t)
        row = []
        for j in range(RAIL_SEGMENTS):
            phi = 2 * math.pi * j / RAIL_SEGMENTS
            p = centre + side * (w * math.cos(phi)) - inward * (d * math.sin(phi))
            row.append(mesh.point(p, uv_in(name, t, j / RAIL_SEGMENTS)))
        rows.append(row)
    flip = name == "lower"
    mesh.grid(rows, flip=flip, wrap=True)
    for end, t in ((rows[0], 0.0), (rows[-1], 1.0)):
        cap = mesh.point(hinge + along * (t * length), uv_in(name, t, 0.5))
        for j in range(RAIL_SEGMENTS):
            a, b = end[j], end[(j + 1) % RAIL_SEGMENTS]
            tri = (cap, b, a) if (t == 0.0) != flip else (cap, a, b)
            mesh.triangles.append(tri)


def edge_point(spec, rail, t, side_sign):
    hinge, along, inward, length = rail
    w, d = rail_size(spec, t)
    return hinge + along * (t * length) + inward * (0.7 * d) + np.array((side_sign * 0.6 * w, 0.0, 0.0))


def build_membranes(mesh, spec, rs):
    reach = spec["membraneShare"]
    bulge = spec["membraneBulgeMetres"]
    for side_sign, region in ((1.0, "membrane.L"), (-1.0, "membrane.R")):
        for face in (1.0, -1.0):
            rows = []
            for i in range(MEMBRANE_ALONG + 1):
                a = i / MEMBRANE_ALONG
                row = []
                for j in range(MEMBRANE_ACROSS + 1):
                    s = j / MEMBRANE_ACROSS
                    # The free front edge scallops back in the middle, between the rails.
                    t = 0.02 + (reach - 0.02) * a * (1 - 0.3 * math.sin(math.pi * s) * a)
                    up, low = edge_point(spec, rs["upper"], t, side_sign), edge_point(spec, rs["lower"], t, side_sign)
                    swell = bulge * math.sin(math.pi * s) * math.sin(math.pi * min(1.0, t / reach)) ** 0.6
                    p = up + (low - up) * s + np.array((side_sign * (swell + face * SKIN_METRES), 0.0, 0.0))
                    row.append(mesh.point(p, uv_in(region, a, s)))
                rows.append(row)
            mesh.grid(rows, flip=(side_sign * face) < 0)


def build_throat(mesh, spec, rs):
    """A funnel from a ring across the back of the gape into the body, its inside facing the mouth."""
    t = spec["throatShare"]
    hinge = np.array(spec["hinge"], dtype=float)
    apex = hinge + np.array((0.0, -spec["throatDepthMetres"], 0.0))
    up, low = rs["upper"], rs["lower"]
    top = up[0] + up[1] * (t * up[3]) + up[2] * rail_size(spec, t)[1]
    bottom = low[0] + low[1] * (t * low[3]) + low[2] * rail_size(spec, t)[1]
    centre = (top + bottom) / 2
    half_up = np.linalg.norm(top - bottom) / 2
    half_side = spec["membraneBulgeMetres"] * 0.8 + rail_size(spec, t)[0]
    segments = 16
    ring = []
    for j in range(segments):
        phi = 2 * math.pi * j / segments
        p = centre + np.array((half_side * math.cos(phi), 0.0, half_up * math.sin(phi)))
        ring.append(mesh.point(p, uv_in("throat", j / segments, 1.0)))
    for face in (False, True):
        tip = mesh.point(apex, uv_in("throat", 0.5, 0.0))
        for j in range(segments):
            a, b = ring[j], ring[(j + 1) % segments]
            mesh.triangles.append((tip, a, b) if face else (tip, b, a))


def build_body(mesh, spec):
    centre = np.array(spec["bodyCentre"], dtype=float)
    rs, rf, ru = spec["bodyRadii"]
    rows = []
    for i in range(1, BODY_RINGS):
        theta = math.pi * i / BODY_RINGS
        row = []
        for j in range(BODY_SEGMENTS):
            phi = 2 * math.pi * j / BODY_SEGMENTS
            p = centre + np.array((rs * math.sin(theta) * math.cos(phi), rf * math.cos(theta), ru * math.sin(theta) * math.sin(phi)))
            row.append(mesh.point(p, uv_in("body", i / BODY_RINGS, j / BODY_SEGMENTS)))
        rows.append(row)
    mesh.grid(rows, wrap=True, flip=True)
    for end, f in ((rows[0], rf), (rows[-1], -rf)):
        pole = mesh.point(centre + np.array((0.0, f, 0.0)), uv_in("body", 0.0 if f > 0 else 1.0, 0.5))
        for j in range(BODY_SEGMENTS):
            a, b = end[j], end[(j + 1) % BODY_SEGMENTS]
            mesh.triangles.append((pole, a, b) if f > 0 else (pole, b, a))


def tail_points(spec):
    start = np.array(spec["tailStart"], dtype=float)
    return [start + np.array((0.0, -spec["tailMetres"] * k / spec["tailBones"], 0.0)) for k in range(spec["tailBones"] + 1)]


def build_tail(mesh, spec):
    start = np.array(spec["tailStart"], dtype=float)
    h0, h1 = spec["tailHalfHeight"]
    for face in (1.0, -1.0):
        rows = []
        for i in range(TAIL_ALONG + 1):
            a = i / TAIL_ALONG
            half = h0 + (h1 - h0) * a
            c = start + np.array((face * SKIN_METRES * 1.5, -spec["tailMetres"] * a, 0.0))
            rows.append([mesh.point(c + np.array((0.0, 0.0, half * (1 - 2 * k))), uv_in("tail", a, k)) for k in (0, 1)])
        mesh.grid(rows, flip=face < 0)


def build_teeth(mesh, spec, rs):
    rng = np.random.default_rng(SEED)
    for name in ("upper", "lower"):
        hinge, along, inward, length = rs[name]
        count = spec["teethPerRail"]
        side = np.array((1.0, 0.0, 0.0))
        for k in range(count):
            t = 0.08 + 0.9 * (k + rng.uniform(-0.25, 0.25)) / count
            w, d = rail_size(spec, t)
            base = hinge + along * (t * length) + inward * (0.8 * d) + side * rng.uniform(-0.3, 0.3) * w
            long_one = (k % 4 == 1 and t < 0.75) or (k == count - 2)
            size = rng.uniform(0.26, 0.42) if long_one else rng.uniform(0.07, 0.15)
            direction = unit(inward * math.cos(math.radians(12)) + along * math.sin(math.radians(rng.uniform(-6, 18))))
            tip = base + direction * size
            radius = 0.01 + 0.025 * size
            u = unit(np.cross(direction, side))
            ring = []
            for m in range(TOOTH_SIDES):
                phi = 2 * math.pi * m / TOOTH_SIDES
                p = base + side * (radius * math.cos(phi)) + u * (radius * math.sin(phi)) - direction * 0.01
                ring.append(mesh.point(p, uv_in(f"teeth.{name}", 0.5, 0.75)))
            top = mesh.point(tip, uv_in(f"teeth.{name}", 0.5, 0.25))
            for m in range(TOOTH_SIDES):
                mesh.triangles.append((top, ring[m], ring[(m + 1) % TOOTH_SIDES]))


def paint(spec):
    rng = np.random.default_rng(SEED)
    image = np.zeros((ATLAS[1], ATLAS[0], 3))

    def fill(region, colour_of):
        x0, y0, x1, y1 = REGIONS[region]
        for y in range(y0, y1):
            for x in range(x0, x1):
                image[y, x] = colour_of((x - x0 + 0.5) / (x1 - x0), (y - y0 + 0.5) / (y1 - y0))

    def grain():
        return rng.uniform(-6, 6)

    def rail(t, v):
        # v round the rail: 0 its outer side, 0.25 the top/outside, 0.5 the inner (the gape side: its lip), 0.75 underneath.
        lip = max(0.0, math.cos(2 * math.pi * (v - 0.5))) ** 3
        shade = 0.5 + 0.5 * math.sin(2 * math.pi * v)
        colour = PEARL * (0.88 + 0.12 * shade) + (LAVENDER - PEARL) * lip * 0.8
        return colour + grain()

    fill("upper", rail)
    fill("lower", rail)

    def membrane(a, s):
        ribs = 7
        rib = abs((s * ribs) % 1.0 - 0.5) < 0.09
        deep = max(0.0, 1.0 - a * 3.0)
        colour = MEMBRANE + (THROAT_LIGHT - MEMBRANE) * deep * 0.6
        if rib:
            colour = MEMBRANE_RIB
        if a > 0.94:
            colour = MEMBRANE_EDGE
        return colour + grain() * 0.6

    fill("membrane.L", membrane)
    fill("membrane.R", membrane)
    fill("throat", lambda u, v: THROAT_DEEP + (THROAT_LIGHT - THROAT_DEEP) * v ** 1.6 + grain() * 0.4)

    def body(a, phi):
        underneath = max(0.0, -math.sin(2 * math.pi * phi))
        colour = PEARL * (0.9 + 0.1 * (1 - underneath)) + (LAVENDER - PEARL) * underneath * 0.5
        for pit_phi in (0.15, 0.35):
            if abs(a - 0.22) < 0.05 and abs(phi - pit_phi) < 0.025:
                colour = PIT
        return colour + grain()

    fill("body", body)

    def tail(a, k):
        edge = abs(k - 0.5) * 2
        colour = PEARL + (LAVENDER - PEARL) * edge ** 2
        if edge > 0.82:
            colour = RIM
        return colour + grain() * 0.6

    fill("tail", tail)
    for name in ("teeth.upper", "teeth.lower"):
        fill(name, lambda u, v: TOOTH if v < 0.5 else TOOTH_ROOT)
    return Image.fromarray(np.clip(image, 0, 255).astype(np.uint8))


def main():
    argv = sys.argv[1:]
    species = json.loads(Path(argv[0]).read_text())
    spec = species["gulper"]
    mesh = Mesh()
    rs = rails(spec)
    for name in ("upper", "lower"):
        build_rail(mesh, spec, name, rs[name])
    build_membranes(mesh, spec, rs)
    build_throat(mesh, spec, rs)
    build_body(mesh, spec)
    build_tail(mesh, spec)
    build_teeth(mesh, spec, rs)
    image = paint(spec)
    positions = np.array(mesh.positions, np.float32)
    uvs = np.array(mesh.uvs, np.float32)
    triangles = np.array(mesh.triangles, np.uint32)
    write_glb(argv[1], positions, uvs, triangles, image)
    if "--paint" in argv:
        image.save(argv[argv.index("--paint") + 1])
    lo, hi = positions.min(axis=0), positions.max(axis=0)
    print(f"{len(positions)} vertices, {len(triangles)} triangles; side {lo[0]:.2f}..{hi[0]:.2f}, up {lo[1]:.2f}..{hi[1]:.2f}, "
          f"forward {lo[2]:.2f}..{hi[2]:.2f}; paint {ATLAS[0]}x{ATLAS[1]}")


if __name__ == "__main__":
    main()
