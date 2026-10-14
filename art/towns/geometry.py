"""Shared mesh, material and existing-prop helpers for offline town authoring."""

from __future__ import annotations

import json
import math
from pathlib import Path

import bpy
from mathutils import Vector

ROOT = Path(__file__).resolve().parents[2]
WALL_THICKNESS = 0.22
BEVEL_WIDTH = 0.018
DOOR_GROUND = 0.28
MATERIALS = {}
MATERIAL_DEFINITIONS = {}


def color(value):
    rgb = [int(value[i : i + 2], 16) / 255 for i in (1, 3, 5)]
    return tuple(v / 12.92 if v <= 0.04045 else ((v + 0.055) / 1.055) ** 2.4 for v in rgb) + (1,)


def materials(definitions, wet=False):
    global MATERIAL_DEFINITIONS
    MATERIAL_DEFINITIONS = definitions
    for name, definition in definitions.items():
        mat = bpy.data.materials.new(name)
        mat.use_nodes = True
        shader = mat.node_tree.nodes.get("Principled BSDF")
        shader.inputs["Base Color"].default_value = color(definition["color"])
        shader.inputs["Roughness"].default_value = (
            min(definition["roughness"], 0.42)
            if wet and name in ("stone", "wood", "roof")
            else definition["roughness"]
        )
        shader.inputs["Metallic"].default_value = definition.get("metallic", 0)
        shader.inputs["Alpha"].default_value = definition.get("alpha", 1)
        if definition.get("texture"):
            image = mat.node_tree.nodes.new("ShaderNodeTexImage")
            image.image = bpy.data.images.load(str(ROOT / definition["texture"]), check_existing=True)
            image.interpolation = "Linear"
            image.extension = "REPEAT"
            source = image.outputs["Color"]
            if definition.get("tint"):
                tint = mat.node_tree.nodes.new("ShaderNodeMixRGB")
                tint.blend_type = "MULTIPLY"
                tint.inputs[0].default_value = 1
                tint.inputs[2].default_value = color(definition["tint"])
                mat.node_tree.links.new(source, tint.inputs[1])
                source = tint.outputs[0]
            mat.node_tree.links.new(source, shader.inputs["Base Color"])
        if definition.get("emission"):
            shader.inputs["Emission Color"].default_value = color(definition["color"])
            shader.inputs["Emission Strength"].default_value = definition["emission"]
        MATERIALS[name] = mat


def empty(name, position=(0, 0, 0), angle=0, parent=None):
    obj = bpy.data.objects.new(name, None)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    obj.location = position
    obj.rotation_euler.z = math.radians(angle)
    return obj


def mesh(name, vertices, faces, material, parent=None):
    data = bpy.data.meshes.new(name)
    data.from_pydata(vertices, [], faces)
    data.update()
    obj = bpy.data.objects.new(name, data)
    bpy.context.collection.objects.link(obj)
    obj.parent = parent
    data.materials.append(MATERIALS[material])
    uv = data.uv_layers.new(name="MaterialMetres")
    tile = MATERIAL_DEFINITIONS[material].get("tile_metres", 2)
    for poly in data.polygons:
        axis = max(range(3), key=lambda i: abs(poly.normal[i]))
        plane = ((1, 2), (0, 2), (0, 1))[axis]
        for loop in poly.loop_indices:
            vertex = data.vertices[data.loops[loop].vertex_index].co
            uv.data[loop].uv = (vertex[plane[0]] / tile, vertex[plane[1]] / tile)
    obj["material_role"] = material
    return obj


def box(name, location, size, material, parent=None, bevel=BEVEL_WIDTH):
    x, y, z = (v / 2 for v in size)
    vertices = [
        (-x, -y, -z),
        (x, -y, -z),
        (x, y, -z),
        (-x, y, -z),
        (-x, -y, z),
        (x, -y, z),
        (x, y, z),
        (-x, y, z),
    ]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    obj = mesh(name, vertices, faces, material, parent)
    obj.location = location
    # Continuous masonry/plaster across adjacent panels. Timber keeps its local
    # grain orientation so an upright and a rotated brace use the same source.
    if material in ("plaster", "stone", "paving"):
        tile = MATERIAL_DEFINITIONS[material].get("tile_metres", 2)
        for poly in obj.data.polygons:
            axis = max(range(3), key=lambda i: abs(poly.normal[i]))
            plane = ((1, 2), (0, 2), (0, 1))[axis]
            for loop in poly.loop_indices:
                obj.data.uv_layers.active.data[loop].uv += Vector(
                    (location[plane[0]] / tile, location[plane[1]] / tile)
                )
    elif material in ("wood", "painted-timber"):
        grain_axis = max(range(3), key=lambda i: size[i])
        tile = MATERIAL_DEFINITIONS[material].get("tile_metres", 2)
        for poly in obj.data.polygons:
            normal_axis = max(range(3), key=lambda i: abs(poly.normal[i]))
            cross_axis = next(
                (i for i in range(3) if i != grain_axis and i != normal_axis), (grain_axis + 1) % 3
            )
            for loop in poly.loop_indices:
                v = obj.data.vertices[obj.data.loops[loop].vertex_index].co
                obj.data.uv_layers.active.data[loop].uv = (v[cross_axis] / tile, v[grain_axis] / tile)
    if bevel:
        modifier = obj.modifiers.new("EdgeSoftness", "BEVEL")
        modifier.width = min(bevel, min(size) / 4)
        modifier.segments = 1
    return obj


def beam(name, start, end, width, material, parent=None):
    a, b = Vector(start), Vector(end)
    obj = box(name, (a + b) / 2, (width, width, (b - a).length), material, parent)
    obj.rotation_euler = (b - a).to_track_quat("Z", "Y").to_euler()
    return obj


def disk(name, location, radius, material, parent, depth=0.035):
    sides = 16
    vertices = [
        (math.cos(i * math.tau / sides) * radius, y, math.sin(i * math.tau / sides) * radius)
        for y in (-depth / 2, depth / 2)
        for i in range(sides)
    ]
    faces = [tuple(range(sides - 1, -1, -1)), tuple(range(sides, sides * 2))]
    faces += [(i, (i + 1) % sides, (i + 1) % sides + sides, i + sides) for i in range(sides)]
    obj = mesh(name, vertices, faces, material, parent)
    obj.location = location
    return obj


def rock(name, position, scale, material="rock"):
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=1, location=position)
    obj = bpy.context.object
    obj.name = name
    for vertex in obj.data.vertices:
        vertex.co = Vector(tuple(vertex.co[i] * scale[i] for i in range(3)))
    obj.data.materials.append(MATERIALS[material])
    uv = obj.data.uv_layers.new(name="MaterialMetres")
    tile = MATERIAL_DEFINITIONS[material].get("tile_metres", 2)
    for loop in obj.data.loops:
        v = obj.data.vertices[loop.vertex_index].co
        uv.data[loop.index].uv = (v.x / tile, v.z / tile)
    for poly in obj.data.polygons:
        poly.use_smooth = True
    return obj


def product_prop(name, source, position, scale=1, angle=0):
    """Reuse existing product art; convert its Y-up coordinates to Blender Z-up."""
    document = json.loads((ROOT / "content/game/models" / source).read_text())
    parent = empty(name, position, angle)
    parent.scale = (scale,) * 3
    for part in document["parts"]:
        material_name = source + ":" + part["role"]
        if material_name not in MATERIALS:
            texture = ROOT / "content/game" / document["textures"][part["role"]]
            mat = bpy.data.materials.new(material_name)
            mat.use_nodes = True
            shader = mat.node_tree.nodes.get("Principled BSDF")
            shader.inputs["Roughness"].default_value = 0.85
            image = mat.node_tree.nodes.new("ShaderNodeTexImage")
            image.image = bpy.data.images.load(str(texture), check_existing=True)
            mat.node_tree.links.new(image.outputs["Color"], shader.inputs["Base Color"])
            mat.node_tree.links.new(image.outputs["Alpha"], shader.inputs["Alpha"])
            MATERIALS[material_name] = mat
            MATERIAL_DEFINITIONS[material_name] = {}
        coordinates = part["positions"]
        vertices = [
            (coordinates[i], -coordinates[i + 2], coordinates[i + 1]) for i in range(0, len(coordinates), 3)
        ]
        indices = part["indices"]
        faces = []
        discarded = 0
        for i in range(0, len(indices), 3):
            triangle = tuple(indices[i : i + 3])
            a, b, c = (Vector(vertices[index]) for index in triangle)
            # Collapsed end-cap triangles in source props have no visible area
            # but make a GLB scene fail Engine admission. Keep the source intact.
            if (b - a).cross(c - a).length_squared <= 1e-12:
                discarded += 1
                continue
            faces.append(triangle)
        obj = mesh(name + "." + part["role"], vertices, faces, material_name, parent)
        if discarded:
            obj["discarded_degenerate_triangles"] = discarded
            print("PROP_REPAIR", source, part["role"], discarded, "zero-area triangles omitted")
        uv = obj.data.uv_layers.active
        for loop in obj.data.loops:
            i = loop.vertex_index * 2
            uv.data[loop.index].uv = part["uvs"][i : i + 2]
        for poly in obj.data.polygons:
            poly.use_smooth = True
    return parent
