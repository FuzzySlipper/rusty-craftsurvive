"""Two alternate silhouettes inside the existing quay/landing footprints."""

import math

from buildings import bench, lantern, roof
from geometry import beam, box, empty, mesh


def beacon(position=(11.0, -4, 0.1), angle=0):
    root = empty("port.beacon", position, angle)
    box("beacon.plinth", (0, 0, 0.18), (1.35, 1.35, 0.36), "stone", root)
    box("beacon.column", (0, 0, 1.9), (0.7, 0.7, 3.3), "stone", root)
    for x in (-0.45, 0.45):
        for y in (-0.45, 0.45):
            beam("beacon.frame", (x, y, 3.4), (x, y, 4.7), 0.11, "wood", root)
    lantern("beacon.lantern", (0, 0, 4.05), root, False)
    roof(1.2, 1.2, 4.7, 0.55, root)
    return root


def landing_bell(position=(11.9, -0.8, 0), angle=90):
    root = empty("ferry.bell", position, angle)
    for x in (-0.85, 0.85):
        for y in (-0.65, 0.65):
            beam("bell.post", (x, y, 0.15), (x, y, 3.65), 0.16, "wood", root)
    roof(1.8, 1.5, 3.7, 0.65, root)
    beam("bell.rope", (0, 0, 3.7), (0, 0, 3.15), 0.045, "rope", root)
    rings = [(0.12, 3.2), (0.22, 2.95), (0.28, 2.65), (0.43, 2.5)]
    sides = 12
    vertices = [
        (r * math.cos(i * math.tau / sides), r * math.sin(i * math.tau / sides), z)
        for r, z in rings
        for i in range(sides)
    ]
    faces = [
        (
            row * sides + i,
            (row + 1) * sides + i,
            (row + 1) * sides + (i + 1) % sides,
            row * sides + (i + 1) % sides,
        )
        for row in range(len(rings) - 1)
        for i in range(sides)
    ]
    mesh("landing.bell.shell", vertices, faces, "ceramic", root)
    beam("bell.clapper", (0, 0, 2.9), (0, 0, 2.45), 0.07, "iron", root)
    bench("bell.bench", (0, 0.7, 0.18), 1.5, root)
    return root
