"""Prepares a generated creature mesh for build_creature.py (#10568): orient, scale, cut to a game
budget, re-chart at an even texel density and bake its paint into a master texture.

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/prepare_mesh.py -- \
           <generated.glb> <out dir> --head +x --length 5.4 --xatlas <dir> [--triangles 6000] [--texels 128]

Needs the `xatlas` package built for Blender's Python (3.13), put in a scratch directory with
`python3 -m pip install --target <dir> --python-version 3.13 --only-binary=:all: --no-deps xatlas`.

- The head points along --head (+x, -x, +y, -y in Blender's frame after glTF import). The mesh is
  turned to face Blender's -Y (glTF +Z, the rig's front), scaled so its length along that axis is
  --length metres, stood on the ground (lowest point at 0) and centred on its bounding box.
- It is decimated to about --triangles and charted by xatlas at --texels per metre. Blender's
  smart projection cuts a generated body into thousands of islands a few texels across, which a
  32 texels/m reduction averages into colour noise; xatlas makes large charts (as
  authoring/tools/characters/reuv.py does for people). Its base colour is baked from the generated mesh into
  that master, four times the game density, for pixel_reduce.
- Writes <out dir>/body.glb (the prepared mesh with the master texture), master.png, and
  body.json (surface area, texel density, texture size, bounds).
"""
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

HEAD_AXES = {"+x": Vector((1, 0, 0)), "-x": Vector((-1, 0, 0)), "+y": Vector((0, 1, 0)), "-y": Vector((0, -1, 0))}
FRONT = Vector((0, -1, 0))
CAGE_SHARE = 0.01
# Master texels between charts: two game texels after a fourfold reduction.
CHART_PADDING = 8
# How far a chart may bend before xatlas splits it (its default is 2).
CHART_MAX_COST = 4.0
# In the generator's units (Tripo's are a 1 m model): close enough to be the same vertex.
WELD_METRES = 1e-5


def option(argv, name, default, kind=str):
    return kind(argv[argv.index(name) + 1]) if name in argv else default


def select_only(*objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects[-1]


def surface_area(mesh):
    return sum(p.area for p in mesh.data.polygons)


def chart(mesh, texels, xatlas):
    """xatlas charts at a texel density; returns the atlas size. Triangle order is kept, so
    corner k of triangle t takes corner k of xatlas's triangle t."""
    import numpy as np

    positions = np.array([v.co[:] for v in mesh.data.vertices], np.float32)
    triangles = np.array([p.vertices[:] for p in mesh.data.polygons], np.uint32)
    atlas = xatlas.Atlas()
    atlas.add_mesh(positions, triangles)
    options = xatlas.ChartOptions()
    options.max_cost = CHART_MAX_COST
    pack = xatlas.PackOptions()
    pack.texels_per_unit = texels
    pack.padding = CHART_PADDING
    pack.bilinear = False
    pack.rotate_charts = True
    atlas.generate(options, pack)
    _vmapping, indices, uvs = atlas[0]
    for layer in list(mesh.data.uv_layers):
        mesh.data.uv_layers.remove(layer)
    layer = mesh.data.uv_layers.new(name="UVMap").data
    for poly, corners in zip(mesh.data.polygons, indices):
        for loop, corner in zip(poly.loop_indices, corners):
            layer[loop].uv = uvs[corner]
    print(f"charts {atlas.chart_count}, atlas {atlas.width}x{atlas.height}, utilization {atlas.get_utilization(0):.3f}")
    return atlas.width, atlas.height, atlas.chart_count, atlas.get_utilization(0)


def main(argv):
    source, out = Path(argv[0]), Path(argv[1])
    head = option(argv, "--head", "+x")
    length = option(argv, "--length", 5.4, float)
    triangles = option(argv, "--triangles", 6000, int)
    texels = option(argv, "--texels", 128, int)
    sys.path.insert(0, option(argv, "--xatlas", ""))
    import xatlas
    out.mkdir(parents=True, exist_ok=True)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)

    bpy.ops.import_scene.gltf(filepath=str(source))
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    select_only(*meshes)
    if len(meshes) > 1:
        bpy.ops.object.join()
    original = bpy.context.view_layer.objects.active
    original.parent = None
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    # glTF splits vertices along the generator's UV seams; weld them, or the body stays hundreds of
    # loose pieces that no chart can cross and heat weighting cannot skin.
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=WELD_METRES)
    bpy.ops.object.mode_set(mode="OBJECT")

    # Face the rig's front, scale to length, stand on the ground, centre on the bounds.
    turn = HEAD_AXES[head].rotation_difference(FRONT).to_matrix().to_4x4()
    original.data.transform(turn)
    points = [v.co for v in original.data.vertices]
    lo = Vector([min(p[i] for p in points) for i in range(3)])
    hi = Vector([max(p[i] for p in points) for i in range(3)])
    scale = length / (hi.y - lo.y)
    centre = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
    original.data.transform(Matrix.Scale(scale, 4) @ Matrix.Translation(-centre))
    original.data.update()

    # The game mesh: a decimated copy with one evenly scaled chart set.
    game = original.copy()
    game.data = original.data.copy()
    game.name = "body"
    bpy.context.scene.collection.objects.link(game)
    select_only(game)
    before = sum(len(p.vertices) - 2 for p in game.data.polygons)
    decimate = game.modifiers.new("decimate", "DECIMATE")
    decimate.ratio = min(1.0, triangles / max(before, 1))
    bpy.ops.object.modifier_apply(modifier=decimate.name)
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.quads_convert_to_tris()
    bpy.ops.object.mode_set(mode="OBJECT")
    bpy.ops.object.shade_smooth()
    width, height, charts, utilization = chart(game, texels, xatlas)
    area = surface_area(game)
    image = bpy.data.images.new("master", width=width, height=height, alpha=False)
    material = bpy.data.materials.new("body")
    material.use_nodes = True
    nodes = material.node_tree.nodes
    shader = nodes["Principled BSDF"]
    shader.inputs["Roughness"].default_value = 0.9
    node = nodes.new("ShaderNodeTexImage")
    node.image = image
    node.interpolation = "Closest"
    material.node_tree.links.new(node.outputs["Color"], shader.inputs["Base Color"])
    nodes.active = node
    game.data.materials.clear()
    game.data.materials.append(material)

    # Bake the generated paint onto the new charts: base colour only, no light.
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 4
    scene.render.bake.use_selected_to_active = True
    scene.render.bake.cage_extrusion = CAGE_SHARE * length
    scene.render.bake.margin = 8
    select_only(original, game)
    bpy.ops.object.bake(type="DIFFUSE", pass_filter={"COLOR"})
    image.filepath_raw = str(out / "master.png")
    image.file_format = "PNG"
    image.save()

    bpy.data.objects.remove(original)
    select_only(game)
    bpy.ops.export_scene.gltf(filepath=str(out / "body.glb"), export_format="GLB", use_selection=True,
                              export_yup=True, export_materials="EXPORT", export_image_format="AUTO")
    points = [v.co for v in game.data.vertices]
    lo = [min(p[i] for p in points) for i in range(3)]
    hi = [max(p[i] for p in points) for i in range(3)]
    facts = {
        "source": str(source),
        "triangles": sum(len(p.vertices) - 2 for p in game.data.polygons),
        "areaSquareMetres": round(area, 3),
        "charts": charts,
        "utilization": round(utilization, 4),
        "masterTexels": [width, height],
        "masterTexelsPerMetre": texels,
        "boundsBlender": [lo, hi],
    }
    (out / "body.json").write_text(json.dumps(facts, indent=2) + "\n")
    print(json.dumps(facts))


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
