#!/usr/bin/env python3
"""Builds a ruin fixture's body from its species' `fixture` block, with no generator (#10963).

usage: python3 -I authoring/tools/creatures/make_fixture.py <species.json> <body.glb> <body.weights.json> \
           [--paint <body-64.png>] [--emission <emission.png>]

Fixtures are the ruin's living infrastructure (den-wiki wandering-years/ruins-wildlife, "Fixtures as hazards"): rooted
organisms whose few moving parts do all their acting. A generator fuses such parts (a gulper's membranes, a bower's
ribs and curtains), so each body is built here from parts: tubes along paths, surfaces of revolution and two-sided
sheets. Every vertex is given its bones as it is made, written to <body.weights.json> as [side, forward, up, bone,
weight, ...]: build_creature.py sets the skin from it (`weigh_from_file`) rather than solving heat weights.

- `unfolder`: an oval shell seated in a recess, with shingled back plates, an opening down its front over plum
  tissue and two amber organs, six short braced legs, and two long limbs ending in pincers.
- `filter`: a root spread on the ceiling, three stalks, and on each a plated lobe with a jade collar and a pleated
  amber fringe. A stalk's points share root and lobe by how far along it they lie, so it stretches as the lobe drops.
- `bower`: six ribs rooted in a ring and meeting at a crown, an amber membrane between each pair down to an arch,
  and below each arch a curtain that lies collapsed along the arch while it is open and drops to the floor to close.
- `vessel`: four amber sacs in pod frames on rooted stems, linked by plum tissue. Each sac is two halves hinged at
  its back, so it can split open down its front.

Each part has its own block of one paint drawn here at the game's 64 texels/m, with an emission image for the parts
that glow (the amber organs, collars and sacs). Points are the species' [side, forward, up] metres; the GLB is glTF's
frame (x the left side, y up, z forward), so it goes to build_creature.py --mesh as it is.
"""
import io
import json
import math
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

TEXELS_PER_METRE = 64
ATLAS_WIDTH = 512
SMALLEST_PATCH = 4
# A sheet is drawn as two faces this far apart, so both sides draw.
SHEET_METRES = 0.008
SEED = 10963

PALETTE = {
    "ivory": (222, 208, 180), "ivory_shade": (192, 176, 150), "lavender": (186, 172, 190),
    "line": (98, 78, 66), "jade": (118, 160, 128), "jade_dark": (78, 116, 92), "jade_light": (160, 196, 160),
    "amber": (226, 146, 50), "amber_deep": (168, 88, 28), "amber_light": (252, 198, 96),
    "plum": (146, 76, 98), "plum_dark": (104, 50, 72), "plum_light": (180, 112, 128),
    "pink": (222, 178, 166), "pink_dark": (170, 116, 112), "root": (214, 182, 172), "root_dark": (170, 130, 126),
    "membrane": (214, 156, 66), "vein": (156, 98, 40), "hollow": (70, 52, 50),
}


def colour(name):
    return np.array(PALETTE[name], dtype=float)


def unit(v):
    v = np.asarray(v, dtype=float)
    n = np.linalg.norm(v)
    return v / n if n > 1e-12 else v


def smooth(x):
    x = min(max(x, 0.0), 1.0)
    return x * x * (3 - 2 * x)


UP = np.array((0.0, 0.0, 1.0))


# ---------------------------------------------------------------- the body being built

class Patch:
    def __init__(self, width, height, material, params):
        self.width, self.height = width, height
        self.material, self.params = material, params
        self.x = self.y = 0


class Body:
    def __init__(self):
        self.positions, self.uvs, self.weights, self.triangles = [], [], [], []
        self.patches, self.shared = [], {}

    def patch(self, width_metres, height_metres, material, key=None, **params):
        """A block of the paint for one part, sized for 64 texels a metre; parts with the same key share one."""
        if key is not None and key in self.shared:
            return self.shared[key]
        width = max(SMALLEST_PATCH, int(math.ceil(width_metres * TEXELS_PER_METRE)))
        height = max(SMALLEST_PATCH, int(math.ceil(height_metres * TEXELS_PER_METRE)))
        if width > ATLAS_WIDTH:
            raise SystemExit(f"a {material} part is {width} texels across, wider than the atlas")
        index = len(self.patches)
        self.patches.append(Patch(width, height, material, params))
        if key is not None:
            self.shared[key] = index
        return index

    def vertex(self, point, patch, u, v, weights):
        self.positions.append(np.asarray(point, dtype=float))
        self.uvs.append((patch, min(max(u, 0.0), 1.0), min(max(v, 0.0), 1.0)))
        total = sum(weights.values())
        self.weights.append({bone: w / total for bone, w in weights.items() if w > 1e-4})
        return len(self.positions) - 1

    def triangle(self, a, b, c, outward):
        """One face, wound so it looks along `outward` (species metres) once the points are in glTF's frame."""
        pa, pb, pc = (self.gltf(self.positions[i]) for i in (a, b, c))
        normal = np.cross(pb - pa, pc - pa)
        if np.dot(normal, self.gltf(outward)) < 0:
            b, c = c, b
        self.triangles.append((a, b, c))

    @staticmethod
    def gltf(point):
        s, f, u = point
        return np.array((s, u, f))

    # ------------------------------------------------ parts

    def tube(self, path, radii, segments, material, weights, key=None, caps=(True, True), closed=False,
             reference=None, outward_of=None, **params):
        """A tube along `path`, its section an ellipse of `radii` [(across, other), ...] (a number for a circle) about
        a frame carried along the path from `reference`. `weights(i, t, point)` gives each point its bones; t runs 0 to
        1 along the path. With `closed` the path is a loop."""
        path = [np.asarray(p, dtype=float) for p in path]
        radii = [r if isinstance(r, (tuple, list)) else (r, r) for r in radii]
        count = len(path)
        tangents = []
        for i in range(count):
            if closed:
                a, b = path[i - 1], path[(i + 1) % count]
            else:
                a, b = path[max(i - 1, 0)], path[min(i + 1, count - 1)]
            tangents.append(unit(b - a))
        across = unit(np.cross(tangents[0], reference if reference is not None else
                               (UP if abs(np.dot(tangents[0], UP)) < 0.9 else np.array((1.0, 0.0, 0.0)))))
        frames = []
        for i in range(count):
            across = unit(across - tangents[i] * np.dot(across, tangents[i]))
            frames.append((across, np.cross(tangents[i], across)))
        lengths = [0.0]
        for i in range(1, count):
            lengths.append(lengths[-1] + np.linalg.norm(path[i] - path[i - 1]))
        if closed:
            lengths.append(lengths[-1] + np.linalg.norm(path[0] - path[-1]))
        total = max(lengths[-1], 1e-6)
        around = 2 * math.pi * float(np.mean([0.5 * (a + b) for a, b in radii]))
        patch = self.patch(around, total, material, key, **params)
        rows = []
        stations = list(range(count)) + ([0] if closed else [])
        for row, i in enumerate(stations):
            t = lengths[row] / total
            weight = weights(i, t, path[i])
            ring = []
            for j in range(segments + 1):
                phi = 2 * math.pi * j / segments
                a, b = frames[i]
                ra, rb = radii[i]
                p = path[i] + a * (ra * math.cos(phi)) + b * (rb * math.sin(phi))
                ring.append(self.vertex(p, patch, j / segments, t, weight))
            rows.append(ring)
        for r in range(len(rows) - 1):
            i = stations[r]
            for j in range(segments):
                corners = (rows[r][j], rows[r][j + 1], rows[r + 1][j], rows[r + 1][j + 1])
                middle = sum(self.positions[k] for k in corners) / 4
                centre = 0.5 * (path[i] + path[stations[r + 1]])
                outward = middle - centre if outward_of is None else outward_of(middle)
                self.triangle(corners[0], corners[1], corners[2], outward)
                self.triangle(corners[1], corners[3], corners[2], outward)
        if not closed:
            for end, row, i, sign in ((caps[0], rows[0], 0, -1.0), (caps[1], rows[-1], count - 1, 1.0)):
                if not end:
                    continue
                t = 0.0 if i == 0 else 1.0
                centre = self.vertex(path[i] + tangents[i] * 0.0, patch, 0.5, t, weights(i, t, path[i]))
                for j in range(segments):
                    self.triangle(centre, row[j], row[j + 1], tangents[i] * sign)
        return patch

    def lathe(self, centre, axis, profile, radii, segments, material, weights, key=None, reference=None, **params):
        """A closed surface of revolution: `profile` gives the radius share at each step from one pole (0) to the
        other (1), scaled by `radii` (across, other) and laid along `axis` (its length is the body's)."""
        centre, axis = np.asarray(centre, dtype=float), np.asarray(axis, dtype=float)
        path = [centre + axis * (k - 0.5) for k, _ in profile]
        rs = [(max(share * radii[0], 1e-3), max(share * radii[1], 1e-3)) for _, share in profile]
        return self.tube(path, rs, segments, material, weights, key=key, reference=reference, **params)

    def sheet(self, grid, material, weights, key=None, thickness=SHEET_METRES, wrap=False, normal_hint=None, **params):
        """A two-sided sheet through `grid` [rows][columns] of points; `weights(r, c, point)` gives each its bones. Its
        width in the paint is its widest row, its height its longest column."""
        grid = np.asarray(grid, dtype=float)
        rows, columns = grid.shape[0], grid.shape[1]
        width = max(sum(np.linalg.norm(grid[r][c + 1] - grid[r][c]) for c in range(columns - 1))
                    + (np.linalg.norm(grid[r][0] - grid[r][-1]) if wrap else 0.0) for r in range(rows))
        height = max(sum(np.linalg.norm(grid[r + 1][c] - grid[r][c]) for r in range(rows - 1)) for c in range(columns))
        patch = self.patch(width, height, material, key, **params)
        normals = np.zeros_like(grid)
        for r in range(rows):
            for c in range(columns):
                if wrap:
                    du = grid[r][(c + 1) % columns] - grid[r][c - 1]
                else:
                    du = grid[r][min(c + 1, columns - 1)] - grid[r][max(c - 1, 0)]
                dv = grid[min(r + 1, rows - 1)][c] - grid[max(r - 1, 0)][c]
                n = unit(np.cross(du, dv))
                if normal_hint is not None and np.dot(n, normal_hint(grid[r][c])) < 0:
                    n = -n
                normals[r][c] = n
        span = columns if wrap else columns - 1
        for side in (1.0, -1.0):
            ids = [[self.vertex(grid[r][c] + normals[r][c] * (side * thickness / 2), patch,
                                (c / span) if span else 0.5, r / max(rows - 1, 1), weights(r, c, grid[r][c]))
                    for c in range(columns)]
                   for r in range(rows)]
            if wrap:
                # The seam's last column repeats the first at the far edge of the paint.
                for r in range(rows):
                    point = grid[r][0] + normals[r][0] * (side * thickness / 2)
                    ids[r].append(self.vertex(point, patch, 1.0, r / max(rows - 1, 1), weights(r, 0, grid[r][0])))
            for r in range(rows - 1):
                for c in range(span):
                    c1 = c + 1
                    cn = c1 % columns
                    out = (normals[r][c] + normals[r][cn] + normals[r + 1][c] + normals[r + 1][cn]) * side
                    a, b, d, e = ids[r][c], ids[r][c1], ids[r + 1][c], ids[r + 1][c1]
                    self.triangle(a, b, d, out)
                    self.triangle(b, e, d, out)
        return patch


def bones(**weights):
    return lambda *_: weights


# ---------------------------------------------------------------- paint

class Painter:
    def __init__(self, seed):
        self.rng = np.random.default_rng(seed)

    def noise(self, width, height, cell):
        """Smooth value noise in [-1, 1] at `cell` texels, for calm mottling rather than grain."""
        gw, gh = width // cell + 2, height // cell + 2
        grid = self.rng.uniform(-1, 1, (gh, gw))
        ys, xs = np.mgrid[0:height, 0:width] / cell
        x0, y0 = xs.astype(int), ys.astype(int)
        fx, fy = xs - x0, ys - y0
        fx, fy = fx * fx * (3 - 2 * fx), fy * fy * (3 - 2 * fy)
        top = grid[y0, x0] * (1 - fx) + grid[y0, x0 + 1] * fx
        bottom = grid[y0 + 1, x0] * (1 - fx) + grid[y0 + 1, x0 + 1] * fx
        return top * (1 - fy) + bottom * fy

    def paint(self, patch):
        """The patch's colours and glow (0 to 1) as [height][width] arrays; x runs across (u), y along (v)."""
        w, h = patch.width, patch.height
        image = np.zeros((h, w, 3))
        glow = np.zeros((h, w))
        getattr(self, patch.material)(image, glow, w, h, **patch.params)
        return image, glow

    def marks(self, image, count, colour_name, w, h, size=(2, 1), rows=None):
        """A few small authored marks: short pairs of texels, never a tiled pattern."""
        for _ in range(count):
            x = int(self.rng.integers(0, max(w - size[0], 1)))
            y0, y1 = rows if rows else (0, h)
            y = int(self.rng.integers(y0, max(y1 - size[1], y0 + 1)))
            image[y:y + size[1], x:x + size[0]] = colour(colour_name)

    def shell(self, image, glow, w, h, tint="ivory", rims=(), marks=0, jade_rows=(), quilt=0, organs=()):
        base = colour(tint)
        shade = self.noise(w, h, 6)
        image[:] = base + shade[..., None] * 9
        # A soft shading round the part, darker underneath its middle.
        ys = (np.arange(h) + 0.5) / h
        image *= (0.94 + 0.06 * np.cos(math.pi * (ys - 0.4)))[:, None, None]
        if quilt:
            cells = self.rng.uniform(0, 1, (quilt, 2)) * (w, h)
            xs, yy = np.meshgrid(np.arange(w) + 0.5, np.arange(h) + 0.5)
            distance = np.full((h, w), np.inf)
            second = np.full((h, w), np.inf)
            for cx, cy in cells:
                for ox in (-w, 0, w):
                    d = np.hypot(xs - cx - ox, (yy - cy) * 1.4)
                    second = np.where(d < distance, distance, np.minimum(second, d))
                    distance = np.minimum(distance, d)
            image[(second - distance) < 1.1] = colour("ivory_shade") * 0.85
        for row in rims:
            y = int(row * (h - 1))
            image[max(y - 1, 0):y + 1] = colour("line")
        for row in jade_rows:
            y = int(row * (h - 1))
            image[max(y - 1, 0):y + 2] = colour("jade")
            image[y] = colour("jade_dark")
        self.marks(image, marks, "jade", w, h, (2, 2))
        for (ox, oy, rx, ry) in organs:
            xs, yy = np.meshgrid(np.arange(w) + 0.5, np.arange(h) + 0.5)
            d = np.hypot((xs - ox * w) / max(rx * w, 1), (yy - oy * h) / max(ry * h, 1))
            image[d < 1.35] = colour("jade")
            image[(d >= 1.15) & (d < 1.35)] = colour("jade_dark")
            image[d < 1.0] = colour("amber")
            image[d < 0.55] = colour("amber_light")
            glow[d < 1.0] = 0.8

    def casing(self, image, glow, w, h, bands=(), insets=()):
        """A limb's pale casing with jade bands round it and amber insets set into it."""
        self.shell(image, glow, w, h)
        for row in bands:
            y = int(row * (h - 1))
            image[max(y - 1, 0):y + 2] = colour("jade")
            image[min(y + 2, h - 1)] = colour("jade_dark")
        for row in insets:
            y = int(row * (h - 1))
            x = w // 4
            image[max(y - 2, 0):y + 3, x - 1:x + 2] = colour("line")
            image[max(y - 1, 0):y + 2, x:x + 1] = colour("amber")
            glow[max(y - 1, 0):y + 2, x:x + 1] = 0.7

    def pincer(self, image, glow, w, h):
        """A finger: pale outside, its inner pad (the middle of the paint across) soft jade."""
        self.shell(image, glow, w, h)
        xs = (np.arange(w) + 0.5) / w
        pad = np.abs(xs - 0.75) < 0.16
        image[:, pad] = colour("jade") + self.noise(w, h, 3)[:, pad][..., None] * 8
        edge = np.abs(np.abs(xs - 0.75) - 0.16) < 0.03
        image[:, edge] = colour("jade_dark")

    def jade_band(self, image, glow, w, h, glowing=0.0):
        image[:] = colour("jade") + self.noise(w, h, 4)[..., None] * 8
        xs = (np.arange(w) + 0.5) / w
        image[:, np.abs(xs - 0.25) < 0.05] = colour("jade_dark")
        if glowing:
            band = np.abs(xs - 0.75) < 0.09
            image[:, band] = colour("amber_light")
            glow[:, band] = glowing
        # A few authored notches along the band.
        for x in self.rng.integers(0, w, 3):
            image[::max(h // 6, 2), x] = colour("jade_dark")

    def organ(self, image, glow, w, h, veins=3):
        ys = (np.arange(h) + 0.5) / h
        depth = np.sin(math.pi * ys) ** 0.6
        image[:] = colour("amber_deep") + (colour("amber") - colour("amber_deep")) * depth[:, None, None]
        xs = (np.arange(w) + 0.5) / w
        image[:, np.abs(xs - 0.75) < 0.12] += (colour("amber_light") - colour("amber")) * 0.8
        for k in range(veins):
            phase = self.rng.uniform(0, 1)
            for y in range(h):
                x = int(((phase + 0.08 * math.sin(6 * ys[y] + k)) % 1.0) * w)
                image[y, x] = colour("amber_deep") * 0.8
        glow[:] = 0.75

    def tissue(self, image, glow, w, h, ribs=5):
        image[:] = colour("plum") + self.noise(w, h, 5)[..., None] * 10
        ys = np.arange(h)
        for y in ys[(ys % ribs) == 0]:
            image[y] = colour("plum_dark")
        for y in ys[(ys % ribs) == 1]:
            image[y] = colour("plum_light")

    def frill(self, image, glow, w, h):
        image[:] = colour("pink") + self.noise(w, h, 4)[..., None] * 8
        for x in range(0, w, 3):
            image[:, x] = colour("pink_dark")

    def root(self, image, glow, w, h):
        image[:] = colour("root") + self.noise(w, h, 5)[..., None] * 9
        for _ in range(max(w // 6, 2)):
            x = int(self.rng.integers(0, w))
            y0 = int(self.rng.integers(0, max(h // 2, 1)))
            image[y0:y0 + int(self.rng.integers(h // 4 + 1, h // 2 + 2)), x] = colour("root_dark")

    def membrane(self, image, glow, w, h, veins=5, edged=True, pleats=0, glowing=0.12):
        image[:] = colour("membrane") + self.noise(w, h, 7)[..., None] * 10
        xs = (np.arange(w) + 0.5) / w
        ys = (np.arange(h) + 0.5) / h
        # Lighter towards the middle of its span, as light comes through it.
        image += (np.sin(math.pi * xs)[None, :, None] * np.sin(math.pi * ys)[:, None, None]) * 14
        if pleats:
            for k in range(pleats):
                x = int((k + 0.5) / pleats * w)
                image[:, x] = colour("vein")
        else:
            mid = w // 2
            image[:, mid] = colour("vein")
            step = max(h // max(veins, 1), 4)
            for k, y0 in enumerate(range(step // 2, h, step)):
                for side in (-1, 1):
                    for d in range(w // 2 - 2):
                        y = y0 + int(d * 0.7)
                        x = mid + side * d
                        if 0 <= y < h and 0 <= x < w:
                            image[y, x] = colour("vein")
        if edged:
            image[:, :2] = colour("jade")
            image[:, -2:] = colour("jade")
            image[:, 0] = image[:, -1] = colour("line")
        glow[:] = glowing

    def sac(self, image, glow, w, h, coils=2.0):
        """An amber sac with a dark coiled shape faintly inside, on its front half."""
        xs = (np.arange(w) + 0.5) / w
        ys = (np.arange(h) + 0.5) / h
        depth = np.sin(math.pi * ys) ** 0.5
        image[:] = colour("amber_deep") + (colour("amber_light") - colour("amber_deep")) * (0.35 + 0.5 * depth)[:, None, None]
        gx, gy = np.meshgrid(xs, ys)
        coil = np.abs(((gx * 3.0 + gy * coils) % 1.0) - 0.5) < 0.08
        inside = (gy > 0.2) & (gy < 0.85)
        image[coil & inside] = image[coil & inside] * 0.72
        glow[:] = 0.6 * depth[:, None]

    def hollow(self, image, glow, w, h):
        image[:] = colour("hollow") + self.noise(w, h, 4)[..., None] * 6


def pack(body):
    """Shelf-packs the patches into one atlas, ATLAS_WIDTH wide and a power of two tall."""
    order = sorted(range(len(body.patches)), key=lambda k: -body.patches[k].height)
    x = y = shelf = 0
    for k in order:
        p = body.patches[k]
        if x + p.width > ATLAS_WIDTH:
            x, y, shelf = 0, y + shelf, 0
        p.x, p.y = x, y
        x += p.width
        shelf = max(shelf, p.height)
    used = y + shelf
    height = 1 << max(int(math.ceil(math.log2(max(used, 1)))), 4)
    return ATLAS_WIDTH, height


def paint_atlas(body, size):
    painter = Painter(SEED)
    image = np.zeros((size[1], size[0], 3))
    glow = np.zeros((size[1], size[0]))
    for p in body.patches:
        colours, shine = painter.paint(p)
        image[p.y:p.y + p.height, p.x:p.x + p.width] = colours
        glow[p.y:p.y + p.height, p.x:p.x + p.width] = shine
    paint = Image.fromarray(np.clip(image, 0, 255).astype(np.uint8))
    emission = Image.fromarray(np.clip(image * glow[..., None], 0, 255).astype(np.uint8))
    return paint, emission


def atlas_uvs(body, size):
    out = []
    for patch, u, v in body.uvs:
        p = body.patches[patch]
        # Inset half a texel, so a nearest lookup at an edge stays in its own block.
        x = p.x + 0.5 + u * (p.width - 1)
        y = p.y + 0.5 + v * (p.height - 1)
        out.append((x / size[0], y / size[1]))
    return np.array(out, np.float32)


def write_glb(path, positions, uvs, triangles, atlas):
    png = io.BytesIO()
    atlas.save(png, "PNG")
    blobs = [positions.tobytes(), uvs.tobytes(), triangles.tobytes(), png.getvalue()]
    views, offset = [], 0
    for blob in blobs:
        views.append({"buffer": 0, "byteOffset": offset, "byteLength": len(blob)})
        offset += (len(blob) + 3) // 4 * 4
    binary = b"".join(blob + b"\0" * (-len(blob) % 4) for blob in blobs)
    document = {
        "asset": {"version": "2.0"},
        "scene": 0,
        "scenes": [{"nodes": [0]}],
        "nodes": [{"mesh": 0, "name": "body"}],
        "meshes": [{"primitives": [{"attributes": {"POSITION": 0, "TEXCOORD_0": 1}, "indices": 2, "material": 0}]}],
        "materials": [{"pbrMetallicRoughness": {"baseColorTexture": {"index": 0}, "metallicFactor": 0, "roughnessFactor": 1}}],
        "textures": [{"source": 0}],
        "images": [{"bufferView": 3, "mimeType": "image/png"}],
        "buffers": [{"byteLength": len(binary)}],
        "bufferViews": views,
        "accessors": [
            {"bufferView": 0, "componentType": 5126, "count": len(positions), "type": "VEC3",
             "min": positions.min(axis=0).tolist(), "max": positions.max(axis=0).tolist()},
            {"bufferView": 1, "componentType": 5126, "count": len(uvs), "type": "VEC2"},
            {"bufferView": 2, "componentType": 5125, "count": triangles.size, "type": "SCALAR"},
        ],
    }
    text = json.dumps(document, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    Path(path).write_bytes(struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(text), 0x4E4F534A) + text
                           + struct.pack("<II", len(binary), 0x004E4942) + binary)


# ---------------------------------------------------------------- shapes shared by the fixtures

def ellipsoid_profile(steps):
    """Radius shares from one pole to the other of an ellipsoid, `steps` rings spaced evenly round it, so the poles
    stay round."""
    return [((1 - math.cos(math.pi * k / steps)) / 2, max(math.sin(math.pi * k / steps), 0.04)) for k in range(steps + 1)]


def bezier(a, b, c, steps):
    a, b, c = (np.asarray(p, dtype=float) for p in (a, b, c))
    return [(1 - t) ** 2 * a + 2 * (1 - t) * t * b + t * t * c for t in np.linspace(0.0, 1.0, steps)]


def along(a, b, steps):
    a, b = np.asarray(a, dtype=float), np.asarray(b, dtype=float)
    return [a + (b - a) * t for t in np.linspace(0.0, 1.0, steps)]


def mirror(point):
    s, f, u = point
    return (-s, f, u)


# ---------------------------------------------------------------- the niche unfolder

def build_unfolder(body, spec):
    """An oval shell seated in a wall recess: shingled plates over its back and sides, a frilled opening down its front
    over plum tissue and two amber organs, six short legs braced into the recess, and two long limbs ending in pincers.
    The limbs are built hanging forward and down; the clips fold them flat against the front and reach them out."""
    centre = np.array(spec["shellCentre"], dtype=float)
    rs, rf, ru = spec["shellRadii"]
    segments = 22
    # The shell, round the up axis, its front face carrying the opening's tissue.
    body.lathe(centre, (0, 0, 2 * ru), ellipsoid_profile(16), (rs, rf), segments, "shell", bones(body=1.0),
               reference=np.array((0.0, 1.0, 0.0)), marks=6)
    # The plates: bands of the shell a little proud of it, from the crown down, each overlapping the next like
    # shingles, round the back and sides, leaving the front open.
    plates = spec["plates"]
    open_degrees = spec["frontOpenDegrees"]
    for k in range(plates):
        top = 1.0 - k / plates * 0.95
        bottom = top - 1.25 / plates
        rows = []
        for r in range(5):
            z = top + (bottom - top) * r / 4
            height = centre[2] + ru * (2 * z - 1)
            flare = 1.03 + 0.012 * k + 0.12 * (r / 4) ** 2
            share = 2 * math.sqrt(min(max(z, 0.01), 0.99) * (1 - min(max(z, 0.01), 0.99))) * flare
            row = []
            for c in range(13):
                azimuth = math.radians(open_degrees / 2 + (360 - open_degrees) * c / 12)
                row.append((centre[0] + rs * share * math.sin(azimuth), centre[1] + rf * share * math.cos(azimuth), height))
            rows.append(row)
        tint = "lavender" if k in spec.get("lavenderPlates", ()) else "ivory"
        body.sheet(rows, "shell", bones(body=1.0), key=f"plate-{tint}", tint=tint, rims=(0.96,), marks=3, thickness=0.025,
                   organs=((0.3, 0.55, 0.035, 0.09), (0.7, 0.55, 0.035, 0.09)),
                   normal_hint=lambda p: np.array((p[0] - centre[0], p[1] - centre[1], 0.0)))
    # The opening: a long bulge of plum tissue down the front, two amber organs in it, and a frilled rim around it.
    front = centre[1] + rf * 0.82
    body.lathe((0.0, front, centre[2]), (0, 0, 2 * ru * 0.74), ellipsoid_profile(12), (rs * 0.46, rf * 0.3), 14,
               "tissue", bones(body=1.0), reference=np.array((0.0, 1.0, 0.0)))
    for height, size in spec["organs"]:
        body.lathe((0.0, front + rf * 0.16, height), (0, 0, size[2] * 2), ellipsoid_profile(10), (size[0], size[1]), 12,
                   "organ", bones(body=1.0), key="organ", reference=np.array((0.0, 1.0, 0.0)))
    ring = []
    for k in range(48):
        a = 2 * math.pi * k / 48
        ruffle = 0.025 * math.sin(14 * a)
        ring.append((math.sin(a) * (rs * 0.5 + ruffle), front + 0.03 + ruffle, centre[2] + math.cos(a) * (ru * 0.8 + ruffle)))
    body.tube(ring, [0.045] * len(ring), 8, "frill", bones(body=1.0), closed=True)
    # Six short legs, three a side, braced out and down into the recess floor.
    for sign in (1.0, -1.0):
        for forward in spec["legForwards"]:
            hip = (sign * rs * 0.7, centre[1] + forward, spec["legHip"])
            knee = (sign * (rs * 0.7 + 0.28), centre[1] + forward * 1.2, spec["legHip"] + 0.08)
            foot = (sign * (rs * 0.7 + 0.32), centre[1] + forward * 1.3, 0.02)
            body.tube(bezier(hip, knee, foot, 7), np.linspace(0.06, 0.035, 7), 8, "casing", bones(body=1.0),
                      key="leg", bands=(0.85,), insets=(0.4,))
    # The two long limbs, each three tubes (limb, fore, pincer) and a lower finger, wholly on their own bones.
    p = spec["limb"]
    for side, sign in (("L", 1.0), ("R", -1.0)):
        def at(name):
            point = p[name]
            return (sign * point[0], point[1], point[2])
        shoulder, elbow, wrist = at("shoulder"), at("elbow"), at("wrist")
        body.lathe(shoulder, (0, 0, 0.24), ellipsoid_profile(8), (0.13, 0.13), 10, "casing", bones(**{f"limb.{side}": 1.0}),
                   key="joint")
        body.tube(along(shoulder, elbow, 8), np.linspace(p["thickness"][0], p["thickness"][1], 8), 10, "casing",
                  bones(**{f"limb.{side}": 1.0}), key="upper", bands=(0.1, 0.9), insets=(0.5,))
        body.lathe(elbow, (0, 0, 0.2), ellipsoid_profile(8), (0.1, 0.1), 10, "casing", bones(**{f"fore.{side}": 1.0}),
                   key="elbow")
        body.tube(along(elbow, wrist, 8), np.linspace(p["thickness"][1], p["thickness"][2], 8), 10, "casing",
                  bones(**{f"fore.{side}": 1.0}), key="fore", bands=(0.12, 0.88), insets=(0.45,))
        body.lathe(wrist, (0, 0, 0.18), ellipsoid_profile(8), (0.1, 0.09), 10, "casing", bones(**{f"pincer.{side}": 1.0}),
                   key="wrist")
        # The pincer: a fixed upper finger on the wrist and a lower one that closes on it, both curving in at the tip.
        for name, bone, lift in (("upperFinger", f"pincer.{side}", 1.0), ("lowerFinger", f"finger.{side}", -1.0)):
            tip = at(name)
            bend = np.asarray(wrist) + 0.6 * (np.asarray(tip) - np.asarray(wrist)) + np.array((0.0, 0.0, lift * 0.09))
            path = bezier(wrist, bend, tip, 9)
            radii = [(0.07 * (1 - 0.75 * t), 0.045 * (1 - 0.6 * t)) for t in np.linspace(0, 1, 9)]
            body.tube(path, radii, 10, "pincer", bones(**{bone: 1.0}), key="finger",
                      reference=np.array((sign, 0.0, 0.0)))


# ---------------------------------------------------------------- the vault filter

# A lobe's collar sits this share of the way down it, round its waist.
LOBE_COLLAR = 0.8


def lobe_share(z):
    """A lobe's radius share at z down it: a narrow neck at the stalk swelling to a round bulb, held wide to the
    collar, then closing under it."""
    if z < 0.55:
        return max(math.sin(0.5 * math.pi * z / 0.55) ** 0.8, 0.16)
    if z < LOBE_COLLAR:
        return 1.0 - 0.1 * (z - 0.55) / (LOBE_COLLAR - 0.55)
    return 0.9 - 0.45 * ((z - LOBE_COLLAR) / (1.0 - LOBE_COLLAR)) ** 1.5


def build_filter(body, spec):
    """A root spread on the ceiling, three stalks, and on each a plated lobe with a jade collar and a pleated fringe."""
    ceiling = spec["ceilingMetres"]
    rng = np.random.default_rng(SEED + 1)
    knot = np.array((0.0, 0.0, ceiling - 0.05))
    body.lathe(knot, (0, 0, 0.12), ellipsoid_profile(6), (0.22, 0.2), 14, "root", bones(root=1.0))
    for k in range(spec["rootBranches"]):
        angle = 2 * math.pi * (k + rng.uniform(-0.2, 0.2)) / spec["rootBranches"]
        length = rng.uniform(0.55, 0.95)
        end = knot + np.array((math.sin(angle) * length, math.cos(angle) * length, 0.03))
        bend = knot + np.array((math.sin(angle + 0.25) * length * 0.5, math.cos(angle + 0.25) * length * 0.5, 0.02))
        body.tube(bezier(knot, bend, end, 6), [(r, r * 0.55) for r in np.linspace(0.06, 0.012, 6)], 6, "root",
                  bones(root=1.0), key="branch", reference=UP)
        fork = bezier(knot, bend, end, 6)[3]
        twig = fork + np.array((math.sin(angle + 0.9) * 0.35, math.cos(angle + 0.9) * 0.35, 0.02))
        body.tube(along(fork, twig, 4), [(r, r * 0.55) for r in np.linspace(0.03, 0.01, 4)], 6, "root", bones(root=1.0),
                  key="branch", reference=UP)
    for k, lobe in enumerate(spec["lobes"], start=1):
        bone = f"lobe{k}"
        top = np.array(lobe["top"], dtype=float)
        radius, height = spec["lobeRadius"] * lobe.get("size", 1.0), spec["lobeHeight"] * lobe.get("size", 1.0)
        # The stalk: from the knot, out and down to the lobe's top, stretching as the lobe drops.
        start = knot + np.array((top[0] * 0.15, top[1] * 0.15, -0.05))
        control = np.array((top[0] * 0.75, top[1] * 0.75, ceiling - 0.25))
        path = bezier(start, control, top + np.array((0.0, 0.0, 0.04)), 10)
        body.tube(path, np.linspace(0.07, 0.04, 10), 8, "root", lambda i, t, _p, b=bone: {"root": 1.0 - t, b: t},
                  key="stalk")
        # The lobe: a teardrop, plated, its bottom closed inside the collar.
        profile = [(z, lobe_share(z)) for z in np.linspace(0.0, 1.0, 13)]
        centre = top - np.array((0.0, 0.0, height / 2))
        body.lathe(centre, (0, 0, -height), profile, (radius, radius), 16, "shell", bones(**{bone: 1.0}), key="lobe",
                   quilt=18)
        collar = top - np.array((0.0, 0.0, height * LOBE_COLLAR))
        ring = [collar + np.array((math.sin(a) * radius * lobe_share(LOBE_COLLAR), math.cos(a) * radius * lobe_share(LOBE_COLLAR), 0.0))
                for a in np.linspace(0, 2 * math.pi, 24, endpoint=False)]
        body.tube(ring, [(0.045, 0.07)] * 24, 8, "jade_band", bones(**{bone: 1.0}), closed=True, key="collar",
                  reference=UP, glowing=0.9)
        # The fringe: pleats hanging from just inside the collar, its tips ragged; its points share the lobe and the two
        # nearest fringe bones by how far down they hang.
        columns, rows = spec["pleats"], 6
        sectors = spec["fringeBones"]
        lengths = [spec["fringeMetres"] * lobe.get("size", 1.0) * rng.uniform(0.72, 1.0) for _ in range(columns)]
        grid = []
        for r in range(rows):
            d = r / (rows - 1)
            row = []
            for c in range(columns):
                a = 2 * math.pi * c / columns
                pleat = 0.03 if c % 2 == 0 else -0.02
                reach = radius * lobe_share(LOBE_COLLAR) * 0.9 + pleat + 0.07 * d
                row.append(collar + np.array((math.sin(a) * reach, math.cos(a) * reach, -0.02 - lengths[c] * d)))
            grid.append(row)

        def fringe_weights(r, c, _p, b=bone, k=k):
            d = r / (rows - 1)
            share = smooth(d * 1.4)
            place = c / columns * sectors
            near = int(math.floor(place)) % sectors
            frac = place - math.floor(place)
            out = {b: 1.0 - share}
            out[f"fringe{k}_{near + 1}"] = share * (1.0 - frac)
            out[f"fringe{k}_{(near + 1) % sectors + 1}"] = out.get(f"fringe{k}_{(near + 1) % sectors + 1}", 0.0) + share * frac
            return out

        body.sheet(grid, "membrane", fringe_weights, key="fringe", wrap=True, pleats=columns, edged=False, glowing=0.1,
                   normal_hint=lambda p, c=collar: np.array((p[0] - c[0], p[1] - c[1], 0.0)))


# ---------------------------------------------------------------- the folding bower

def dome_radius(spec, h):
    """How far a rib stands from the bower's middle at height h: steep sides curving in to the crown."""
    ring, crown, top = spec["ringMetres"], spec["crownRadius"], spec["crownMetres"]
    x = min(max(h / top, 0.0), 1.0)
    return crown + (ring - crown) * (1.0 - x ** spec["domePower"]) ** (1.0 / spec["domePower"])


def hem(spec, bay, a):
    """The arch the membrane of a bay ends in, `a` across it from one rib (0) to the next (1)."""
    low = spec["archFoot"]
    high = spec["frontArch"] if bay == spec["frontBay"] else spec["sideArch"]
    return low + (high - low) * math.sin(math.pi * a) ** 0.7


def build_bower(body, spec):
    """Six ribs rooted in a ring and meeting at a crown, a membrane between each pair down to an arch, and under each
    arch a curtain, built down to the floor (closed) and weighted to rise and collapse along the arch while it is open."""
    ribs = spec["ribs"]
    top = spec["crownMetres"]
    rng = np.random.default_rng(SEED + 2)
    azimuths = [math.radians(spec["firstRibDegrees"] + 360.0 * k / ribs) for k in range(ribs)]
    heights = np.linspace(0.0, top * 0.985, 14)

    def at(azimuth, h, inset=0.0):
        r = dome_radius(spec, h) - inset
        return np.array((math.sin(azimuth) * r, math.cos(azimuth) * r, h))

    for k, azimuth in enumerate(azimuths):
        path = [at(azimuth, h) for h in heights]
        thick = np.linspace(spec["ribThickness"][0], spec["ribThickness"][1], len(path))
        body.tube(path, thick, 8, "casing", bones(root=1.0), key="rib", caps=(True, False), bands=(0.45,),
                  insets=(0.3, 0.7))
        # The outer branch sweeping out and down from near the crown, ending in a knob.
        start = at(azimuth, top * 0.86)
        end = at(azimuth + 0.12, top * 0.74, inset=-0.9)
        control = at(azimuth + 0.05, top * 0.9, inset=-0.5)
        body.tube(bezier(start, control, end, 7), np.linspace(0.11, 0.07, 7), 7, "casing", bones(root=1.0),
                  key="branch", bands=(0.9,))
        body.lathe(end, (0, 0, 0.3), ellipsoid_profile(6), (0.15, 0.15), 8, "shell", bones(root=1.0), key="knob",
                   organs=((0.25, 0.5, 0.18, 0.2),))
        # Rooted feet: short roots splaying out from the rib's foot.
        foot = at(azimuth, 0.35)
        for j in range(spec["rootsPerFoot"]):
            turn = azimuth + math.radians(-50 + 100 * j / max(spec["rootsPerFoot"] - 1, 1))
            out = foot + np.array((math.sin(turn) * 0.55, math.cos(turn) * 0.55, -0.35))
            mid = foot + np.array((math.sin(turn) * 0.3, math.cos(turn) * 0.3, -0.12))
            body.tube(bezier(foot, mid, out, 5), np.linspace(0.11, 0.04, 5), 7, "casing", bones(root=1.0), key="foot")
        # Organs set into the rib at head height and at its foot, facing out.
        for h in spec["organHeights"]:
            p = at(azimuth, h, inset=-spec["ribThickness"][0] * 0.6)
            body.lathe(p, (0, 0, 0.3), ellipsoid_profile(6), (0.15, 0.12), 8, "shell", bones(root=1.0), key="rib-organ",
                       organs=((0.25, 0.5, 0.2, 0.26),))
    # The crown: a ring where the ribs meet, around a dark hollow.
    crown = np.array((0.0, 0.0, top))
    ring = [crown + np.array((math.sin(a) * spec["crownRadius"], math.cos(a) * spec["crownRadius"], 0.02))
            for a in np.linspace(0, 2 * math.pi, 20, endpoint=False)]
    body.tube(ring, [0.07] * 20, 8, "casing", bones(root=1.0), closed=True, key="crown")
    body.lathe(crown + np.array((0, 0, -0.04)), (0, 0, 0.16), ellipsoid_profile(6), (spec["crownRadius"], spec["crownRadius"]),
               14, "hollow", bones(root=1.0))
    # The membranes and curtains, bay by bay.
    across, upper_rows, lower_rows = 11, 10, 6
    reach = spec["curtainRiseMetres"]
    for bay in range(ribs):
        a0, a1 = azimuths[bay], azimuths[(bay + 1) % ribs] + (2 * math.pi if bay == ribs - 1 else 0.0)
        bulge = spec["bulgeMetres"]

        def point(a, h, a0=a0, a1=a1):
            azimuth = a0 + (a1 - a0) * a
            r = dome_radius(spec, h) - 0.04 + bulge * math.sin(math.pi * a) * math.sin(math.pi * min(h / top, 1.0)) ** 0.5
            return np.array((math.sin(azimuth) * r, math.cos(azimuth) * r, h))

        upper = []
        for r in range(upper_rows):
            row = []
            for c in range(across):
                a = c / (across - 1)
                low = hem(spec, bay, a)
                h = top * 0.97 - (top * 0.97 - low) * r / (upper_rows - 1)
                row.append(point(a, h))
            upper.append(row)
        bay_bone = f"bay{bay + 1}"

        def upper_weights(r, c, _p, bay_bone=bay_bone):
            a = c / (across - 1)
            share = 0.85 * math.sin(math.pi * a) * math.sin(math.pi * r / (upper_rows - 1))
            return {"root": 1.0 - share, bay_bone: share}

        body.sheet(upper, "membrane", upper_weights, key="membrane",
                   normal_hint=lambda p: np.array((p[0], p[1], 0.0)), veins=6)
        lower = []
        for r in range(lower_rows):
            row = []
            for c in range(across):
                a = c / (across - 1)
                low = hem(spec, bay, a)
                row.append(point(a, low * (1.0 - r / (lower_rows - 1))))
            lower.append(row)
        curtain = f"curtain{bay + 1}"

        def lower_weights(r, c, p, bay=bay, curtain=curtain):
            # Rising by `reach`, each point comes to the arch above it: its share is how far below the arch it hangs.
            a = c / (across - 1)
            share = (hem(spec, bay, a) - p[2]) / reach
            return {"root": 1.0 - share, curtain: share}

        body.sheet(lower, "membrane", lower_weights, key="curtain",
                   normal_hint=lambda p: np.array((p[0], p[1], 0.0)), veins=3)
    del rng


# ---------------------------------------------------------------- the brood vessel

def build_vessel(body, spec):
    """Four amber sacs in pod frames on rooted stems, linked by plum tissue; each sac two halves hinged at its back."""
    tops = []
    for k, unit_spec in enumerate(spec["units"], start=1):
        base = np.array(unit_spec["at"], dtype=float)
        frame_low, frame_high = spec["frameFoot"], unit_spec["top"]
        width = spec["frameRadius"] * unit_spec.get("size", 1.0)
        # The stem: a flared foot on the floor, rising slender to the frame.
        stem = [base + np.array((0.0, 0.0, h)) for h in np.linspace(0.0, frame_low + 0.08, 8)]
        radii = [0.2 * (1 - smooth(h / 0.35)) + 0.05 for h in np.linspace(0.0, frame_low + 0.08, 8)]
        body.tube(stem, radii, 10, "casing", bones(root=1.0), key="stem", bands=(0.12,))
        # The frame: a pod shell round the back and sides, open down its front.
        open_degrees = spec["frontOpenDegrees"]
        rows = []
        for r in range(12):
            z = r / 11
            h = frame_low + (frame_high - frame_low) * z
            share = max(math.sin(math.pi * z), 0.02) ** 0.75
            row = []
            for c in range(11):
                azimuth = math.radians(open_degrees / 2 + (360 - open_degrees) * c / 10)
                row.append(base + np.array((math.sin(azimuth) * width * share, math.cos(azimuth) * width * 0.9 * share, h)))
            rows.append(row)
        body.sheet(rows, "shell", bones(root=1.0), key="frame", thickness=0.04, marks=0, organs=(
            (0.12, 0.35, 0.04, 0.03), (0.88, 0.35, 0.04, 0.03), (0.5, 0.6, 0.04, 0.03)),
            normal_hint=lambda p, b=base: np.array((p[0] - b[0], p[1] - b[1], 0.0)))
        # The frame's tip curls forward over the sac.
        tip = base + np.array((0.0, 0.0, frame_high))
        curl = bezier(tip - np.array((0.0, 0.05, 0.05)), tip + np.array((0.0, 0.05, 0.16)), tip + np.array((0.0, 0.22, 0.08)), 7)
        body.tube(curl, np.linspace(0.06, 0.02, 7), 8, "casing", bones(root=1.0), key="curl")
        tops.append(base + np.array((0.0, -0.1, frame_high - 0.12)))
        # The sac: two halves of an ellipsoid parted at its front and back, each on its own bone.
        middle = base + np.array((0.0, spec["sacForward"], frame_low + (frame_high - frame_low) * 0.5))
        rs, rf, ru = (r * unit_spec.get("size", 1.0) for r in spec["sacRadii"])
        for side, sign in (("L", 1.0), ("R", -1.0)):
            grid = []
            for r in range(11):
                z = r / 10
                share = max(math.sin(math.pi * z), 0.03) ** 0.85
                h = middle[2] + ru * (1 - 2 * z)
                row = []
                for c in range(9):
                    azimuth = math.radians(2 + 176 * c / 8)
                    row.append((middle[0] + sign * math.sin(azimuth) * rs * share, middle[1] + math.cos(azimuth) * rf * share, h))
                grid.append(row)
            body.sheet(grid, "sac", bones(**{f"sac{side}{k}": 1.0}), key="sac", thickness=0.03,
                       normal_hint=lambda p, m=middle: np.asarray(p) - m)
    # Plum tissue: two twisted strands sagging from each frame's top to the next.
    for k in range(len(tops) - 1):
        a, b = tops[k], tops[k + 1]
        middle = 0.5 * (a + b) + np.array((0.0, -0.08, -0.22))
        path = bezier(a, middle, b, 14)
        for strand in range(2):
            twisted = []
            for i, p in enumerate(path):
                angle = 2 * math.pi * (i / 13 * 2.5 + strand * 0.5)
                twisted.append(p + np.array((0.0, math.cos(angle) * 0.06, math.sin(angle) * 0.06)))
            body.tube(twisted, [0.07] * len(twisted), 8, "tissue", bones(root=1.0), key="tissue")
    # And a loop of it over the back of the first and last frames.
    for end, outward in ((tops[0], -1.0), (tops[-1], 1.0)):
        loop = bezier(end, end + np.array((outward * 0.3, -0.15, 0.25)), end + np.array((outward * 0.1, -0.2, -0.35)), 9)
        body.tube(loop, [0.075] * 9, 8, "tissue", bones(root=1.0), key="tissue")


BUILDERS = {"unfolder": build_unfolder, "filter": build_filter, "bower": build_bower, "vessel": build_vessel}


def main():
    argv = sys.argv[1:]
    species = json.loads(Path(argv[0]).read_text())
    spec = species["fixture"]
    body = Body()
    BUILDERS[spec["kind"]](body, spec)
    size = pack(body)
    paint, emission = paint_atlas(body, size)
    positions = np.array([Body.gltf(p) for p in body.positions], np.float32)
    uvs = atlas_uvs(body, size)
    triangles = np.array(body.triangles, np.uint32)
    write_glb(argv[1], positions, uvs, triangles, paint)
    names = sorted({bone for weights in body.weights for bone in weights})
    rig = {b[0] for b in species["bones"]}
    missing = sorted(set(names) - rig)
    if missing:
        raise SystemExit(f"weights name bones the species has none of: {missing}")
    points = [[round(float(x), 5) for x in p] + [x for bone, w in weights.items() for x in (bone, round(w, 4))]
              for p, weights in zip(body.positions, body.weights)]
    Path(argv[2]).write_text(json.dumps({"$comment": "make_fixture.py: [side, forward, up, bone, weight, ...] per vertex",
                                         "points": points}, separators=(",", ":")))
    if "--paint" in argv:
        paint.save(argv[argv.index("--paint") + 1])
    if "--emission" in argv:
        emission.save(argv[argv.index("--emission") + 1])
    lo, hi = positions.min(axis=0), positions.max(axis=0)
    print(f"{species['id']}: {len(positions)} vertices, {len(triangles)} triangles; side {lo[0]:.2f}..{hi[0]:.2f}, "
          f"up {lo[1]:.2f}..{hi[1]:.2f}, forward {lo[2]:.2f}..{hi[2]:.2f}; paint {size[0]}x{size[1]}, bones {names}")


if __name__ == "__main__":
    main()
