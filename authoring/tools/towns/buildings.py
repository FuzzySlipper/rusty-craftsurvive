"""A small shared architectural vocabulary, controlled by recipes.json."""

from __future__ import annotations

import json
import math
import random

import bpy

from geometry import DOOR_GROUND, WALL_THICKNESS, beam, box, disk, empty, mesh, rock


def wall(name, span, height, holes, parent, bottom=0.25):
    """Rectangular panels around explicit apertures; no painted-on door hole."""
    xs = sorted({-span / 2, span / 2, *(v for hole in holes for v in (hole[0], hole[1]))})
    zs = sorted({bottom, height, *(v for hole in holes for v in (hole[2], hole[3]))})
    for xi, (left, right) in enumerate(zip(xs, xs[1:])):
        for zi, (bottom, top) in enumerate(zip(zs, zs[1:])):
            x, z = (left + right) / 2, (bottom + top) / 2
            if any(h[0] < x < h[1] and h[2] < z < h[3] for h in holes):
                continue
            box(
                f"{name}.plaster.{xi}.{zi}",
                (x, 0, z),
                (right - left, WALL_THICKNESS, top - bottom),
                "plaster",
                parent,
                bevel=0,
            )


def window(name, x, z, width, height, parent, shutters=True):
    module = empty(name, (x, -0.03, z), parent=parent)
    box("recess.back", (0, 0.53, height / 2), (width, 0.06, height), "interior", module)
    box("window.glass", (0, 0.10, height / 2), (width - 0.13, 0.015, height - 0.15), "glass", module, bevel=0)
    # Depth and partial curtains keep windows from reading as identical orange
    # decals. A shaded luminous interior patch remains behind the glazing.
    box(
        "window.inner.light",
        (0, 0.48, height * 0.51),
        (width * 0.90, 0.02, height * 0.85),
        "window",
        module,
        bevel=0,
    )
    for side in (-1, 1):
        box(
            "curtain",
            (side * width * 0.36, 0.29, height * 0.52),
            (width * 0.23, 0.04, height * 0.86),
            "curtain",
            module,
        )
    for side in (-1, 1):
        box(
            "frame.jamb",
            (side * width / 2, -0.12, height / 2),
            (0.13, 0.25, height + 0.22),
            "painted-timber",
            module,
        )
        if shutters:
            shutter = box(
                "shutter",
                (side * (width / 2 + 0.23), -0.15, height / 2),
                (0.35, 0.09, height),
                "painted-timber",
                module,
            )
            shutter.rotation_euler.z = side * 0.16
    for level in (0, height):
        box("frame.rail", (0, -0.12, level), (width + 0.25, 0.3, 0.13), "painted-timber", module)
    box("sill", (0, -0.24, -0.06), (width + 0.48, 0.58, 0.11), "painted-timber", module)
    for xbar in (0,):
        box("mullion", (xbar, 0.08, height / 2), (0.045, 0.055, height), "wood", module, bevel=0)
    box("crossbar", (0, 0.06, height * 0.54), (width, 0.055, 0.045), "wood", module, bevel=0)
    for side in (-1, 1):
        beam(
            "upper.diamond",
            (side * width * 0.43, 0.055, height * 0.72),
            (0, 0.055, height * 0.95),
            0.025,
            "wood",
            module,
        )
    return module


def lantern(name, position, parent, lights):
    module = empty(name, position, parent=parent)
    box("glass", (0, 0, 0), (0.18, 0.18, 0.33), "window", module)
    for z in (-0.2, 0.2):
        box("cap", (0, 0, z), (0.29, 0.29, 0.075), "iron", module)
    for x in (-0.1, 0.1):
        for y in (-0.1, 0.1):
            box("cage", (x, y, 0), (0.025, 0.025, 0.42), "iron", module, bevel=0)
    if lights:
        data = bpy.data.lights.new(name + ".light", "POINT")
        data.energy = 55
        data.color = (1, 0.55, 0.2)
        data.shadow_soft_size = 0.35
        obj = bpy.data.objects.new(name + ".light", data)
        bpy.context.collection.objects.link(obj)
        obj.parent = module
        obj.location = (0, -0.2, 0)
    return module


def roof(width, depth, eaves, rise, parent):
    extent, reach = width / 2 + 0.6, depth / 2 + 0.65
    # A slight change of pitch at the eave gives the silhouette weight. UVs
    # run along the ridge/up the slope, so slate courses do not turn sideways.
    for sign in (-1, 1):
        profile = [(0, eaves + rise), (extent * 0.78, eaves + rise * 0.18), (extent, eaves - 0.08)]
        for j, ((x0, z0), (x1, z1)) in enumerate(zip(profile, profile[1:])):
            top = [
                (sign * x0, -reach, z0),
                (sign * x1, -reach, z1),
                (sign * x1, reach, z1),
                (sign * x0, reach, z0),
            ]
            if sign < 0:
                top.reverse()
            obj = mesh(
                "roof.slope",
                top + [(x, y, z - 0.14) for x, y, z in top],
                [(0, 1, 2, 3), (7, 6, 5, 4), (0, 4, 5, 1), (1, 5, 6, 2), (2, 6, 7, 3), (3, 7, 4, 0)],
                "roof",
                parent,
            )
            for loop in obj.data.loops:
                v = obj.data.vertices[loop.vertex_index].co
                obj.data.uv_layers.active.data[loop.index].uv = (v.y / 3, abs(v.x) / 3)
            for y in (-reach, reach):
                beam(
                    "roof.rake",
                    (sign * x0, y, z0 - 0.09),
                    (sign * x1, y, z1 - 0.09),
                    0.22,
                    "painted-timber",
                    parent,
                )
        beam(
            "roof.eave",
            (sign * extent, -reach, eaves - 0.12),
            (sign * extent, reach, eaves - 0.12),
            0.23,
            "painted-timber",
            parent,
        )
        # Short rafter tails give thickness at normal viewing distances.
        for i in range(math.ceil(depth / 0.7) + 1):
            y = -depth / 2 + i * depth / math.ceil(depth / 0.7)
            beam(
                "rafter.tail",
                (sign * (extent - 0.65), y, eaves + 0.12),
                (sign * extent, y, eaves - 0.17),
                0.13,
                "wood",
                parent,
            )
    beam(
        "roof.ridge",
        (0, -reach - 0.07, eaves + rise),
        (0, reach + 0.07, eaves + rise),
        0.21,
        "roof-edge",
        parent,
    )
    for y in (-depth / 2, depth / 2):
        mesh(
            "gable.plaster",
            [(-width / 2, y, eaves), (width / 2, y, eaves), (0, y, eaves + rise)],
            [(2, 1, 0)] if y < 0 else [(0, 1, 2)],
            "plaster",
            parent,
        )
        beam(
            "gable.kingpost",
            (0, y - 0.02, eaves),
            (0, y - 0.02, eaves + rise - 0.12),
            0.2,
            "painted-timber",
            parent,
        )
        for sign in (-1, 1):
            beam(
                "gable.brace",
                (sign * width * 0.35, y - 0.05, eaves + 0.12),
                (0, y - 0.05, eaves + rise * 0.72),
                0.13,
                "painted-timber",
                parent,
            )


def planter(name, position, width, parent):
    root = empty(name, position, parent=parent)
    box("planter.box", (0, 0, 0), (width, 0.34, 0.24), "wood", root)
    for x in (-width * 0.35, width * 0.35):
        beam("planter.bracket", (x, 0.18, -0.2), (x, -0.22, -0.04), 0.06, "iron", root)
    rng = random.Random(name)
    vertices, faces = [], []
    for i in range(45):
        x, y = rng.uniform(-width * 0.46, width * 0.46), rng.uniform(-0.2, 0.2)
        z = rng.uniform(0.1, 0.26)
        a = rng.uniform(0, math.tau)
        length = rng.uniform(0.09, 0.22)
        dx, dy = math.cos(a) * length, math.sin(a) * length
        start = len(vertices)
        vertices.extend(
            [
                (x - dx * 0.4, y - dy * 0.4, z),
                (x - dy * 0.4, y + dx * 0.4, z + 0.07),
                (x + dx, y + dy, z + 0.05),
                (x + dy * 0.4, y - dx * 0.4, z - 0.01),
            ]
        )
        faces.append(tuple(range(start, start + 4)))
    mesh("planter.leaves", vertices, faces, "leaf", root)
    for i in range(6):
        flower = rock("small.blossom", (0, 0, 0), (0.035, 0.035, 0.035), "flower")
        flower.parent = root
        flower.location = (rng.uniform(-width * 0.45, width * 0.45), rng.uniform(-0.12, 0.08), 0.26)


def barrel(name, position, parent=None, scale=1):
    root = empty(name, position, parent=parent)
    root.scale = (scale,) * 3
    segments = 12
    profile = [(0, 0.3), (0.08, 0.32), (0.45, 0.38), (0.85, 0.32), (0.92, 0.3)]
    vertices = [
        (math.cos(i * math.tau / segments) * r, math.sin(i * math.tau / segments) * r, z)
        for z, r in profile
        for i in range(segments)
    ]
    faces = [
        (
            i + j * segments,
            (i + 1) % segments + j * segments,
            (i + 1) % segments + (j + 1) * segments,
            i + (j + 1) * segments,
        )
        for j in range(len(profile) - 1)
        for i in range(segments)
    ]
    faces += [
        tuple(range(segments - 1, -1, -1)),
        tuple(range((len(profile) - 1) * segments, len(profile) * segments)),
    ]
    mesh("barrel.staves", vertices, faces, "wood", root)
    for z, r in ((0.12, 0.333), (0.72, 0.353)):
        vertices = [
            (math.cos(i * math.tau / segments) * r, math.sin(i * math.tau / segments) * r, h)
            for h in (z, z + 0.065)
            for i in range(segments)
        ]
        mesh(
            "barrel.hoop",
            vertices,
            [(i, (i + 1) % segments, (i + 1) % segments + segments, i + segments) for i in range(segments)],
            "iron",
            root,
        )
    return root


def bench(name, position, width, parent):
    root = empty(name, position, parent=parent)
    for y in (-0.18, 0, 0.18):
        box("bench.seat", (0, y, 0.54), (width, 0.16, 0.1), "wood", root)
    for x in (-width * 0.36, width * 0.36):
        box("bench.leg", (x, 0, 0.29), (0.14, 0.42, 0.5), "wood", root)
        beam("bench.back.post", (x, 0.23, 0.12), (x, 0.33, 1.05), 0.1, "wood", root)
    box("bench.back", (0, 0.31, 0.92), (width, 0.1, 0.2), "wood", root)


def service_sign(kind, position, parent):
    root = empty("service.sign." + kind, position, parent=parent)
    beam("sign.bracket", (0, 0.7, 0.4), (0, -0.22, 0.4), 0.065, "iron", root)
    beam("sign.brace", (0, 0.7, 0.07), (0, -0.1, 0.4), 0.035, "iron", root)
    for x in (-0.22, 0.22):
        beam("sign.chain", (x, -0.19, 0.4), (x, -0.19, 0.18), 0.022, "iron", root)
    box("sign.board", (0, -0.18, -0.2), (0.72, 0.1, 0.8), "wood", root)
    for x in (-0.36, 0.36):
        box("sign.edge", (x, -0.23, -0.2), (0.045, 0.03, 0.83), "painted-timber", root)
    if kind == "mug":
        box("mug.body", (-0.04, -0.247, -0.22), (0.27, 0.025, 0.34), "ceramic", root)
        for a, b in [
            ((0.10, -0.25, -0.08), (0.25, -0.25, -0.08)),
            ((0.25, -0.25, -0.08), (0.25, -0.25, -0.30)),
            ((0.25, -0.25, -0.30), (0.10, -0.25, -0.30)),
        ]:
            beam("mug.handle", a, b, 0.04, "ceramic", root)
        beam("mug.foam", (-0.19, -0.25, -0.03), (0.1, -0.25, -0.03), 0.055, "ceramic", root)
    else:
        beam("grain.stalk", (0, -0.25, -0.47), (0, -0.25, 0.09), 0.03, "ceramic", root)
        for side in (-1, 1):
            for z in (-0.25, -0.10, 0.03):
                beam(
                    "grain.ear", (0, -0.25, z - 0.08), (side * 0.17, -0.25, z + 0.05), 0.055, "ceramic", root
                )


def porch(recipe, root, lights):
    w, d, dh = recipe["width"], recipe["depth"], recipe["door_height"]
    reach = recipe["porch_depth"]
    kind = recipe["porch"]
    x0, x1 = (
        (-w / 2 - 0.18, w / 2 + 0.18) if kind != "side" else (-w / 2 - 0.1, -recipe["door_width"] / 2 - 0.3)
    )
    back, front = -d / 2 - 0.05, -d / 2 - reach
    high, low = dh + 0.6, dh + 0.12
    if kind == "gallery":
        slab = mesh(
            "porch.slate",
            [(x0, back, high), (x1, back, high), (x1, front, low), (x0, front, low)],
            [(0, 3, 2, 1)],
            "roof",
            root,
        )
        solid = slab.modifiers.new("RoofThickness", "SOLIDIFY")
        solid.thickness = 0.12
        box("porch.floor", ((x0 + x1) / 2, (front + back) / 2, 0.17), (x1 - x0, reach, 0.24), "wood", root)
    else:
        vertices = [
            (x, back - t * reach, high - (high - low) * t - 0.1 * math.sin(t * math.pi))
            for x in (x0, x1)
            for t in (0, 0.25, 0.5, 0.75, 1)
        ]
        slab = mesh("awning.cloth", vertices, [(i, i + 1, i + 6, i + 5) for i in range(4)], "cloth", root)
        solid = slab.modifiers.new("ClothThickness", "SOLIDIFY")
        solid.thickness = 0.018
        for i in range(math.ceil((x1 - x0) / 0.35)):
            x = x0 + (i + 0.5) * (x1 - x0) / math.ceil((x1 - x0) / 0.35)
            box(
                "awning.valance",
                (x, front, low - 0.07),
                ((x1 - x0) / math.ceil((x1 - x0) / 0.35) - 0.015, 0.03, 0.16),
                "cloth",
                root,
                bevel=0.025,
            )
    for x in (x0 + 0.18, x1 - 0.18):
        beam("porch.post", (x, front, 0.2), (x, front, low + 0.08), 0.17, "wood", root)
        sign = 1 if x < (x0 + x1) / 2 else -1
        beam("porch.brace", (x, front, low - 0.55), (x + sign * 0.5, front, low), 0.12, "wood", root)
    beam("porch.front.rail", (x0, front, low - 0.04), (x1, front, low - 0.04), 0.18, "painted-timber", root)
    if kind == "gallery":
        for side in (-1, 1):
            bench("porch.bench", (side * w * 0.31, back - 0.5, 0.22), 1.55, root)
            lantern("porch.lamp", (side * w * 0.30, front + 0.45, low - 0.38), root, lights)
        for x in (x0, x1):
            beam("porch.side.rail", (x, front, 0.95), (x, back, 0.95), 0.1, "wood", root)
    elif kind == "side":
        bench("sheltered.bench", ((x0 + x1) / 2, back - 0.35, 0), x1 - x0 - 0.25, root)
    else:
        for side in (-1, 1):
            display = empty("market.display", (side * w * 0.32, front + 0.55, 0), parent=root)
            box("market.table", (0, 0, 0.75), (1.65, 0.72, 0.12), "wood", display)
            for x in (-0.65, 0.65):
                box("market.leg", (x, 0, 0.4), (0.12, 0.55, 0.7), "wood", display)
            for x in (-0.52, 0, 0.52):
                box("produce.tray", (x, 0, 0.88), (0.46, 0.62, 0.17), "wood", display)
                for j in range(5):
                    obj = rock("produce", (0, 0, 0), (0.085, 0.08, 0.07), "fruit")
                    obj.parent = display
                    obj.location = (x + (j % 2 - 0.5) * 0.17, (j // 2 - 1) * 0.17, 1.0)


def building(name, recipe, position=(0, 0, 0), angle=0, lights=True):
    root = empty(name, position, angle)
    root["recipe"] = json.dumps(recipe, sort_keys=True)
    w, d, h, rise = (recipe[k] for k in ("width", "depth", "eaves", "roof_rise"))
    dw, dh = recipe["door_width"], recipe["door_height"]
    jetty = recipe["jetty"]
    split = 3.15
    upper_z, upper_h = h - 1.82, 1.3
    count = recipe["upper_windows"]
    low_z, low_h, low_w = 0.85, 1.38, 1.05
    upper_xs = ([-w * 0.22, w * 0.22] if count == 2 else [0]) if count else []
    low_xs = [-w * 0.33, w * 0.33]
    floors = (
        [(0.25, split, w, d, False), (split, h, w + 2 * jetty, d + 2 * jetty, True)]
        if count
        else [(0.25, h, w, d, False)]
    )
    for bottom, top, fw, fd, upper in floors:
        for side, span, point, rotation in [
            ("front", fw, (0, -fd / 2, 0), 0),
            ("back", fw, (0, fd / 2, 0), 180),
            ("right", fd, (fw / 2, 0, 0), 90),
            ("left", fd, (-fw / 2, 0, 0), -90),
        ]:
            parent = empty(name + "." + ("upper." if upper else "lower.") + side, point, rotation, root)
            wz, wh, ww = (upper_z, upper_h, 1.32) if upper else (low_z, low_h, low_w)
            xs = (upper_xs if upper else low_xs) if side == "front" else [0]
            holes = [(x - ww / 2, x + ww / 2, wz, wz + wh) for x in xs]
            if side == "front" and not upper:
                holes.append((-dw / 2, dw / 2, DOOR_GROUND, DOOR_GROUND + dh))
            wall(side, span, top, holes, parent, bottom=bottom)
            if not upper:
                box("foundation", (0, 0, 0.20), (span + 0.12, 0.40, 0.4), "stone", parent)
            for x in (-span / 2, span / 2):
                box(
                    "corner.post",
                    (x, -0.12, (bottom + top) / 2),
                    (0.24, 0.30, top - bottom + 0.14),
                    "painted-timber",
                    parent,
                )
            for z in (bottom + 0.08, top - 0.08):
                box("storey.rail", (0, -0.13, z), (span + 0.28, 0.30, 0.23), "painted-timber", parent)
            for i, x in enumerate(xs):
                window(side + ".window." + str(i), x, wz, ww, wh, parent, shutters=upper or side == "front")
                if upper and side == "front":
                    planter("window.planter." + str(i), (x, -0.4, wz - 0.24), 1.2, parent)
            if upper:
                for sign in (-1, 1):
                    beam(
                        "corner.brace",
                        (sign * (span / 2 - 0.65), -0.16, bottom + 0.12),
                        (sign * (span / 2 - 0.06), -0.16, bottom + 0.78),
                        0.13,
                        "painted-timber",
                        parent,
                    )
            elif side == "front":
                for x in (-dw / 2 - 0.12, dw / 2 + 0.12):
                    box(
                        "entrance.post",
                        (x, -0.18, dh / 2 + DOOR_GROUND),
                        (0.27, 0.36, dh + 0.20),
                        "painted-timber",
                        parent,
                    )
                for i in range(10):
                    x1, x2 = -dw / 2 + dw * i / 10, -dw / 2 + dw * (i + 1) / 10
                    curve = lambda x: dh + DOOR_GROUND + 0.1 + 0.10 * (1 - (x / (dw / 2)) ** 2)
                    beam(
                        "entrance.lintel",
                        (x1, -0.18, curve(x1)),
                        (x2, -0.18, curve(x2)),
                        0.22,
                        "painted-timber",
                        parent,
                    )
                for sign in (-1, 1):
                    leaf = empty("door.hinge", (sign * dw / 2, 0.04, DOOR_GROUND), -sign * 78, parent)
                    for i in range(4):
                        box(
                            "door.plank",
                            (-sign * (i + 0.5) * dw / 8, 0, dh / 2),
                            (dw / 8 - 0.012, 0.09, dh - 0.07),
                            "wood",
                            leaf,
                        )
                    for z in (0.38, dh - 0.38):
                        box(
                            "door.strap",
                            (-sign * dw / 4, -0.065, z),
                            (dw / 2 - 0.14, 0.025, 0.07),
                            "iron",
                            leaf,
                        )
                    lantern("entrance.lantern", (sign * (dw / 2 + 0.43), -0.4, dh + 0.05), parent, lights)
                if recipe["porch"] != "gallery":
                    service_sign(recipe["sign"], (dw / 2 + 0.74, -0.62, dh + 0.4), parent)
                if count:
                    for x in (-0.62, 0, 0.62):
                        disk("fascia.medallion", (x, -0.16, 3.00), 0.12, "ceramic", parent)
                        for sign in (-1, 1):
                            beam(
                                "tile.mark",
                                (x + sign * 0.065, -0.186, 2.965),
                                (x - sign * 0.065, -0.186, 3.035),
                                0.022,
                                "painted-timber",
                                parent,
                            )
    # The shallow room is a real volume visible through the doorway, with
    # modest furniture and warm practical lamps, not an interior image card.
    box("interior.floor", (0, 0, 0.23), (w - 0.1, d - 0.1, 0.15), "wood", root)
    box("interior.ceiling", (0, 0, 2.99), (w - 0.1, d - 0.1, 0.15), "wood", root)
    box("interior.backdrop", (0, d * 0.24, 1.6), (w - 0.35, 0.12, 2.8), "interior", root)
    for z in (0.85, 1.45, 2.05):
        box("shelf", (0, d * 0.24 - 0.22, z), (2.4, 0.4, 0.11), "wood", root)
        for x in (-0.9, -0.35, 0.5):
            barrel("shelf.jar", (x, d * 0.24 - 0.19, z + 0.06), root, scale=0.28)
    box("interior.table", (0, -0.05, 0.95), (1.65, 0.65, 0.11), "wood", root)
    for x in (-0.64, 0.64):
        for y in (-0.23, 0.23):
            beam("table.leg", (x, y, 0.3), (x, y, 0.92), 0.1, "wood", root)
    bench("interior.seat", (0, 0.65, 0.02), 1.4, root)
    lantern("interior.lamp", (0, 0.5, 2.3), root, lights)
    lantern("interior.side.lamp", (-1.5, -0.25, 1.8), root, lights)
    for i in range(2):
        box(
            "entrance.step",
            (0, -d / 2 - 0.35 - i * 0.32, 0.14 - i * 0.07),
            (dw + 1.0 + i * 0.3, 0.7, 0.14),
            "stone",
            root,
        )
    roof(w + 2 * jetty, d + 2 * jetty, h, rise, root)
    box("chimney.stack", (w * 0.28, d * 0.20, h + rise * 0.72), (0.7, 0.74, 1.9), "stone", root)
    box("chimney.cap", (w * 0.28, d * 0.20, h + rise * 0.72 + 0.99), (0.96, 0.98, 0.16), "stone", root)
    box(
        "chimney.dark.opening",
        (w * 0.28, d * 0.20, h + rise * 0.72 + 1.08),
        (0.49, 0.50, 0.035),
        "iron",
        root,
        bevel=0,
    )
    for y in (-d / 2, d / 2):
        for sign in (-1, 1):
            beam(
                "eave.brace",
                (sign * (w / 2 - 0.55), y, h - 0.1),
                (sign * w / 2, y, h - 0.66),
                0.16,
                "painted-timber",
                root,
            )
    porch(recipe, root, lights)
    if recipe["porch"] == "gallery":
        service_sign(recipe["sign"], (w / 2 - 0.34, -d / 2 - recipe["porch_depth"] - 0.15, 2.30), root)
    barrel("rain.barrel", (w / 2 + 0.5, -d / 2 + 0.3, 0.03), root)
    if recipe["lean_to"]:
        x0, x1 = w / 2 + 0.05, w / 2 + 1.8
        top = mesh(
            "woodshed.roof",
            [(x0, -d * 0.35, 3.1), (x1, -d * 0.35, 2.45), (x1, d * 0.35, 2.45), (x0, d * 0.35, 3.1)],
            [(0, 1, 2, 3)],
            "roof",
            root,
        )
        solid = top.modifiers.new("RoofThickness", "SOLIDIFY")
        solid.thickness = 0.13
        for y in (-d * 0.35, d * 0.35):
            beam("woodshed.post", (x1, y, 0.1), (x1, y, 2.5), 0.15, "wood", root)
        for row in range(4):
            for i in range(6 - row % 2):
                beam(
                    "stacked.firewood",
                    (x0 + 0.3 + i * 0.21, -0.2, 0.2 + row * 0.18),
                    (x0 + 0.3 + i * 0.21, 0.8, 0.2 + row * 0.18),
                    0.17,
                    "wood",
                    root,
                )
    return root
