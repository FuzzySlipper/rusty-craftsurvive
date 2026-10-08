"""Render a textured prop mesh (scripts/generate-tree.py output) for a quick look. Blender, headless.

Front, three-quarter and from-below views on a sky-grey background in EEVEE, with the prop's
textures nearest-filtered and the leaves alpha-cut, as the game draws them. Not a substitute for
judging the prop in the game; it is for iterating on shape and texture.

    blender -b --python scripts/preview-prop-mesh.py -- content/game/models/trees/oak-p1.prop-mesh.json OUT.png
"""
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parent.parent
CONTENT = ROOT / "content" / "game"
SIZE = 512
BACKGROUND = (0.62, 0.72, 0.8, 1.0)
VIEWS = (("front", 0, 5), ("three-quarter", 40, 18), ("below", 20, -25))


def material(name, texture, cutout):
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    shader = nodes["Principled BSDF"]
    shader.inputs["Roughness"].default_value = 0.85
    image = nodes.new("ShaderNodeTexImage")
    image.image = bpy.data.images.load(str(CONTENT / texture))
    image.interpolation = "Closest"
    colour = nodes.new("ShaderNodeVertexColor")
    colour.layer_name = "Col"
    multiply = nodes.new("ShaderNodeMixRGB")
    multiply.blend_type = "MULTIPLY"
    multiply.inputs["Fac"].default_value = 1.0
    links.new(image.outputs["Color"], multiply.inputs[1])
    links.new(colour.outputs["Color"], multiply.inputs[2])
    links.new(multiply.outputs["Color"], shader.inputs["Base Color"])
    if cutout:
        links.new(image.outputs["Alpha"], shader.inputs["Alpha"])
        if hasattr(mat, "blend_method"):
            mat.blend_method = "CLIP"
        if hasattr(mat, "surface_render_method"):
            mat.surface_render_method = "DITHERED"
        mat.use_backface_culling = False
    return mat


def main():
    source, output = sys.argv[sys.argv.index("--") + 1:][:2]
    asset = json.loads(Path(source).read_text())
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    for part in asset["parts"]:
        p, n, uv, c = part["positions"], part["normals"], part["uvs"], part["colors"]
        verts = [(p[i], -p[i + 2], p[i + 1]) for i in range(0, len(p), 3)]
        faces = [tuple(part["indices"][i:i + 3]) for i in range(0, len(part["indices"]), 3)]
        mesh = bpy.data.meshes.new(part["role"])
        mesh.from_pydata(verts, [], faces)
        layer = mesh.uv_layers.new(name="UV")
        cols = mesh.color_attributes.new("Col", "FLOAT_COLOR", "POINT")
        for i in range(len(verts)):
            cols.data[i].color = (c[4 * i], c[4 * i + 1], c[4 * i + 2], 1.0)
        for loop in mesh.loops:
            vi = loop.vertex_index
            layer.data[loop.index].uv = (uv[2 * vi], 1 - uv[2 * vi + 1])
        mesh.normals_split_custom_set_from_vertices([(n[3 * i], -n[3 * i + 2], n[3 * i + 1]) for i in range(len(verts))])
        mesh.materials.append(material(part["role"], asset["textures"][part["role"]], part["role"] == "leaves"))
        obj = bpy.data.objects.new(part["role"], mesh)
        scene.collection.objects.link(obj)
    world = bpy.data.worlds.new("sky")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = BACKGROUND
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.9
    scene.world = world
    sun_data = bpy.data.lights.new("sun", "SUN")
    sun_data.energy = 3.5
    sun = bpy.data.objects.new("sun", sun_data)
    sun.rotation_euler = (math.radians(40), 0, math.radians(30))
    scene.collection.objects.link(sun)
    engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    scene.render.engine = "BLENDER_EEVEE_NEXT" if "BLENDER_EEVEE_NEXT" in engines else "BLENDER_EEVEE"
    scene.render.resolution_x = scene.render.resolution_y = SIZE
    height = asset["height"]
    camera_data = bpy.data.cameras.new("camera")
    camera_data.lens = 35
    camera = bpy.data.objects.new("camera", camera_data)
    scene.collection.objects.link(camera)
    scene.camera = camera
    frames = []
    for name, angle, pitch in VIEWS:
        yaw, tilt = math.radians(angle), math.radians(pitch)
        target = Vector((0, 0, height * (0.55 if pitch >= 0 else 0.75)))
        distance = height * (1.9 if pitch >= 0 else 0.9)
        camera.location = target + Vector((math.sin(yaw) * math.cos(tilt), -math.cos(yaw) * math.cos(tilt),
                                           math.sin(tilt))) * distance
        if pitch < 0:
            camera.location.z = 1.7
        camera.rotation_euler = (target - camera.location).to_track_quat("-Z", "Y").to_euler()
        frame = str(Path(output).with_name(Path(output).stem + f"-{name}.png"))
        scene.render.filepath = frame
        bpy.ops.render.render(write_still=True)
        frames.append(frame)
    sheet = bpy.data.images.new("sheet", SIZE * len(frames), SIZE)
    pixels = [0.0] * (SIZE * len(frames) * SIZE * 4)
    for k, frame in enumerate(frames):
        source_pixels = list(bpy.data.images.load(frame).pixels[:])
        for y in range(SIZE):
            row = (y * SIZE * len(frames) + k * SIZE) * 4
            pixels[row:row + SIZE * 4] = source_pixels[y * SIZE * 4:(y + 1) * SIZE * 4]
        Path(frame).unlink()
    sheet.pixels = pixels
    sheet.filepath_raw = output
    sheet.file_format = "PNG"
    sheet.save()


main()
