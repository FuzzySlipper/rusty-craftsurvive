"""Cuts limbs a generated body should not have out of it, and closes the holes (#10570).

usage: blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/drop_limbs.py -- \
           <body.glb> <out.glb> --below <up metres> --between <forward from> <forward to> [--between ...]

A reference sheet drawing near and far legs apart in its side view gives Tripo a body with twice
the legs. Faces wholly below the height and between the forward bands go, in the species' own
metres; each hole is closed with the paint of the belly beside it, so the texture stands.
"""
import sys

import bmesh
import bpy

# How far off the middle, and above the cut, the belly's paint is taken from.
BELLY_METRES = 0.12


def main(argv):
    source, out = argv[0], argv[1]
    below = float(argv[argv.index("--below") + 1])
    bands = [(float(argv[i + 1]), float(argv[i + 2])) for i, word in enumerate(argv) if word == "--between"]

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=source)
    mesh = next(o for o in bpy.context.scene.objects if o.type == "MESH")
    world = mesh.matrix_world

    body = bmesh.new()
    body.from_mesh(mesh.data)
    uv = body.loops.layers.uv.active

    def cut(vertex):
        point = world @ vertex.co
        return point.z < below and any(start <= -point.y <= end for start, end in bands)

    doomed = [face for face in body.faces if all(cut(v) for v in face.verts)]
    print(f"cutting {len(doomed)} of {len(body.faces)} faces")
    bmesh.ops.delete(body, geom=doomed, context="FACES_ONLY")
    bmesh.ops.delete(body, geom=[v for v in body.verts if not v.link_faces], context="VERTS")
    bmesh.ops.delete(body, geom=[e for e in body.edges if not e.link_faces], context="EDGES")

    # The skin around a cut limb can carry paint from outside the body, so a patch takes the belly's own.
    belly = [(world @ loop.vert.co, loop[uv].uv.copy()) for face in body.faces for loop in face.loops
             if abs((world @ loop.vert.co).x) < BELLY_METRES and (world @ loop.vert.co).z < below + BELLY_METRES]
    filled = bmesh.ops.holes_fill(body, edges=[e for e in body.edges if e.is_boundary], sides=0)["faces"]
    made = bmesh.ops.triangulate(body, faces=filled)["faces"]
    for face in made:
        forward = (world @ face.calc_center_median()).y
        paint = min(belly, key=lambda point: abs(point[0].y - forward))[1]
        for loop in face.loops:
            loop[uv].uv = paint
    print(f"closed with {len(made)} faces, {len(body.faces)} in all")

    body.to_mesh(mesh.data)
    body.free()
    bpy.ops.export_scene.gltf(filepath=out, export_format="GLB", export_yup=True, export_materials="EXPORT", export_image_format="AUTO")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
