#!/usr/bin/env python3
"""The rocks of frozen ground (#10629): closed, chunky stones in three species for the flora catalog.

Each rock is a seeded sphere cut flat by a few planes, which gives it broad faces and hard edges, then stretched into a stone,
a slab, a shard or a lump; an outcrop is several pressed together and sunk deep. Rocks sit part buried, their origin at the
ground, and share one tile of the field stones' weathered bedrock with its green taken out, because nothing grows here. The
tile is projected on each triangle's dominant plane at 32 texels a metre, the crunch rule.

    python3 authoring/tools/generators/make-frozen-rocks.py
"""
import colorsys
import json
from pathlib import Path

import numpy as np
from PIL import Image

ROOT = Path(__file__).resolve().parents[3]
MODELS = ROOT / "content/game/models/scatter"
SOURCE_TEXTURE = ROOT / "content/game/textures/scatter/stones.png"
TEXTURE = ROOT / "content/game/textures/scatter/frozen-rock.png"
TEXTURE_PATH = "textures/scatter/frozen-rock.png"

TILE_TEXELS = 128
TEXELS_PER_METRE = 32
# Green paint (hue, in turns) keeps this much of its colour; the pale tile is dimmed this much, so stone stands dark on snow.
GREEN_HUES = (0.14, 0.5)
GREEN_KEEPS = 0.1
LIGHT = 0.72

# Shares of a rock's height left under the ground.
BURIED = 0.25
OUTCROP_BURIED = 0.4
# Sphere subdivisions, and how many planes cut it and how deep (a share of the radius from the centre).
SMALL_DETAIL, LARGE_DETAIL = 2, 3
CUTS = (7, 12)
CUT_DEPTH = (0.55, 0.92)
# How far a low wobble moves the surface, as a share of the radius.
WOBBLE = 0.05

# name, seed, width in metres, then stretch across, up and along.
SCREE = [
    ("scree-rock-1", 11, 0.55, (1.0, 0.8, 1.15)),
    ("scree-rock-2", 12, 0.42, (1.25, 0.7, 0.9)),
    ("scree-rock-3", 13, 0.62, (1.0, 0.95, 1.1)),
    ("scree-rock-4", 14, 0.38, (0.85, 1.25, 0.95)),
]
BOULDERS = [
    ("boulder-1", 21, 1.5, (1.0, 0.85, 1.15)),
    ("boulder-2", 22, 2.0, (1.4, 0.5, 1.1)),
    ("boulder-3", 23, 1.2, (0.75, 1.6, 0.85)),
    ("boulder-4", 24, 2.3, (1.5, 0.42, 1.0)),
]
# name, then each rock: seed, width, stretch, where it stands (metres), turn in degrees.
OUTCROPS = [
    ("outcrop-1", [(31, 3.0, (1.3, 0.85, 1.0), (0, 0, 0), 0), (32, 2.2, (1.0, 1.2, 0.9), (1.5, 0, 0.5), 70), (33, 1.6, (0.8, 1.5, 0.8), (-1.2, 0, 0.8), 140)]),
    ("outcrop-2", [(34, 3.4, (1.6, 0.6, 1.1), (0, 0, 0), 30), (35, 2.0, (1.0, 1.0, 1.0), (-1.4, 0, -0.7), 200), (36, 1.5, (1.2, 0.7, 0.9), (1.6, 0, -0.6), 95)]),
    ("outcrop-3", [(37, 2.8, (0.9, 1.5, 0.9), (0, 0, 0), 15), (38, 2.4, (1.4, 0.65, 1.0), (1.1, 0, 0.9), 120)]),
]


def icosphere(detail):
    """A unit sphere of triangles, wound outward."""
    t = (1 + 5**0.5) / 2
    points = [(-1, t, 0), (1, t, 0), (-1, -t, 0), (1, -t, 0), (0, -1, t), (0, 1, t), (0, -1, -t), (0, 1, -t), (t, 0, -1), (t, 0, 1), (-t, 0, -1), (-t, 0, 1)]
    faces = [(0, 11, 5), (0, 5, 1), (0, 1, 7), (0, 7, 10), (0, 10, 11), (1, 5, 9), (5, 11, 4), (11, 10, 2), (10, 7, 6), (7, 1, 8),
             (3, 9, 4), (3, 4, 2), (3, 2, 6), (3, 6, 8), (3, 8, 9), (4, 9, 5), (2, 4, 11), (6, 2, 10), (8, 6, 7), (9, 8, 1)]
    points = [np.array(point, float) / np.linalg.norm(point) for point in points]
    for _ in range(detail):
        middles, finer = {}, []

        def middle(a, b):
            key = (min(a, b), max(a, b))
            if key not in middles:
                point = points[a] + points[b]
                points.append(point / np.linalg.norm(point))
                middles[key] = len(points) - 1
            return middles[key]

        for a, b, c in faces:
            ab, bc, ca = middle(a, b), middle(b, c), middle(c, a)
            finer += [(a, ab, ca), (b, bc, ab), (c, ca, bc), (ab, bc, ca)]
        faces = finer
    return np.array(points), np.array(faces)


def rock(seed, width, stretch, at=(0, 0, 0), turn=0):
    """A closed rock: its points and triangles."""
    rng = np.random.default_rng(seed)
    points, faces = icosphere(LARGE_DETAIL if width >= 1 else SMALL_DETAIL)
    for _ in range(int(rng.integers(*CUTS))):
        normal = rng.normal(size=3)
        normal /= np.linalg.norm(normal)
        depth = rng.uniform(*CUT_DEPTH)
        over = points @ normal - depth
        points = points - np.outer(np.maximum(over, 0), normal)
    phase = rng.uniform(0, 6.28, 3)
    wobble = np.sin(points[:, 0] * 3.1 + phase[0]) * np.sin(points[:, 1] * 2.7 + phase[1]) * np.sin(points[:, 2] * 3.3 + phase[2])
    points = points * (1 + WOBBLE * wobble)[:, None]
    angle = np.radians(turn)
    spin = np.array([[np.cos(angle), 0, np.sin(angle)], [0, 1, 0], [-np.sin(angle), 0, np.cos(angle)]])
    return (points * np.array(stretch) * width / 2) @ spin.T + np.array(at), faces


def smooth_normals(points, faces):
    corners = points[faces]
    area = np.cross(corners[:, 1] - corners[:, 0], corners[:, 2] - corners[:, 0])
    normals = np.zeros_like(points)
    for index in range(3):
        np.add.at(normals, faces[:, index], area)
    return normals / np.maximum(np.linalg.norm(normals, axis=1, keepdims=True), 1e-9), area


def write(name, rocks, buried):
    pieces = [(points, faces, *smooth_normals(points, faces)) for points, faces in rocks]
    every = np.concatenate([piece[0] for piece in pieces])
    floor = every[:, 1].min() + np.ptp(every[:, 1]) * buried
    tile_metres = TILE_TEXELS / TEXELS_PER_METRE
    positions, normals, uvs = [], [], []
    for points, faces, vertex_normals, area in pieces:
        for face, facing in zip(faces, area):
            axis = int(np.argmax(np.abs(facing)))
            plane = [(2, 1), (0, 2), (0, 1)][axis]
            for vertex in face:
                point = points[vertex]
                positions.append((point[0], point[1] - floor, point[2]))
                normals.append(vertex_normals[vertex])
                # Each plane reads a different part of the tile, so a corner does not line its marks up.
                uvs.append((point[plane[0]] / tile_metres + axis * 0.37, point[plane[1]] / tile_metres + axis * 0.21))
    at = np.array(positions)
    count = len(at)
    rounded = lambda values: [round(float(value), 4) for value in np.array(values).ravel()]
    document = {
        "name": name,
        "height": round(float(at[:, 1].max()), 4),
        "bounds": {"min": rounded(at.min(0)), "max": rounded(at.max(0))},
        "parts": [
            {
                "role": "solid",
                "positions": rounded(at),
                "normals": rounded(normals),
                "uvs": rounded(uvs),
                "colors": [1.0, 1.0, 1.0, 0.0] * count,
                "indices": list(range(count)),
            }
        ],
        "textures": {"solid": TEXTURE_PATH},
    }
    (MODELS / f"{name}.prop-mesh.json").write_text(json.dumps(document, separators=(",", ":")) + "\n")
    print(f"{name}: {count // 3} triangles, {document['height']:.2f} m tall")


def texture():
    tile = Image.open(SOURCE_TEXTURE).convert("RGBA").resize((TILE_TEXELS, TILE_TEXELS), Image.LANCZOS)
    pixels = np.array(tile).astype(float) / 255
    for row in pixels:
        for pixel in row:
            hue, light, saturation = colorsys.rgb_to_hls(*pixel[:3])
            if GREEN_HUES[0] <= hue <= GREEN_HUES[1]:
                saturation *= GREEN_KEEPS
            pixel[:3] = colorsys.hls_to_rgb(hue, min(light * LIGHT, 1), saturation)
    Image.fromarray((pixels * 255).round().astype("uint8"), "RGBA").save(TEXTURE)


def main():
    texture()
    for name, seed, width, stretch in SCREE + BOULDERS:
        write(name, [rock(seed, width, stretch)], BURIED)
    for name, parts in OUTCROPS:
        write(name, [rock(*part) for part in parts], OUTCROP_BURIED)


if __name__ == "__main__":
    main()
