"""Grows a ruff of large frills around a prepared body's neck, each on its own bone (#10882, the plume-neck).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/add_ruff.py -- \
           <body.glb> <body-64.png> <species.json> <ruffed.glb> <ruffed-64.png> <ruffed-species.json>

The plume-neck flares a wide crimson ruff before it attacks. Tripo makes a mane lying along the neck, which cannot
open, so the ruff is grown here from the species' `"ruff"` block: `count` leaf-shaped frills, each a solid serrated
leaf `length` long, `width` at its widest and `thickness` thick, rooted in a ring `radius` from the neck's axis at
`centre` ([forward, up]), spread around it from `fromDegrees` to `toDegrees` (0 is the top of the neck, positive the
animal's left). Closed, each lies back along the neck's line `slopeDegrees` below level and `splayDegrees` out from it,
its broad face to the outside.

It writes `<ruffed-species.json>`: the species with a point pair `ruff<k>` / `ruff<k>_tip` for each frill, so the
family gives each its own bone, and with each frill's angle around the ring as `ruff.angles`, so a clip swings it
outward along its own radius. The paint grows a strip of `STRIP_ROWS` rows along its bottom (the body's UVs move up to
keep every texel where it was): the frills' edges take `edgeColour`. Their broad faces are mapped onto the largest
square of the body's own paint that is all of the mane's crimson (`colour`, within `colourWithin` in each channel), so
they carry the mane's own texels rather than a flat colour.
"""
import json
import math
import sys
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

STRIP_ROWS = 2
# Texel columns of the strip: a frill's broad faces, then its edges.
FACE_COLUMN, EDGE_COLUMN = 0, 1
# The leaf's outline: (share of its length, share of its width) along one side, base to tip; teeth on the edge.
OUTLINE = ((0.0, 0.25), (0.15, 0.7), (0.35, 1.0), (0.45, 0.8), (0.55, 0.95), (0.65, 0.65), (0.75, 0.75), (0.85, 0.4),
           (0.92, 0.45), (1.0, 0.0))


def body_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    return meshes[0]


def widen_paint(paint_path, out_path, face, edge):
    """The paint with the strip added along its bottom; returns (width, old height, new height)."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    old = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)
    wider = np.zeros((height + STRIP_ROWS, width, 4), dtype=np.float32)
    wider[..., 3] = 1.0
    wider[STRIP_ROWS:] = old
    wider[:STRIP_ROWS, :, :3] = np.array(edge, dtype=np.float32) / 255.0
    wider[:STRIP_ROWS, FACE_COLUMN, :3] = np.array(face, dtype=np.float32) / 255.0
    out = bpy.data.images.new("ruffed", width, height + STRIP_ROWS, alpha=False)
    out.pixels[:] = wider.ravel()
    out.filepath_raw = str(Path(out_path).resolve())
    out.file_format = "PNG"
    out.save()
    return width, height, height + STRIP_ROWS


def crimson_square(paint_path, colour, within):
    """The largest square of the paint whose texels are all within `within` of `colour` in each channel: (u0, v0, size)
    in UV units, v from the bottom as Blender has it."""
    paint = bpy.data.images.load(str(Path(paint_path).resolve()))
    width, height = paint.size
    pixels = np.array(paint.pixels[:], dtype=np.float32).reshape(height, width, 4)[..., :3] * 255.0
    near = np.all(np.abs(pixels - np.array(colour, dtype=np.float32)) <= within, axis=2)
    best = np.zeros((height + 1, width + 1), dtype=np.int32)
    size, at = 0, (0, 0)
    for y in range(height):
        for x in range(width):
            if near[y, x]:
                best[y + 1, x + 1] = 1 + min(best[y, x], best[y + 1, x], best[y, x + 1])
                if best[y + 1, x + 1] > size:
                    size, at = best[y + 1, x + 1], (y, x)
    if size < 4:
        raise SystemExit(f"ruff: no square of the mane's colour in the paint (largest {size} texels)")
    y, x = at
    return (x - size + 1) / width, (y - size + 1) / height, size, width, height


def leaf(data, uv_layer, texels, base, along, across, out, length, width, thickness, square):
    """One frill: the outline swept along `along` and `across`, with `out` the side its broad face shows."""
    def ring(sign):
        side = [(base + along * (length * a) + across * (sign * width * 0.5 * w)) for a, w in OUTLINE]
        return side

    left, right = ring(1.0), ring(-1.0)
    outline = left + right[-2:0:-1]
    top = [data.verts.new(p + out * (thickness * 0.5)) for p in outline]
    bottom = [data.verts.new(p - out * (thickness * 0.5)) for p in outline]

    def face(verts, texel):
        made = data.faces.new(verts)
        for loop in made.loops:
            loop[uv_layer].uv = texel

    def painted(verts, shares):
        made = data.faces.new(verts)
        for loop, (a, w) in zip(made.loops, shares):
            loop[uv_layer].uv = Vector((u0 + span * (0.5 + 0.5 * w), v0 + span * a))

    u0, v0, span = square
    shares = [(a, w) for a, w in OUTLINE] + [(a, -w) for a, w in OUTLINE[-2:0:-1]]
    painted(top, shares)
    painted(list(reversed(bottom)), list(reversed(shares)))
    count = len(outline)
    for i in range(count):
        j = (i + 1) % count
        face([top[i], bottom[i], bottom[j], top[j]], texels[EDGE_COLUMN])


def to_blender(point):
    side, forward, up = point
    return Vector((side, -forward, up))


def main(argv):
    body_path, paint_path, species_path, out_glb, out_paint, out_species = argv[:6]
    species = json.loads(Path(species_path).read_text())
    ruff = species["ruff"]
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    mesh = body_mesh(body_path)
    u0, v0, size, paint_width, paint_height = crimson_square(paint_path, ruff["colour"], ruff["colourWithin"])
    width, old_height, height = widen_paint(paint_path, out_paint, ruff["colour"], ruff["edgeColour"])
    # The square, moved up with the rest of the paint by the strip.
    square = (u0, (v0 * old_height + STRIP_ROWS) / height, size / width)
    uv = mesh.data.uv_layers.active.data
    for loop in uv:
        loop.uv = Vector((loop.uv.x, (loop.uv.y * old_height + STRIP_ROWS) / height))
    texels = [Vector(((column + 0.5) / width, 0.5 / height)) for column in (FACE_COLUMN, EDGE_COLUMN)]

    data = bmesh.new()
    data.from_mesh(mesh.data)
    uv_layer = data.loops.layers.uv.active
    count = ruff["count"]
    forward, up = ruff["centre"]
    centre = to_blender((0.0, forward, up))
    # Back along the neck: Blender +Y is backward; tipped down by the slope.
    slope = math.radians(ruff["slopeDegrees"])
    back = Vector((0.0, math.cos(slope), -math.sin(slope)))
    points, angles = {}, []
    for k in range(count):
        share = k / (count - 1) if count > 1 else 0.5
        angle = ruff["fromDegrees"] + (ruff["toDegrees"] - ruff["fromDegrees"]) * share
        theta = math.radians(angle)
        radial = Vector((math.sin(theta), 0.0, math.cos(theta)))
        base = centre + radial * ruff["radius"]
        splay = Matrix.Rotation(math.radians(ruff["splayDegrees"]), 3, back.cross(radial).normalized())
        along = (splay @ back).normalized()
        across = along.cross(radial).normalized()
        out = across.cross(along).normalized()
        leaf(data, uv_layer, texels, base, along, across, out, ruff["length"], ruff["width"], ruff["thickness"], square)
        tip = base + along * ruff["length"]
        points[f"ruff{k + 1}"] = [round(base.x, 4), round(-base.y, 4), round(base.z, 4)]
        points[f"ruff{k + 1}_tip"] = [round(tip.x, 4), round(-tip.y, 4), round(tip.z, 4)]
        angles.append(round(angle, 2))
    data.normal_update()
    data.to_mesh(mesh.data)
    data.free()

    image = bpy.data.images.load(str(Path(out_paint).resolve()))
    for material in [m for m in mesh.data.materials if m]:
        for node in material.node_tree.nodes:
            if node.type == "TEX_IMAGE":
                node.image = image
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.ops.export_scene.gltf(filepath=str(Path(out_glb).resolve()), export_format="GLB", use_selection=True,
                              export_yup=True, export_materials="EXPORT", export_image_format="AUTO")
    species["points"].update(points)
    species["ruff"]["angles"] = angles
    Path(out_species).write_text(json.dumps(species, indent=2) + "\n")
    triangles = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    print(f"ruff: {count} frills of {ruff['length']:.2f} m on a {size}-texel square of the mane's paint; {triangles} triangles;"
          f" paint {width}x{height}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
