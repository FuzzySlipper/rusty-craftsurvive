"""Generate the river boats' prop meshes (#9879): a log raft and a dugout canoe.

Both are built in metres with Y up and the waterline at y = 0, so the product floats a boat by
standing its origin at the water's surface. The raft is seven pine logs lashed under two timber
crossbars; the canoe is a dugout: pine bark outside, worked timber inside, a timber rim. Textures
come from the trees' and construction's own (no new art), repeated by the metre.

    python3 scripts/generate-boats.py
"""
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CONTENT = ROOT / "content" / "game"
MODELS = CONTENT / "models"

# The boats' textures are the pine's bark and the builders' timber, lifted: a texture is sRGB-decoded
# when opened, so a prop's texture needs a mean luminance near 0.5-0.7 to read as light wood (the tree
# and construction sources are darker, for their own materials).
BARK_SOURCE = "textures/trees/pine-bark.png"
TIMBER_SOURCE = "textures/construction/timber.png"
BARK = "textures/props/boat-bark.png"
TIMBER = "textures/props/boat-timber.png"
TARGET_LUMINANCE = 0.5

# Raft: logs along X.
RAFT_LOGS = 7
RAFT_LOG_LENGTH = 4.0
RAFT_LOG_RADIUS = 0.16
RAFT_LOG_SPACING = 0.33
RAFT_SIDES = 12
RAFT_BAR_AT = (-1.45, 1.45)
RAFT_BAR_SIZE = (0.14, 0.1)  # along X, height

# Canoe: a dugout hull lofted along X.
CANOE_LENGTH = 4.6
CANOE_BEAM = 0.78
CANOE_RIM = 0.26
CANOE_KEEL = -0.2
CANOE_WALL = 0.06
CANOE_STATIONS = 24
CANOE_ARC = 10
CANOE_END_POWER = 0.6
# The water surface is one plane, drawn through anything under it: a hull's inside below the waterline
# would show water. So the dugout rides with its floor just above the surface (a kinematic float, #9879).
CANOE_RIDE = 0.2

UV_METRES = 1.0


class Part:
    def __init__(self, role):
        self.role = role
        self.positions, self.normals, self.uvs, self.indices = [], [], [], []

    def vertex(self, p, n, uv):
        self.positions.append(p)
        self.normals.append(n)
        self.uvs.append(uv)
        return len(self.positions) - 1

    def triangle(self, a, b, c):
        """A triangle wound anticlockwise seen from the side its vertex normals face."""
        pa, pb, pc = self.positions[a], self.positions[b], self.positions[c]
        e1 = [pb[i] - pa[i] for i in range(3)]
        e2 = [pc[i] - pa[i] for i in range(3)]
        face = (e1[1] * e2[2] - e1[2] * e2[1], e1[2] * e2[0] - e1[0] * e2[2], e1[0] * e2[1] - e1[1] * e2[0])
        normal = [sum(self.normals[v][i] for v in (a, b, c)) for i in range(3)]
        self.indices += [a, b, c] if sum(face[i] * normal[i] for i in range(3)) >= 0 else [a, c, b]

    def quad(self, a, b, c, d):
        """Two triangles over a quad, each wound to face its vertex normals."""
        self.triangle(a, b, c)
        self.triangle(a, c, d)

    def json(self):
        flat = lambda rows: [round(v, 4) for row in rows for v in row]
        return {
            "role": self.role,
            "positions": flat(self.positions),
            "normals": flat(self.normals),
            "uvs": flat(self.uvs),
            # White, and a wind weight of nothing: a boat does not sway.
            "colors": [1.0, 1.0, 1.0, 0.0] * len(self.positions),
            "indices": self.indices,
        }


def normalise(v):
    length = math.sqrt(sum(c * c for c in v)) or 1.0
    return tuple(c / length for c in v)


def log(part, z, y):
    """One log along X: a capped cylinder of RAFT_SIDES faces."""
    half = RAFT_LOG_LENGTH / 2
    ring = []
    for s in range(RAFT_SIDES + 1):
        angle = 2 * math.pi * s / RAFT_SIDES
        cz, cy = math.cos(angle), math.sin(angle)
        u = s / RAFT_SIDES * (2 * math.pi * RAFT_LOG_RADIUS) / UV_METRES
        a = part.vertex((-half, y + cy * RAFT_LOG_RADIUS, z + cz * RAFT_LOG_RADIUS), (0, cy, cz), (0, u))
        b = part.vertex((half, y + cy * RAFT_LOG_RADIUS, z + cz * RAFT_LOG_RADIUS), (0, cy, cz), (RAFT_LOG_LENGTH / UV_METRES, u))
        ring.append((a, b))
    for s in range(RAFT_SIDES):
        (a0, b0), (a1, b1) = ring[s], ring[s + 1]
        part.quad(a0, a1, b1, b0)
    for end, sign in ((-half, -1), (half, 1)):
        centre = part.vertex((end, y, z), (sign, 0, 0), (0.5, 0.5))
        rim = []
        for s in range(RAFT_SIDES + 1):
            angle = 2 * math.pi * s / RAFT_SIDES
            cz, cy = math.cos(angle), math.sin(angle)
            rim.append(part.vertex((end, y + cy * RAFT_LOG_RADIUS, z + cz * RAFT_LOG_RADIUS), (sign, 0, 0), (0.5 + cz / 2, 0.5 + cy / 2)))
        for s in range(RAFT_SIDES):
            part.triangle(centre, rim[s], rim[s + 1])


def box(part, centre, half):
    """An axis-aligned box, each face its own four vertices."""
    cx, cy, cz = centre
    hx, hy, hz = half
    faces = [
        ((1, 0, 0), (0, 0, -1), (0, 1, 0)),
        ((-1, 0, 0), (0, 0, 1), (0, 1, 0)),
        ((0, 1, 0), (1, 0, 0), (0, 0, -1)),
        ((0, -1, 0), (1, 0, 0), (0, 0, 1)),
        ((0, 0, 1), (1, 0, 0), (0, 1, 0)),
        ((0, 0, -1), (-1, 0, 0), (0, 1, 0)),
    ]
    for normal, right, up in faces:
        size = lambda axis: abs(axis[0]) * hx + abs(axis[1]) * hy + abs(axis[2]) * hz
        n, r, u = normal, right, up
        sn, sr, su = size(n), size(r), size(u)
        corners = []
        for dr, du in ((-1, -1), (1, -1), (1, 1), (-1, 1)):
            p = tuple(c + n[i] * sn + r[i] * sr * dr + u[i] * su * du for i, c in enumerate((cx, cy, cz)))
            corners.append(part.vertex(p, n, ((dr + 1) * sr / UV_METRES, (du + 1) * su / UV_METRES)))
        part.quad(*corners)


def raft():
    logs, bars = Part("bark"), Part("solid")
    width = (RAFT_LOGS - 1) * RAFT_LOG_SPACING
    for i in range(RAFT_LOGS):
        log(logs, -width / 2 + i * RAFT_LOG_SPACING, 0.0)
    bar_half = (RAFT_BAR_SIZE[0] / 2, RAFT_BAR_SIZE[1] / 2, width / 2 + RAFT_LOG_RADIUS)
    for x in RAFT_BAR_AT:
        box(bars, (x, RAFT_LOG_RADIUS + bar_half[1] - 0.02, 0.0), bar_half)
    return [logs, bars]


def canoe():
    outside, inside = Part("bark"), Part("solid")
    stations = []
    for s in range(CANOE_STATIONS + 1):
        t = s / CANOE_STATIONS
        x = (t - 0.5) * CANOE_LENGTH
        taper = max(0.0, 1 - abs(2 * t - 1)) ** CANOE_END_POWER
        stations.append((x, taper, t))

    def surface(part, scale, inward):
        rows = []
        for x, taper, t in stations:
            beam = CANOE_BEAM / 2 * taper * scale
            keel = CANOE_RIM + (CANOE_KEEL - CANOE_RIM) * (0.35 + 0.65 * taper) * scale
            row = []
            for a in range(CANOE_ARC + 1):
                angle = math.pi * a / CANOE_ARC  # 0 starboard rim, pi port rim, through the keel
                z = beam * math.cos(angle)
                y = CANOE_RIM + (keel - CANOE_RIM) * math.sin(angle)
                n = normalise((0.0, -math.sin(angle) * (CANOE_RIM - keel), math.cos(angle) * max(beam, 1e-3)))
                if inward:
                    n = tuple(-c for c in n)
                row.append(part.vertex((x, y + CANOE_RIDE, z), n, (t * CANOE_LENGTH / UV_METRES, a / CANOE_ARC * CANOE_BEAM * 1.6 / UV_METRES)))
            rows.append(row)
        for s in range(CANOE_STATIONS):
            for a in range(CANOE_ARC):
                p, q, r, w = rows[s][a], rows[s][a + 1], rows[s + 1][a + 1], rows[s + 1][a]
                part.quad(p, q, r, w)
        return rows

    outer = surface(outside, 1.0, False)
    inner_scale = 1 - CANOE_WALL / (CANOE_BEAM / 2)
    inner = surface(inside, inner_scale, True)
    # The rim: a flat strip between the outer and inner edges on both sides.
    for side in (0, CANOE_ARC):
        for s in range(CANOE_STATIONS):
            o0, o1 = outside.positions[outer[s][side]], outside.positions[outer[s + 1][side]]
            i0, i1 = inside.positions[inner[s][side]], inside.positions[inner[s + 1][side]]
            up = (0.0, 1.0, 0.0)
            v = [inside.vertex(p, up, (p[2], p[0])) for p in (o0, o1, i1, i0)]
            inside.quad(v[0], v[1], v[2], v[3])
    return [outside, inside]


def lift(source, target):
    """Writes the source texture with its tones lifted by one gamma to the target mean luminance (RGBA)."""
    import numpy as np
    from PIL import Image

    image = Image.open(CONTENT / source).convert("RGBA")
    pixels = np.asarray(image).astype(float) / 255
    rgb, alpha = pixels[..., :3], pixels[..., 3:]
    luminance = lambda c: (c @ [0.2126, 0.7152, 0.0722]).mean()
    low, high = 0.05, 1.0
    for _ in range(40):
        gamma = (low + high) / 2
        if luminance(rgb ** gamma) > TARGET_LUMINANCE:
            low = gamma
        else:
            high = gamma
    lifted = np.concatenate([rgb ** gamma, alpha], axis=-1)
    Image.fromarray((lifted * 255).round().astype(np.uint8), "RGBA").save(CONTENT / target)
    print(CONTENT / target, f"gamma {gamma:.2f}", f"luminance {luminance(rgb ** gamma):.2f}")


def write(name, parts):
    positions = [p for part in parts for p in part.positions]
    low = [min(p[i] for p in positions) for i in range(3)]
    high = [max(p[i] for p in positions) for i in range(3)]
    document = {
        "name": name,
        "height": round(high[1] - low[1], 4),
        "bounds": {"min": [round(v, 4) for v in low], "max": [round(v, 4) for v in high]},
        "parts": [part.json() for part in parts],
        "textures": {"bark": BARK, "solid": TIMBER},
    }
    path = MODELS / f"{name}.prop-mesh.json"
    path.write_text(json.dumps(document, separators=(",", ":")))
    print(path, document["bounds"], sum(len(p.indices) // 3 for p in parts), "triangles")


if __name__ == "__main__":
    lift(BARK_SOURCE, BARK)
    lift(TIMBER_SOURCE, TIMBER)
    write("raft", raft())
    write("canoe", canoe())
