"""Rough procedural brushwork for costume textures (#10028).

Paint is laid in texture space, but every decision is made on the *canonical body*: the neutral
MPFB body fitted to UAL's T-pose, one position per base vertex. Each texel knows where on that
body it lies (rasterised from MakeHuman's UV layout), so a costume is a list of regions in body
terms ("torso above the waist", "sleeve to mid-forearm"). It fits every character unchanged,
because they all share the topology and the layout.

The style follows docs/evidence/character-directions/style-sheet-draft.md:
- broad colour patches laid as overlapping strokes along the fabric's fall;
- darker strokes at folds (elbows, knees, waist, armpits);
- painted edges where colours meet;
- soft form shading in creases and undersides. No light direction is painted.
"""
import math

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

# Canonical landmarks on UAL's T-pose (metres): arms lie along x.
SHOULDER_X, ELBOW_X, WRIST_X = 0.20, 0.47, 0.73
HIP_Y, WAIST_Y, CHEST_Y, NECK_Y, CHIN_Y = 0.93, 1.03, 1.30, 1.44, 1.52
KNEE_Y, ANKLE_Y = 0.53, 0.11


def hexrgb(value):
    value = value.lstrip("#")
    return np.array([int(value[i:i + 2], 16) for i in (0, 2, 4)], np.float64)


def in_region(kind, p, **o):
    """Whether canonical points (..., 3) lie in a body region (torso, sleeve, hand, legs, feet, head, neck)."""
    x, y, z = p[..., 0], p[..., 1], p[..., 2]
    ax = np.abs(x)
    arm = (ax > SHOULDER_X - 0.02) & (y > 1.15)
    if kind == "head":
        return (y > NECK_Y + 0.02) & ~arm
    if kind == "neck":
        return (y > NECK_Y - 0.06) & (y <= NECK_Y + 0.02) & ~arm
    if kind == "torso":
        # The default top follows the shoulders: a collar at the neck, cloth over the shoulder tops.
        top = o.get("top", NECK_Y - 0.015) + (0.06 * np.clip((ax - 0.07) / 0.05, 0, 1) if "top" not in o else 0)
        m = (y <= top) & (y > o.get("bottom", HIP_Y - 0.05)) & ~arm
        if "front" in o:
            m &= z > o["front"]
        if "halfWidth" in o:
            m &= ax < o["halfWidth"]
        return m
    if kind == "sleeve":
        return arm & (ax <= o.get("to", WRIST_X))
    if kind == "hand":
        return arm & (ax > WRIST_X)
    if kind == "legs":
        return (y <= o.get("top", HIP_Y)) & (y > o.get("bottom", ANKLE_Y)) & ~arm
    if kind == "feet":
        return (y <= o.get("top", ANKLE_Y)) & ~arm
    raise ValueError(kind)


def rasterise(size, corner_uv, corner_attr):
    """Per texel: the interpolated attribute (n x 3 x k corners -> size x size x k) and coverage."""
    k = corner_attr.shape[-1]
    out = np.zeros((size, size, k))
    covered = np.zeros((size, size), bool)
    ys, xs = np.mgrid[0:size, 0:size] + 0.5
    for uv, attr in zip(corner_uv, corner_attr):
        p = np.stack([uv[:, 0] * size, (1 - uv[:, 1]) * size], 1)
        a, b, c = p
        m = np.array([[b[0] - a[0], c[0] - a[0]], [b[1] - a[1], c[1] - a[1]]])
        if abs(np.linalg.det(m)) < 1e-12:
            continue
        lo = np.floor(p.min(0) - 1).astype(int).clip(0, size - 1)
        hi = np.ceil(p.max(0) + 1).astype(int).clip(0, size - 1)
        sub = np.stack([xs[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1], ys[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]], -1)
        w = np.einsum("ij,...j->...i", np.linalg.inv(m), sub - a)
        margin = 1.2 / max(np.linalg.norm(b - a), np.linalg.norm(c - a), 1)
        inside = (w[..., 0] >= -margin) & (w[..., 1] >= -margin) & (w.sum(-1) <= 1 + margin)
        bary = np.stack([1 - w.sum(-1), w[..., 0], w[..., 1]], -1)
        region = out[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1]
        region[inside] = (bary @ attr)[inside]
        covered[lo[1]:hi[1] + 1, lo[0]:hi[0] + 1] |= inside
    return out, covered


class Canvas:
    """Texture-space canvas with per-texel canonical position (x, y, z) and normal."""

    def __init__(self, size, attrs, covered, seed):
        self.size = size
        self.pos = attrs[..., :3]
        self.normal = attrs[..., 3:6]
        self.covered = covered
        self.rgb = np.zeros((size, size, 3))
        self.painted = np.zeros((size, size), bool)
        self.rng = np.random.default_rng(seed)
        # The fabric's fall in texture space: the gradient of canonical height (folds run along it).
        gy, gx = np.gradient(self.pos[..., 1])
        along_arm = np.abs(self.pos[..., 0]) > SHOULDER_X
        ax_y, ax_x = np.gradient(np.abs(self.pos[..., 0]))
        self.fall = np.where(along_arm[..., None], np.stack([ax_x, ax_y], -1), np.stack([gx, gy], -1))

    def region(self, kind, **o):
        return in_region(kind, self.pos, **o)

    def fill(self, mask, colour, seed_strokes=True, stroke_len=0.06, value_jitter=0.07, hue_jitter=0.03):
        """Broad patch of colour laid as overlapping strokes along the fall."""
        mask = mask & self.covered
        base = hexrgb(colour)
        self.rgb[mask] = base
        self.painted |= mask
        if not seed_strokes or not mask.any():
            return
        layer = Image.new("RGB", (self.size, self.size))
        alpha = Image.new("L", (self.size, self.size), 0)
        dc, da = ImageDraw.Draw(layer), ImageDraw.Draw(alpha)
        ys, xs = np.nonzero(mask)
        count = int(len(ys) / (self.size * 0.9) * 6) + 8
        length = stroke_len * self.size
        for _ in range(count):
            i = self.rng.integers(len(ys))
            cy, cx = ys[i], xs[i]
            fx, fy = self.fall[cy, cx]
            angle = math.atan2(fy, fx) if (fx or fy) else 0
            angle += self.rng.normal(0, 0.25)
            ln = length * self.rng.uniform(0.5, 1.4)
            w = ln * self.rng.uniform(0.16, 0.32)
            col = base * (1 + self.rng.normal(0, value_jitter)) + self.rng.normal(0, hue_jitter * 255, 3)
            col = tuple(int(v) for v in np.clip(col, 0, 255))
            dx, dy = math.cos(angle) * ln / 2, math.sin(angle) * ln / 2
            nx, ny = -math.sin(angle) * w / 2, math.cos(angle) * w / 2
            quad = [(cx - dx, cy - dy), (cx - dx * 0.6 + nx, cy - dy * 0.6 + ny), (cx + dx * 0.7 + nx * 0.7, cy + dy * 0.7 + ny * 0.7),
                    (cx + dx, cy + dy), (cx + dx * 0.7 - nx * 0.7, cy + dy * 0.7 - ny * 0.7), (cx - dx * 0.6 - nx, cy - dy * 0.6 - ny)]
            dc.polygon(quad, fill=col)
            da.polygon(quad, fill=int(self.rng.uniform(110, 200)))
        a = np.asarray(alpha.filter(ImageFilter.GaussianBlur(0.6)), np.float64)[..., None] / 255 * mask[..., None]
        self.rgb = self.rgb * (1 - a) + np.asarray(layer, np.float64) * a

    def strokes(self, mask, colour, count, length, width, across=False, alpha=(120, 190)):
        """Darker or accent strokes (folds, laces, seams) inside a mask."""
        mask = mask & self.covered
        if not mask.any():
            return
        layer = Image.new("RGB", (self.size, self.size))
        al = Image.new("L", (self.size, self.size), 0)
        dc, da = ImageDraw.Draw(layer), ImageDraw.Draw(al)
        ys, xs = np.nonzero(mask)
        base = hexrgb(colour)
        for _ in range(count):
            i = self.rng.integers(len(ys))
            cy, cx = ys[i], xs[i]
            fx, fy = self.fall[cy, cx]
            angle = (math.atan2(fy, fx) if (fx or fy) else 0) + (math.pi / 2 if across else 0) + self.rng.normal(0, 0.3)
            ln = length * self.size * self.rng.uniform(0.6, 1.3)
            pts = [(cx + math.cos(angle) * ln * t + self.rng.normal(0, 0.6), cy + math.sin(angle) * ln * t + self.rng.normal(0, 0.6)) for t in np.linspace(-0.5, 0.5, 5)]
            col = tuple(int(v) for v in np.clip(base * (1 + self.rng.normal(0, 0.05)), 0, 255))
            dc.line(pts, fill=col, width=max(1, int(width * self.size)), joint="curve")
            da.line(pts, fill=int(self.rng.uniform(*alpha)), width=max(1, int(width * self.size)), joint="curve")
        a = np.asarray(al.filter(ImageFilter.GaussianBlur(0.7)), np.float64)[..., None] / 255 * mask[..., None]
        self.rgb = self.rgb * (1 - a) + np.asarray(layer, np.float64) * a

    def folds(self, mask, colour):
        """Fold strokes where the body bends: elbows, knees, waist, armpits and the hips' crease."""
        x, y = self.pos[..., 0], self.pos[..., 1]
        ax = np.abs(x)
        near = ((np.abs(ax - ELBOW_X) < 0.05) & (y > 1.15)) | (np.abs(y - KNEE_Y) < 0.05) | (np.abs(y - WAIST_Y) < 0.035) \
            | ((np.abs(ax - SHOULDER_X) < 0.05) & (y > 1.2) & (y < 1.36)) | (np.abs(y - HIP_Y) < 0.03)
        self.strokes(mask & near, colour, int((mask & near).sum() / self.size * 0.9) + 4, 0.03, 0.006, across=True)

    def edges(self, mask, colour, width=2):
        """A painted edge where a region meets another."""
        m = Image.fromarray((mask & self.covered).astype(np.uint8) * 255)
        inner = np.asarray(m.filter(ImageFilter.MinFilter(2 * width + 1))) > 0
        edge = (mask & self.covered) & ~inner
        base = hexrgb(colour)
        jitter = self.rng.normal(0, 0.08, edge.sum())[:, None]
        self.rgb[edge] = self.rgb[edge] * 0.35 + base * (0.65 + jitter)

    def form_shading(self, strength=0.22):
        """Soft painted form: creases and undersides darker, never a light direction."""
        x, y = self.pos[..., 0], self.pos[..., 1]
        ax = np.abs(x)
        under = np.clip(-self.normal[..., 1], 0, 1) * 0.5
        crease = np.exp(-((ax - SHOULDER_X) / 0.04) ** 2) * ((y > 1.18) & (y < 1.36)) + np.exp(-((y - HIP_Y + 0.02) / 0.03) ** 2) * (ax < 0.12) \
            + np.exp(-((y - NECK_Y + 0.04) / 0.03) ** 2) * (ax < 0.12)
        shade = 1 - strength * np.clip(under + 0.6 * crease, 0, 1)
        self.rgb *= shade[..., None]

    def image(self, dilate=8):
        """RGBA, with painted colour grown into the empty atlas so nearest sampling never meets black."""
        rgb = self.rgb.copy()
        known = self.covered.copy()
        for _ in range(dilate):
            acc = np.zeros_like(rgb)
            cnt = np.zeros(known.shape)
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    acc += np.roll(np.roll(rgb * known[..., None], dy, 0), dx, 1)
                    cnt += np.roll(np.roll(known, dy, 0), dx, 1)
            grow = (~known) & (cnt > 0)
            rgb[grow] = acc[grow] / cnt[grow][:, None]
            known |= grow
        rgba = np.concatenate([rgb.clip(0, 255), np.full((self.size, self.size, 1), 255.0)], -1)
        return Image.fromarray(rgba.astype(np.uint8), "RGBA")


def band_canvas(size, seed):
    """A plain canvas for cylindrically mapped shells (skirt, hair, hat): x = around, y = down."""
    c = Canvas.__new__(Canvas)
    c.size = size
    v, u = np.mgrid[0:size, 0:size] / size
    c.pos = np.stack([np.zeros_like(u), 1 - v, np.zeros_like(u)], -1)
    c.normal = np.zeros((size, size, 3))
    c.covered = np.ones((size, size), bool)
    c.rgb = np.zeros((size, size, 3))
    c.painted = np.zeros((size, size), bool)
    c.rng = np.random.default_rng(seed)
    c.fall = np.stack([np.zeros_like(u), np.ones_like(u)], -1)
    return c
