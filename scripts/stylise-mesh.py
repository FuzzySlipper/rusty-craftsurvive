"""Stylise a generated GLB into a CraftSurvive prop mesh (#9664). Blender, run headless.

Local generators (asset-pipeline TRELLIS.2, Tripo) return dense, softly realistic textured meshes.
This makes a faceted game prop with economical colour:

1. join the meshes, stand the model on its base at the origin and scale it to --height metres;
2. decimate to about --faces triangles (collapse);
3. colour each triangle from the base-colour texture (mean of its corners and centroid), or the
   material colour when untextured;
4. split triangles into parts by role: with --foliage, a triangle whose colour is green enough
   (hue in --leaf-hue, saturation above --leaf-saturation) is "leaves", everything else "bark";
   without it every triangle is "solid";
5. per part, pull colours toward palette ramps (content/style/palette.json) in Lab and group them
   into --colors colours (k-means, fixed seed), for flat faceted colour fields;
6. per vertex wind weight in colour alpha: bark 0; leaves rise from --leaf-root-weight at the
   trunk axis to 1 at the canopy's edge, so the Engine's flutter moves the outer leaves most.

Output is the product prop-mesh JSON read by CraftSurvive (Y up, metres, unshared triangles with
face normals and RGBA vertex colours):
    {"name", "height", "bounds": {"min", "max"}, "parts": [{"role", "positions", "normals", "colors", "indices"}]}
A preview PNG (front, three-quarter, top) is rendered next to it unless --no-preview.

    blender -b --python scripts/stylise-mesh.py -- SOURCE.glb OUT.prop-mesh.json --height 9 \
        --faces 2500 --foliage --pull-leaves foliage --pull-bark bark --colors 6
"""
import argparse
import colorsys
import json
import math
import sys
from pathlib import Path

import bpy
import bmesh
from mathutils import Vector

PALETTE = Path(__file__).resolve().parent.parent / "content" / "style" / "palette.json"
KMEANS_SEED = 9664
KMEANS_ITERATIONS = 20
PREVIEW_SIZE = 512
DECIMALS = 4


def arguments():
    argv = sys.argv[sys.argv.index("--") + 1:]
    parser = argparse.ArgumentParser(prog="stylise-mesh.py")
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--height", type=float, required=True, help="model height in metres")
    parser.add_argument("--faces", type=int, default=2500)
    parser.add_argument("--foliage", action="store_true", help="split leaves from bark by colour")
    parser.add_argument("--leaf-hue", default="55-170", help="leaf hue range in degrees")
    parser.add_argument("--leaf-saturation", type=float, default=0.18)
    parser.add_argument("--leaf-root-weight", type=float, default=0.35)
    parser.add_argument("--pull-leaves", default="foliage")
    parser.add_argument("--pull-bark", default="bark")
    parser.add_argument("--pull-solid", default="")
    parser.add_argument("--pull-strength", type=float, default=0.6)
    parser.add_argument("--colors", type=int, default=6, help="colours per part (0 = keep)")
    parser.add_argument("--saturation", type=float, default=1.0)
    parser.add_argument("--brightness", type=float, default=1.0)
    parser.add_argument("--yaw", type=float, default=0.0, help="turn about the vertical axis, degrees")
    parser.add_argument("--no-preview", action="store_true")
    return parser.parse_args(argv)


def hex_rgb(value):
    value = value.lstrip("#")
    return [int(value[i:i + 2], 16) / 255 for i in (0, 2, 4)]


def srgb_to_linear(c):
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def lab(rgb):
    r, g, b = (srgb_to_linear(c) for c in rgb)
    x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047
    y = 0.2126 * r + 0.7152 * g + 0.0722 * b
    z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883
    f = [t ** (1 / 3) if t > 0.008856 else 7.787 * t + 16 / 116 for t in (x, y, z)]
    return (116 * f[1] - 16, 500 * (f[0] - f[1]), 200 * (f[1] - f[2]))


def distance2(a, b):
    return sum((p - q) ** 2 for p, q in zip(a, b))


def pull(colours, ramps, strength):
    if not ramps or strength <= 0:
        return colours
    palette = [hex_rgb(c) for c in ramps]
    palette_lab = [lab(c) for c in palette]
    out = []
    for colour in colours:
        target = palette[min(range(len(palette)), key=lambda i: distance2(lab(colour), palette_lab[i]))]
        out.append([c + (t - c) * strength for c, t in zip(colour, target)])
    return out


def group(colours, k):
    """Deterministic k-means over face colours; returns the grouped colour per face."""
    if k <= 0 or len(colours) <= k:
        return colours
    # Farthest-point seeding from a fixed start keeps it deterministic without a random generator.
    centres = [colours[KMEANS_SEED % len(colours)]]
    while len(centres) < k:
        centres.append(max(colours, key=lambda c: min(distance2(c, m) for m in centres)))
    labels = [0] * len(colours)
    for _ in range(KMEANS_ITERATIONS):
        labels = [min(range(k), key=lambda j: distance2(c, centres[j])) for c in colours]
        for j in range(k):
            members = [c for c, l in zip(colours, labels) if l == j]
            if members:
                centres[j] = [sum(m[i] for m in members) / len(members) for i in range(3)]
    return [centres[l] for l in labels]


def base_image(material):
    if material is None or not material.use_nodes:
        return None
    for node in material.node_tree.nodes:
        if node.type == "BSDF_PRINCIPLED":
            links = node.inputs["Base Color"].links
            if links and links[0].from_node.type == "TEX_IMAGE":
                return links[0].from_node.image
    return None


def load(args):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=args.source)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    model = bpy.context.view_layer.objects.active
    for o in list(bpy.context.scene.objects):
        if o is not model:
            bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    corners = [model.matrix_world @ Vector(c) for c in model.bound_box]
    low = Vector((min(c.x for c in corners), min(c.y for c in corners), min(c.z for c in corners)))
    high = Vector((max(c.x for c in corners), max(c.y for c in corners), max(c.z for c in corners)))
    model.location -= Vector(((low.x + high.x) / 2, (low.y + high.y) / 2, low.z))
    bpy.ops.object.transform_apply(location=True)
    model.rotation_euler = (0, 0, math.radians(args.yaw))
    model.scale = (args.height / (high.z - low.z),) * 3
    bpy.ops.object.transform_apply(rotation=True, scale=True)
    triangles = sum(len(p.vertices) - 2 for p in model.data.polygons)
    if triangles > args.faces:
        modifier = model.modifiers.new("decimate", "DECIMATE")
        modifier.decimate_type = "COLLAPSE"
        modifier.ratio = args.faces / triangles
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    return model


def faces(model, args):
    images = {}
    for index, slot in enumerate(model.material_slots):
        image = base_image(slot.material)
        if image is not None:
            images[index] = (image.size[0], image.size[1], list(image.pixels[:]))
    mesh = model.data
    mesh.calc_loop_triangles()
    uv = mesh.uv_layers.active.data if mesh.uv_layers.active else None

    def sample(slot, u, v):
        width, height, pixels = images[slot]
        x = min(width - 1, max(0, int((u % 1.0) * width)))
        y = min(height - 1, max(0, int((v % 1.0) * height)))
        i = (y * width + x) * 4
        return pixels[i:i + 3]

    result = []
    for triangle in mesh.loop_triangles:
        slot = triangle.material_index
        material = model.material_slots[slot].material if model.material_slots else None
        if uv is not None and slot in images:
            uvs = [uv[loop].uv for loop in triangle.loops]
            points = uvs + [sum((Vector(p) for p in uvs), Vector((0, 0))) / 3]
            samples = [sample(slot, p[0], p[1]) for p in points]
            # glTF textures are sRGB; Blender's pixels are already the stored values.
            rgb = [sum(s[i] for s in samples) / len(samples) for i in range(3)]
        else:
            rgb = list(material.diffuse_color[:3]) if material else [0.6, 0.6, 0.6]
        rgb = [min(1.0, c * args.brightness) for c in rgb]
        grey = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2]
        rgb = [min(1.0, max(0.0, grey + (c - grey) * args.saturation)) for c in rgb]
        corners = [mesh.vertices[v].co.copy() for v in triangle.vertices]
        result.append({"corners": corners, "normal": triangle.normal.copy(), "rgb": rgb})
    return result


def classify(face, args):
    if not args.foliage:
        return "solid"
    low, high = (float(v) for v in args.leaf_hue.split("-"))
    h, s, v = colorsys.rgb_to_hsv(*face["rgb"])
    return "leaves" if low <= h * 360 <= high and s >= args.leaf_saturation else "bark"


def build(model, args):
    ramps = json.loads(PALETTE.read_text())["ramps"]
    pulls = {"leaves": args.pull_leaves, "bark": args.pull_bark, "solid": args.pull_solid}
    all_faces = faces(model, args)
    by_role = {}
    for face in all_faces:
        by_role.setdefault(classify(face, args), []).append(face)
    radius = max((math.hypot(c.x, c.y) for f in all_faces for c in f["corners"]), default=1.0) or 1.0
    parts = []
    low = [math.inf] * 3
    high = [-math.inf] * 3
    for role in ("bark", "leaves", "solid"):
        members = by_role.get(role)
        if not members:
            continue
        names = [n for n in pulls[role].split(",") if n]
        palette = [c for n in names for c in ramps[n]]
        colours = group(pull([f["rgb"] for f in members], palette, args.pull_strength), args.colors)
        positions, normals, colors, indices = [], [], [], []
        for face, rgb in zip(members, colours):
            n = face["normal"]
            for corner in face["corners"]:
                # Blender Z up to Engine Y up.
                p = (corner.x, corner.z, -corner.y)
                for axis in range(3):
                    low[axis] = min(low[axis], p[axis])
                    high[axis] = max(high[axis], p[axis])
                weight = 0.0
                if role == "leaves":
                    outward = math.hypot(corner.x, corner.y) / radius
                    weight = args.leaf_root_weight + (1 - args.leaf_root_weight) * min(1.0, outward)
                indices.append(len(positions) // 3)
                positions.extend(round(c, DECIMALS) for c in p)
                normals.extend(round(c, DECIMALS) for c in (n.x, n.z, -n.y))
                colors.extend([round(c, DECIMALS) for c in rgb] + [round(weight, DECIMALS)])
        parts.append({"role": role, "positions": positions, "normals": normals, "colors": colors, "indices": indices})
    return {"name": Path(args.output).name.split(".")[0], "height": args.height,
            "bounds": {"min": [round(v, DECIMALS) for v in low], "max": [round(v, DECIMALS) for v in high]},
            "parts": parts}


def preview(asset, path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    mesh = bpy.data.meshes.new("preview")
    verts, polys, cols = [], [], []
    for part in asset["parts"]:
        p, c = part["positions"], part["colors"]
        base = len(verts)
        for i in range(0, len(p), 3):
            verts.append((p[i], -p[i + 2], p[i + 1]))
        for i in range(0, len(part["indices"]), 3):
            polys.append(tuple(base + j for j in part["indices"][i:i + 3]))
        for i in range(0, len(c), 4):
            cols.append(c[i:i + 3] + [1.0])
    mesh.from_pydata(verts, [], polys)
    attribute = mesh.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
    for i, colour in enumerate(cols):
        attribute.data[i].color = colour
    obj = bpy.data.objects.new("preview", mesh)
    bpy.context.scene.collection.objects.link(obj)
    scene = bpy.context.scene
    scene.render.engine = "BLENDER_WORKBENCH"
    scene.display.shading.light = "STUDIO"
    scene.display.shading.color_type = "VERTEX"
    scene.render.resolution_x = scene.render.resolution_y = PREVIEW_SIZE
    scene.render.film_transparent = True
    height = asset["height"]
    camera_data = bpy.data.cameras.new("camera")
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = max(height, max(asset["bounds"]["max"][0] - asset["bounds"]["min"][0],
                                               asset["bounds"]["max"][2] - asset["bounds"]["min"][2])) * 1.15
    camera = bpy.data.objects.new("camera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    frames = []
    for name, angle, pitch in (("front", 0, 0), ("three-quarter", 45, 20), ("top", 0, 89)):
        yaw, tilt = math.radians(angle), math.radians(pitch)
        distance = height * 4
        target = Vector((0, 0, height / 2))
        camera.location = target + Vector((math.sin(yaw) * math.cos(tilt), -math.cos(yaw) * math.cos(tilt), math.sin(tilt))) * distance
        camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
        frame = str(path.with_name(path.stem + f"-{name}.png"))
        scene.render.filepath = frame
        bpy.ops.render.render(write_still=True)
        frames.append(frame)
    sheet = bpy.data.images.new("sheet", PREVIEW_SIZE * len(frames), PREVIEW_SIZE, alpha=True)
    pixels = [0.0] * (PREVIEW_SIZE * len(frames) * PREVIEW_SIZE * 4)
    for k, frame in enumerate(frames):
        image = bpy.data.images.load(frame)
        source = list(image.pixels[:])
        for y in range(PREVIEW_SIZE):
            row = (y * PREVIEW_SIZE * len(frames) + k * PREVIEW_SIZE) * 4
            pixels[row:row + PREVIEW_SIZE * 4] = source[y * PREVIEW_SIZE * 4:(y + 1) * PREVIEW_SIZE * 4]
        Path(frame).unlink()
    sheet.pixels = pixels
    sheet.filepath_raw = str(path)
    sheet.file_format = "PNG"
    sheet.save()


def main():
    args = arguments()
    model = load(args)
    asset = build(model, args)
    output = Path(args.output)
    output.write_text(json.dumps(asset, separators=(",", ":")))
    counts = ", ".join(f"{p['role']} {len(p['indices']) // 3}" for p in asset["parts"])
    print(f"stylise-mesh {output.name}: {counts} triangles; args: {' '.join(sys.argv[sys.argv.index('--') + 1:])}")
    if not args.no_preview:
        preview(asset, output.with_suffix("").with_suffix(".preview.png"))


main()
