"""Build the town art study with Blender; no product or renderer integration.

Run with Blender's --python option. Outputs are ordinary GLBs, an editable
.blend and camera renders. recipes.json owns the building and camera tuning.
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import math
import sys
from pathlib import Path

import bpy
from mathutils import Matrix, Vector

# Blender's --python does not guarantee the script directory is on sys.path.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from buildings import building
from geometry import box, materials
from scenery import illumination, offline_atmosphere, stage


def camera(definition):
    data = bpy.data.cameras.new("StudyCamera")
    obj = bpy.data.objects.new("StudyCamera", data)
    bpy.context.collection.objects.link(obj)
    obj.location = definition["position"]
    obj.rotation_euler = (Vector(definition["target"]) - obj.location).to_track_quat("-Z", "Y").to_euler()
    data.lens = definition["lens"]
    data.clip_end = 1000
    bpy.context.scene.camera = obj
    return obj


def all_children(root):
    return [root, *root.children_recursive]


def export(path, objects):
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        if obj.type in ("MESH", "EMPTY"):
            obj.select_set(True)
    with contextlib.redirect_stdout(io.StringIO()):
        bpy.ops.export_scene.gltf(
            filepath=str(path),
            export_format="GLB",
            use_selection=True,
            export_apply=True,
            export_animations=False,
            export_cameras=False,
            export_lights=False,
            export_extras=True,
        )
    print("EXPORTED", path)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--scene", choices=("facade", "port", "upriver"), default="facade")
    parser.add_argument("--recipe", default="inn")
    parser.add_argument(
        "--weather", choices=("mild", "wet"), help="Override the fixture weather without changing its layout."
    )
    parser.add_argument("--views", default="wide,close")
    parser.add_argument("--width", type=int, default=1200)
    parser.add_argument("--samples", type=int, default=32)
    parser.add_argument("--render-engine", choices=("eevee", "cycles"), default="eevee")
    parser.add_argument(
        "--travel-frames", type=int, default=0, help="Render a wide-to-close camera study as numbered PNGs."
    )
    parser.add_argument("--export-only", action="store_true")
    args = parser.parse_args(sys.argv[sys.argv.index("--") + 1 :] if "--" in sys.argv else [])
    config = json.loads(Path(__file__).with_name("recipes.json").read_text())
    args.output.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    definition = config["scenes"].get(args.scene)
    wet = (args.weather == "wet") if args.weather else (definition["wet"] if definition else False)
    materials(config["materials"], wet)
    if definition:
        roots = [
            building(
                entry["recipe"] + "." + str(i),
                config["buildings"][entry["recipe"]],
                entry["position"],
                entry["angle"],
            )
            for i, entry in enumerate(definition["buildings"])
        ]
        stage(args.scene, wet)
        views = definition["cameras"]
    else:
        roots = [building(args.recipe, config["buildings"][args.recipe])]
        box("studio.ground", (0, 0, -0.13), (200, 200, 0.2), "ground", bevel=0)
        views = {
            "wide": {"position": [12, -19, 10], "target": [0, 0, 3.5], "lens": 44},
            "close": {"position": [4.5, -11, 4.2], "target": [0, -1.9, 2.4], "lens": 43},
        }
    illumination(wet)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES" if args.render_engine == "cycles" else "BLENDER_EEVEE"
    scene.cycles.device = "CPU"
    scene.cycles.samples = args.samples
    scene.cycles.use_denoising = True
    scene.render.resolution_x = args.width
    scene.render.resolution_y = round(args.width * 9 / 16)
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"
    scene.view_settings.exposure = 0.25 if wet else 0
    for i, root in enumerate(roots):
        placement = root.matrix_basis.copy()
        root.matrix_basis = Matrix.Identity(4)
        bpy.context.view_layer.update()
        export(args.output / f"building-{i}.glb", all_children(root))
        root.matrix_basis = placement
        bpy.context.view_layer.update()
    export(args.output / "scene.glb", list(scene.objects))
    offline_atmosphere(wet)
    selected_views = args.views.split(",")
    metadata = {
        "scene": args.scene,
        "source": "offline Blender art study",
        "render_engine": args.render_engine,
        "units": "metres",
        "weather": "wet" if wet else "mild",
        "views": {},
        "buildings": [
            {
                "name": root.name,
                "recipe": json.loads(root["recipe"]),
                "position": list(root.location),
                "angle_degrees": math.degrees(root.rotation_euler.z),
            }
            for root in roots
        ],
    }
    for name in selected_views:
        base = "close" if name in ("left", "right") else name
        view = dict(views[base])
        if name in ("left", "right"):
            view["position"] = list(view["position"])
            view["position"][0] += (-1 if name == "left" else 1) * (
                definition.get("motion_metres", 1) if definition else 1
            )
        obj = camera(view)
        metadata["views"][name] = view
        if not args.export_only:
            scene.render.filepath = str(args.output / f"{name}.png")
            bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(obj, do_unlink=True)
    travel = camera(views["wide"])
    if args.travel_frames > 1:
        frames = args.output / "camera-travel"
        frames.mkdir(exist_ok=True)
        scene.frame_start, scene.frame_end = 1, args.travel_frames
        scene.render.fps = 12
        for frame, key in ((1, "wide"), (args.travel_frames, "close")):
            target = views[key]
            travel.location = target["position"]
            travel.rotation_euler = (
                (Vector(target["target"]) - travel.location).to_track_quat("-Z", "Y").to_euler()
            )
            travel.data.lens = target["lens"]
            travel.keyframe_insert("location", frame=frame)
            travel.keyframe_insert("rotation_euler", frame=frame)
            travel.data.keyframe_insert("lens", frame=frame)
        metadata["camera_travel"] = {
            "frames": args.travel_frames,
            "fps": 12,
            "from": views["wide"],
            "to": views["close"],
        }
        scene.frame_set(1)
        scene.render.filepath = str(frames / "frame-")
        if not args.export_only:
            bpy.ops.render.render(animation=True)
    bpy.ops.wm.save_as_mainfile(filepath=str(args.output / "study.blend"))
    (args.output / "study.json").write_text(json.dumps(metadata, indent=2) + "\n")
    print("TOWN_STUDY_COMPLETE", args.output)


if __name__ == "__main__":
    main()
