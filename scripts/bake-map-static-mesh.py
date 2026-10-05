"""Bake a normalized map model into an Engine static mesh (Blender, run headless).

Faceted, untextured runtime form: every triangle gets its own vertices, its face normal,
and the base-colour texture sampled at its centroid as a vertex colour. Output is the
Engine's StaticMeshAsset JSON with an inline payload (Y up).

    blender -b --python scripts/bake-map-static-mesh.py -- MODEL.glb NAME OUT.static-mesh.json
"""
import json
import sys

import bpy

argv = sys.argv[sys.argv.index("--") + 1:]
source, name, output = argv

bpy.ops.wm.read_factory_settings(use_empty=True)
bpy.ops.import_scene.gltf(filepath=source)
mesh_object = next(o for o in bpy.context.scene.objects if o.type == "MESH")
depsgraph = bpy.context.evaluated_depsgraph_get()
mesh = mesh_object.evaluated_get(depsgraph).to_mesh()
mesh.calc_loop_triangles()
matrix = mesh_object.matrix_world
uv_layer = mesh.uv_layers.active.data if mesh.uv_layers.active else None


def base_image(material):
    if material is None or not material.use_nodes:
        return None
    for node in material.node_tree.nodes:
        if node.type == "BSDF_PRINCIPLED":
            links = node.inputs["Base Color"].links
            if links and links[0].from_node.type == "TEX_IMAGE":
                return links[0].from_node.image
    return None


images = {}
for slot_index, slot in enumerate(mesh_object.material_slots):
    image = base_image(slot.material)
    if image is not None:
        images[slot_index] = (image.size[0], image.size[1], list(image.pixels[:]))


def sample(slot_index, u, v, fallback):
    if slot_index not in images:
        return fallback
    width, height, pixels = images[slot_index]
    x = min(width - 1, max(0, int((u % 1.0) * width)))
    y = min(height - 1, max(0, int((v % 1.0) * height)))
    i = (y * width + x) * 4
    return pixels[i:i + 3]


positions, normals, colors, indices = [], [], [], []
low = [float("inf")] * 3
high = [float("-inf")] * 3
for triangle in mesh.loop_triangles:
    slot = triangle.material_index
    material = mesh_object.material_slots[slot].material if mesh_object.material_slots else None
    fallback = list(material.diffuse_color[:3]) if material else [0.6, 0.6, 0.6]
    if uv_layer is not None:
        u = sum(uv_layer[loop].uv[0] for loop in triangle.loops) / 3
        v = sum(uv_layer[loop].uv[1] for loop in triangle.loops) / 3
        rgb = sample(slot, u, v, fallback)
    else:
        rgb = fallback
    normal = (matrix.to_3x3() @ triangle.normal).normalized()
    for vertex_index in triangle.vertices:
        p = matrix @ mesh.vertices[vertex_index].co
        y_up = (p.x, p.z, -p.y)
        for axis in range(3):
            low[axis] = min(low[axis], y_up[axis])
            high[axis] = max(high[axis], y_up[axis])
        indices.append(len(positions) // 3)
        positions.extend(round(c, 5) for c in y_up)
        normals.extend(round(c, 5) for c in (normal.x, normal.z, -normal.y))
        colors.extend([round(c, 4) for c in rgb] + [1.0])

count = len(positions) // 3
asset = {
    "asset": f"mesh/map-{name}",
    "payload": {
        "layout": {
            "vertexCount": count,
            "indexCount": len(indices),
            "indexWidth": "u32",
            "attributes": [
                {"name": "position", "components": 3, "kind": "f32"},
                {"name": "normal", "components": 3, "kind": "f32"},
                {"name": "color", "components": 4, "kind": "f32"},
            ],
        },
        "groups": [{"materialSlot": 0, "start": 0, "count": len(indices)}],
        "bounds": {"min": low, "max": high},
        "source": {"kind": "inline", "positions": positions, "normals": normals, "colors": colors, "indices": indices},
        "provenance": "staticAsset",
    },
    "materialSlots": [{"slot": 0, "material": f"material/map-{name}"}],
    "collision": {"kind": "visualOnly"},
}
with open(output, "w") as file:
    json.dump(asset, file, separators=(",", ":"))
print(f"baked {name}: {count} vertices")
