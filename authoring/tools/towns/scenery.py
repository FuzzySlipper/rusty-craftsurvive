"""Authored port/upriver arrangements and offline preview illumination."""

from __future__ import annotations

import math
import random

import bpy

from buildings import barrel, bench, lantern, roof
from geometry import beam, box, disk, empty, mesh, product_prop, rock


def pier(name, position, length=6, width=2.6, angle=0):
    root = empty(name, position, angle)
    count = math.ceil(length / 0.26)
    for i in range(count):
        box(
            "pier.plank",
            ((i + 0.5) * length / count, 0, 0.13),
            (length / count - 0.015, width, 0.12),
            "wood",
            root,
        )
    for x in (0.15, length / 2, length - 0.15):
        for y in (-width * 0.48, width * 0.48):
            box("pier.pile", (x, y, -0.13), (0.23, 0.23, 1.65), "wood", root)
            box("pier.pile.cap", (x, y, 0.74), (0.32, 0.32, 0.09), "wood", root)
    for y in (-width * 0.32, width * 0.32):
        beam("pier.bearer", (0, y, -0.02), (length, y, -0.02), 0.22, "wood", root)
    return root


def harbour_crane(position):
    root = empty("port.harbour.crane", position)
    box("crane.foot", (0, 0, 0.12), (1.25, 1.25, 0.3), "stone", root)
    beam("crane.mast", (0, 0, 0.25), (0, 0, 5.2), 0.32, "wood", root)
    beam("crane.boom", (-1.1, 0, 4.2), (3.2, 0, 4.9), 0.24, "wood", root)
    beam("crane.brace", (0.05, 0, 1.8), (2.2, 0, 4.7), 0.21, "wood", root)
    beam("crane.stay", (0, 0, 5.2), (-1.1, 0, 4.2), 0.045, "rope", root)
    beam("crane.hoist", (3.1, 0, 4.88), (3.1, 0, 1.9), 0.035, "rope", root)
    disk("crane.pulley", (3.05, 0, 4.76), 0.16, "iron", root, depth=0.13)
    beam("crane.hook", (3.1, 0, 1.95), (3.1, 0, 1.73), 0.065, "iron", root)
    beam("crane.hook.tip", (3.1, 0, 1.73), (3.26, 0, 1.73), 0.065, "iron", root)
    for x in (-0.75, 0.75):
        beam("crane.foot.brace", (x, 0, 0.2), (0, 0, 1.6), 0.17, "wood", root)
    return root


def ferry_shelter(position=(0, 0, 0), angle=0, lights=True):
    """The upriver landing's waiting shelter, with its own lamp."""
    root = empty("ferry.shelter", position, angle)
    for x in (-1.3, 1.3):
        for y in (-1.0, 1.0):
            beam("shelter.post", (x, y, 0.2), (x, y, 2.55), 0.15, "wood", root)
    roof(2.6, 2, 2.6, 0.9, root)
    bench("waiting.bench", (0, 0.75, 0.18), 2.1, root)
    lantern("ferry.lamp", (0, 0, 2.1), root, lights)
    return root


def quay_wall(position=(0, 0, 0), length=28):
    """A stone retaining wall and cap stones, running along local Y."""
    root = empty("quay.wall", position)
    box("quay.retaining.wall", (0, 0, -0.31), (1.0, length, 0.78), "stone", root)
    for y in range(-length // 2 + 1, length // 2, 2):
        box("quay.cap", (-0.05, y, 0.13), (1.35, 1.95, 0.22), "stone", root)
    return root


def market_canopy(position=(0, 0, 0), angle=0):
    """A foreground awning: an arrival under shelter without a new building type."""
    shelter = empty("foreground.market.canopy", position, angle)
    box("market.counter", (0, 0, 0.7), (3.5, 1.4, 0.18), "wood", shelter)
    canopy = mesh(
        "canvas",
        [(-2, -1.2, 3.6), (2, -1.2, 3.6), (2, 1.2, 3.9), (-2, 1.2, 3.9)],
        [(0, 1, 2, 3)],
        "cloth",
        shelter,
    )
    solid = canopy.modifiers.new("ClothThickness", "SOLIDIFY")
    solid.thickness = 0.025
    for x in (-1.8, 1.8):
        for y in (-1, 1):
            beam("market.post", (x, y, 0.1), (x, y, 3.65 + 0.12 * y), 0.15, "wood", shelter)
    for x in (-1.1, 0, 1.1):
        barrel("market.bundle", (x, 0, 0.8), shelter, 0.5)
    return shelter


def background_ridge(name, y_start, height, material):
    columns, rows = 71, 21
    vertices = []
    for row in range(rows):
        y = y_start + row * 7
        for column in range(columns):
            x = -180 + column * 6
            ridge = y_start + 55 + 13 * math.sin(x * 0.018)
            silhouette = height * (0.70 + 0.18 * math.sin(x * 0.024) + 0.12 * math.sin(x * 0.065))
            z = silhouette * math.exp(-(((y - ridge) / 34) ** 2)) - 2
            vertices.append((x, y, z))
    faces = [
        (r * columns + c, r * columns + c + 1, (r + 1) * columns + c + 1, (r + 1) * columns + c)
        for r in range(rows - 1)
        for c in range(columns - 1)
    ]
    obj = mesh(name, vertices, faces, material)
    for p in obj.data.polygons:
        p.use_smooth = True


def stage(scene_name, wet, tree_kind=None):
    upriver = scene_name == "upriver"
    rng = random.Random(9890)

    # Curving banks and a low water level replace the rectangular display slab.
    def bank(y):
        return 12.5 + 2.0 * math.sin(y * 0.053)

    columns, rows = 19, 55
    vertices = []
    for row in range(rows):
        y = -65 + row * 4
        for column in range(columns):
            t = column / (columns - 1)
            x = -90 + (bank(y) + 90) * t
            z = -0.07 if t < 0.95 else -0.65
            if y > 24:
                z += min((y - 24) * 0.06, 3) * max(0, 1 - (x / 30) ** 2)
            vertices.append((x, y, z))
    faces = [
        (r * columns + c, r * columns + c + 1, (r + 1) * columns + c + 1, (r + 1) * columns + c)
        for r in range(rows - 1)
        for c in range(columns - 1)
    ]
    terrain = mesh("town.bank", vertices, faces, "ground")
    for p in terrain.data.polygons:
        p.use_smooth = True
    far_x = 30 if upriver else 60
    box("opposite.bank", (far_x + 35, 45, -0.65), (70, 230, 1.1), "ground", bevel=0)
    columns, rows = 17, 65
    vertices = [
        (11 + c * (far_x - 10) / (columns - 1), -75 + r * 4, -0.62 + 0.025 * math.sin(c * 0.7 + r * 0.5))
        for r in range(rows)
        for c in range(columns)
    ]
    faces = [
        (r * columns + c, r * columns + c + 1, (r + 1) * columns + c + 1, (r + 1) * columns + c)
        for r in range(rows - 1)
        for c in range(columns - 1)
    ]
    water = mesh("river.surface", vertices, faces, "water")
    for p in water.data.polygons:
        p.use_smooth = True
    outline = [(-19, -6), (-14, -10), (-5, -12), (4, -10), (10, -5), (11, 8), (6, 17), (-8, 19), (-18, 12)]
    if upriver:
        outline = [(-16, -3), (-11, -7), (-5, -10), (3, -9), (10, -3), (9, 6), (2, 15), (-12, 15), (-18, 7)]
    mesh("worn.courtyard", [(x, y, 0.01) for x, y in outline], [tuple(range(len(outline)))], "paving")
    # Approach leads to the door; its edges are interrupted by plants and stones.
    vertices = []
    for i in range(9):
        y = -42 + i * 4.4
        centre = -3.5 + 2.6 * math.sin((i / 8) * math.pi * 0.5)
        for side in (-1, 1):
            vertices.append((centre + side * (2.1 + 0.14 * math.sin(i * 1.7)), y, 0.018))
    mesh("arrival.path", vertices, [(i * 2, i * 2 + 1, i * 2 + 3, i * 2 + 2) for i in range(8)], "paving")
    for i, (x, y) in enumerate(outline):
        # Individual edge stones and small planting clumps soften the paving's
        # boundary without increasing the whole ground's texture contrast.
        for j in range(3):
            stone = box(
                "courtyard.edge.stone",
                (x + j * 0.3, y + 0.1 * math.sin(j), 0.045),
                (0.28, 0.28, 0.12),
                "stone",
            )
            stone.rotation_euler.z = i * 0.41 + j * 0.08
    for i in range(24):
        y = -35 + i * 3.3
        rock(
            "river.stone",
            (bank(y) + rng.uniform(-0.2, 0.55), y, -0.35),
            (rng.uniform(0.35, 0.75), rng.uniform(0.55, 1.3), rng.uniform(0.3, 0.5)),
        )
    background_ridge("far.landforms", 75, 27 if upriver else 15, "distance")
    background_ridge("near.landforms", 36, 18 if upriver else 8, "middle-distance")
    # Existing trees establish scale without a new vegetation production task.
    locations = [
        (-18, 8),
        (-16, 20),
        (-11, 25),
        (-3, 28),
        (8, 25),
        (11, 36),
        (-21, 0),
        (-26, 18),
        (-15, 36),
        (0, 43),
    ]
    if upriver:
        locations += [(33, 15), (34, 28), (31, 45), (34, -1), (34, -12)]
    for i, (x, y) in enumerate(locations):
        kind = tree_kind or ("pine" if upriver else "oak")
        product_prop(
            "tree." + str(i),
            f"trees/{kind}-p1.prop-mesh.json",
            (x, y, 0),
            rng.uniform(0.95, 1.6),
            rng.uniform(0, 360),
        )
    for i in range(24):
        x, y = rng.choice([(-12, -8), (-8, -14), (5, -9), (10, 0), (-17, 6)])
        product_prop(
            "fern." + str(i),
            "scatter/fern-1.prop-mesh.json",
            (x + rng.uniform(-2, 2), y + rng.uniform(-3, 3), 0.02),
            rng.uniform(0.5, 1.1),
            rng.uniform(0, 360),
        )
    if upriver:
        pier("ferry.landing", (10, -0.8, 0), 7, 3.0)
        product_prop("moored.canoe", "canoe.prop-mesh.json", (17.2, 1.5, -0.49), angle=0)
        # Modest ferry shelter is the upriver landmark, assembled from kit parts.
        ferry_shelter((11.9, -0.8, 0), 90)
        beam("ferry.line", (12, 2, 2.8), (29, 2, 3.1), 0.025, "rope")
        barrel("landing.barrel", (10, 2.2, 0.08))
    else:
        # Open quay and loading equipment make the port read as a working port.
        quay_wall((11.5, 2, 0))
        pier("quay.landing", (11.6, 3, 0), 9, 3.3)
        harbour_crane((11.0, -4, 0.1))
        product_prop("moored.canoe", "canoe.prop-mesh.json", (20, 5, -0.48), scale=1.3, angle=0)
        for i, (x, y) in enumerate([(8, 4), (8.8, 4.6), (7.8, 5.2), (9, 9), (10, 10)]):
            barrel("cargo.barrel." + str(i), (x, y, 0.04), scale=0.95)
        # A foreground awning creates an arrival under shelter, without a new
        # building type. It frames the town while keeping the entrance visible.
        market_canopy((-12, -9, 0), -12)
    # Thin reeds break the shore outline. These are textured meshes, not a new
    # simulation or runtime particle effect.
    vertices, faces = [], []
    for i in range(110):
        y = rng.uniform(-18, 30)
        x = bank(y) + rng.uniform(-0.25, 0.4)
        z = -0.3
        h = rng.uniform(0.35, 0.95)
        n = len(vertices)
        vertices.extend([(x - 0.027, y, z), (x + 0.027, y, z), (x + 0.12, y, z + h)])
        faces.append((n, n + 1, n + 2))
    mesh("shore.reeds", vertices, faces, "reed")
    if wet:
        for i in range(95):
            x, y, z = rng.uniform(-16, 21), rng.uniform(-12, 20), rng.uniform(1, 12)
            beam("rain.study", (x, y, z), (x + 0.045, y + 0.01, z - 0.34), 0.003, "distance")


def offline_atmosphere(wet):
    """Render-only air; GLB exports happen before adding this Blender volume."""
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 100, 28))
    obj = bpy.context.object
    obj.name = "offline.atmosphere"
    obj.scale = (360, 160, 60)
    mat = bpy.data.materials.new("offline.haze")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    volume = nodes.new("ShaderNodeVolumePrincipled")
    volume.inputs["Density"].default_value = 0.006 if wet else 0.0035
    volume.inputs["Color"].default_value = (0.70, 0.78, 0.80, 1)
    out = nodes.new("ShaderNodeOutputMaterial")
    mat.node_tree.links.new(volume.outputs["Volume"], out.inputs["Volume"])
    obj.data.materials.append(mat)


def illumination(wet=False):
    world = bpy.data.worlds.new("TownStudySky")
    world.use_nodes = True
    bpy.context.scene.world = world
    background = world.node_tree.nodes.get("Background")
    background.inputs["Color"].default_value = (0.42, 0.55, 0.68, 1) if wet else (0.68, 0.78, 0.9, 1)
    background.inputs["Strength"].default_value = 0.38 if wet else 0.055
    if not wet:
        sky = world.node_tree.nodes.new("ShaderNodeTexSky")
        sky.sky_type = "MULTIPLE_SCATTERING"
        sky.sun_disc = False
        sky.sun_elevation = math.radians(25)
        sky.sun_rotation = math.radians(130)
        sky.aerosol_density = 1.3
        world.node_tree.links.new(sky.outputs["Color"], background.inputs["Color"])
    sun = bpy.data.lights.new("SoftSun", "SUN")
    sun.energy = 0.5 if wet else 2.3
    sun.angle = 0.4 if wet else 0.15
    sun.color = (0.72, 0.83, 1.0) if wet else (1.0, 0.88, 0.72)
    obj = bpy.data.objects.new("SoftSun", sun)
    bpy.context.collection.objects.link(obj)
    obj.rotation_euler = (math.radians(28), math.radians(-25), math.radians(-35))
    area = bpy.data.lights.new("SkyFill", "AREA")
    area.energy = 900 if wet else 1300
    area.shape = "DISK"
    area.size = 18
    obj = bpy.data.objects.new("SkyFill", area)
    bpy.context.collection.objects.link(obj)
    obj.location = (0, -8, 15)
