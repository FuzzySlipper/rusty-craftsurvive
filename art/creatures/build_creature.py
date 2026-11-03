"""Builds a creature GLB from a species file: rig, body, skin and clips (#10568).

usage: blender -b --factory-startup --python-exit-code 1 --python art/creatures/build_creature.py -- \
           <species.json> <out.glb> [--mesh <body.glb>] [--blend <review.blend>]

- The rig is the species' family skeleton (only `plantigrade` so far), placed from the species'
  key points. Every bone's X axis is the animal's left, so a pitch is the same rotation whatever
  the bone points at.
- The body is the mesh given with --mesh, already in the species' rest pose, or else a stand-in
  built from metaball capsules along the bones. Either is skinned by automatic (heat) weights.
- Clips are generated, not hand-keyed: legs follow gait trajectories through IK (gait.py), the
  rest is keyed directly, and each clip is baked to plain joint keys before export. They are in
  place: the product plays a moving clip at `speed / clip speed` (the manifest has the speeds).
- Writes <out.glb> and <out>.manifest.json. Clip ids are lowercase, as the Engine requires, and
  the samplers are nearest for both filters (rusty-engine #10011).
"""
import json
import math
import struct
import sys
from pathlib import Path

import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
import gait  # noqa: E402

NEAREST = 9728
SIDES = {"L": 1.0, "R": -1.0}
# A pole sits this far in front of a knee or behind an elbow.
POLE_METRES = 1.2
IK_CHAIN = 2


# ---------------------------------------------------------------- the family skeleton

def to_blender(point, side=1.0):
    """Species [side, forward, up] to Blender: the animal faces -Y, so glTF export faces +Z."""
    s, f, u = point
    return Vector((s * side, -f, u))


def plantigrade_bones():
    """(name, parent, head point, tail point, side) for the plantigrade family: sloths, bears."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
        ("jaw", "head", "jaw", "jaw_tip", 1.0),
        ("tail1", "hips", "tail0", "tail1", 1.0),
        ("tail2", "tail1", "tail1", "tail2", 1.0),
        ("tail3", "tail2", "tail2", "tail3", 1.0),
        ("tail4", "tail3", "tail3", "tail4", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"thigh.{side}", "hips", "hip_socket", "knee", sign),
            (f"shin.{side}", f"thigh.{side}", "knee", "ankle", sign),
            (f"foot.{side}", f"shin.{side}", "ankle", "toe", sign),
            (f"upperarm.{side}", "chest", "shoulder", "elbow", sign),
            (f"forearm.{side}", f"upperarm.{side}", "elbow", "wrist", sign),
            (f"hand.{side}", f"forearm.{side}", "wrist", "knuckle", sign),
            (f"claw.{side}", f"hand.{side}", "knuckle", "claw_tip", sign),
        ]
    return bones


# Each leg: the IK bone (chain of two ending at it), the end bone the target turns, and whether
# the joint between bends forward (a knee) or back (an elbow).
LEGS = {
    "hind": ("shin", "foot", "knee", 1.0),
    "fore": ("forearm", "hand", "elbow", -1.0),
}


def build_armature(species):
    points = species["points"]
    data = bpy.data.armatures.new(species["id"])
    rig = bpy.data.objects.new(species["id"], data)
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for name, parent, head, tail, sign in plantigrade_bones():
        bone = data.edit_bones.new(name)
        bone.head = to_blender(points[head], sign)
        bone.tail = to_blender(points[tail], sign)
        direction = (bone.tail - bone.head).normalized()
        # Roll so the bone's X axis is the animal's left (+X): Z = X x Y.
        bone.align_roll(Vector((1, 0, 0)).cross(direction))
        if parent:
            bone.parent = data.edit_bones[parent]
            bone.use_connect = (bone.head - bone.parent.tail).length < 1e-4
    bpy.ops.object.mode_set(mode="OBJECT")
    for pb in rig.pose.bones:
        pb.rotation_mode = "QUATERNION"
    return rig


# ---------------------------------------------------------------- the stand-in body

def segment_distance(p, a, b):
    ab = b - a
    t = max(0.0, min(1.0, (p - a).dot(ab) / max(ab.length_squared, 1e-12)))
    return (p - (a + ab * t)).length


def region_of(bone_name):
    base = bone_name.split(".")[0]
    if base in ("thigh", "shin", "foot", "upperarm", "forearm", "hand"):
        return "legs"
    if base == "claw":
        return "claw"
    return "coat"


def build_proxy(species, rig):
    proxy = species["proxy"]
    radii = proxy["radii"]
    ball = bpy.data.metaballs.new(species["id"] + "-proxy")
    ball.resolution = proxy["resolution"]
    ball.render_resolution = proxy["resolution"]
    ball.threshold = 0.6
    for bone in rig.data.bones:
        base = bone.name.split(".")[0]
        if base not in radii:
            continue
        head, tail = bone.head_local, bone.tail_local
        element = ball.elements.new(type="CAPSULE")
        element.co = (head + tail) / 2
        element.radius = radii[base]
        element.size_x = (tail - head).length / 2
        element.rotation = Vector((1, 0, 0)).rotation_difference((tail - head).normalized())
        element.stiffness = 2.0
    obj = bpy.data.objects.new(species["id"] + "-proxy", ball)
    bpy.context.scene.collection.objects.link(obj)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.convert(target="MESH")
    mesh = bpy.context.view_layer.objects.active
    faces = len(mesh.data.polygons)
    triangles = sum(len(p.vertices) - 2 for p in mesh.data.polygons)
    decimate = mesh.modifiers.new("decimate", "DECIMATE")
    decimate.ratio = min(1.0, proxy["triangles"] / max(triangles, 1))
    bpy.ops.object.modifier_apply(modifier=decimate.name)
    bpy.ops.object.shade_smooth()
    print(f"proxy: {faces} faces from metaballs, {len(mesh.data.polygons)} after decimation")
    paint_proxy(species, rig, mesh)
    return mesh


def paint_proxy(species, rig, mesh):
    """A tiny palette texture, and each face's UVs at the centre of its region's cell."""
    palette = species["proxy"]["palette"]
    names = list(palette)
    size = len(names)
    image = bpy.data.images.new(species["id"] + "-palette", width=size, height=1, alpha=False)
    pixels = []
    for name in names:
        hex_colour = palette[name].lstrip("#")
        srgb = [int(hex_colour[i:i + 2], 16) / 255 for i in (0, 2, 4)]
        pixels += srgb + [1.0]
    image.pixels = pixels
    image.filepath_raw = str(Path(bpy.app.tempdir) / (image.name + ".png"))
    image.file_format = "PNG"
    image.save()

    eye = to_blender(species["points"]["eye"])
    eyes = [eye, Vector((-eye.x, eye.y, eye.z))]
    eye_radius = 0.07
    belly_below = -0.35
    segments = [(b.name, b.head_local, b.tail_local) for b in rig.data.bones if b.name != "root"]
    uv = mesh.data.uv_layers.new(name="UVMap")
    for poly in mesh.data.polygons:
        centre = poly.center
        nearest = min(segments, key=lambda s: segment_distance(centre, s[1], s[2]))[0]
        region = region_of(nearest)
        base = nearest.split(".")[0]
        if region == "coat" and poly.normal.z < belly_below and base in ("hips", "spine", "chest", "neck"):
            region = "belly"
        if base in ("head", "jaw") and poly.normal.y < -0.5:
            region = "belly"
        if any((centre - e).length < eye_radius for e in eyes):
            region = "eye"
        u = (names.index(region) + 0.5) / size
        for loop in poly.loop_indices:
            uv.data[loop].uv = (u, 0.5)

    material = bpy.data.materials.new(species["id"] + "-proxy")
    material.use_nodes = True
    nodes = material.node_tree.nodes
    shader = nodes["Principled BSDF"]
    shader.inputs["Roughness"].default_value = 0.92
    texture = nodes.new("ShaderNodeTexImage")
    texture.image = image
    texture.interpolation = "Closest"
    material.node_tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
    mesh.data.materials.append(material)


def import_mesh(path):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    mesh = meshes[0]
    mesh.parent = None
    return mesh


def skin(rig, mesh):
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")
    unweighted = sum(1 for v in mesh.data.vertices if not v.groups)
    if unweighted:
        raise SystemExit(f"automatic weights left {unweighted} vertices unweighted")


# ---------------------------------------------------------------- IK controls

class Controls:
    """IK targets and poles for each leg, and helpers that pose bones by world-axis rotations."""

    def __init__(self, rig):
        self.rig = rig
        self.targets = {}
        self.rest = {}
        for kind, (ik_bone, end_bone, _joint, bend) in LEGS.items():
            for side in SIDES:
                leg = f"{kind}.{side}"
                end = rig.data.bones[f"{end_bone}.{side}"]
                target = bpy.data.objects.new(f"ik.{leg}", None)
                bpy.context.scene.collection.objects.link(target)
                target.matrix_world = end.matrix_local.copy()
                target.rotation_mode = "QUATERNION"
                pole = bpy.data.objects.new(f"pole.{leg}", None)
                bpy.context.scene.collection.objects.link(pole)
                joint = rig.data.bones[f"{ik_bone}.{side}"].head_local
                pole.location = joint + Vector((0, -bend * POLE_METRES, 0))
                pole.parent = rig
                pole.parent_type = "BONE"
                upper = rig.data.bones[f"{ik_bone}.{side}"].parent
                pole.parent_bone = upper.parent.name
                pole.matrix_parent_inverse = (rig.matrix_world @ upper.parent.matrix_local
                                              @ Matrix.Translation((0, upper.parent.length, 0))).inverted()
                pb = rig.pose.bones[f"{ik_bone}.{side}"]
                ik = pb.constraints.new("IK")
                ik.name = "IK"
                ik.target = target
                ik.pole_target = pole
                ik.chain_count = IK_CHAIN
                turn = rig.pose.bones[f"{end_bone}.{side}"].constraints.new("COPY_ROTATION")
                turn.name = "TURN"
                turn.target = target
                self.targets[leg] = target
                self.rest[leg] = target.matrix_world.copy()
        self.calibrate_poles()

    def calibrate_poles(self):
        """Pick each IK chain's pole angle that keeps the rest pose where it is."""
        for kind, (ik_bone, _end, _joint, _bend) in LEGS.items():
            for side in SIDES:
                pb = self.rig.pose.bones[f"{ik_bone}.{side}"]
                rest = pb.bone.head_local.copy()
                best = None
                for degrees in range(-180, 180, 5):
                    pb.constraints["IK"].pole_angle = math.radians(degrees)
                    bpy.context.view_layer.update()
                    error = (pb.head - rest).length + (pb.tail - pb.bone.tail_local).length
                    if best is None or error < best[0]:
                        best = (error, degrees)
                pb.constraints["IK"].pole_angle = math.radians(best[1])
                print(f"pole {kind}.{side}: {best[1]} degrees, rest error {best[0]:.4f} m")

    def bone_rotation(self, name, world):
        """Set a pose bone's rotation to a rotation about armature-space axes, at its rest."""
        rest = self.rig.data.bones[name].matrix_local.to_3x3()
        self.rig.pose.bones[name].rotation_quaternion = (rest.inverted() @ world @ rest).to_quaternion()

    def bone_offset(self, name, world):
        rest = self.rig.data.bones[name].matrix_local.to_3x3()
        self.rig.pose.bones[name].location = rest.inverted() @ Vector(world)

    def leg_influence(self, leg, value):
        kind, side = leg.split(".")
        ik_bone, end_bone, _joint, _bend = LEGS[kind]
        self.rig.pose.bones[f"{ik_bone}.{side}"].constraints["IK"].influence = value
        self.rig.pose.bones[f"{end_bone}.{side}"].constraints["TURN"].influence = value

    def place(self, leg, forward=0.0, up=0.0, side=0.0, toe_pitch=0.0):
        """Move a leg's target from its rest by metres forward/up/outward; pitch its end bone."""
        rest = self.rest[leg]
        sign = SIDES[leg.split(".")[1]]
        offset = Matrix.Translation((side * sign, -forward, up))
        turn = Matrix.Rotation(math.radians(-toe_pitch), 4, "X")
        location = rest.to_translation()
        self.targets[leg].matrix_world = offset @ Matrix.Translation(location) @ turn @ Matrix.Translation(-location) @ rest

    def reset(self):
        for pb in self.rig.pose.bones:
            pb.rotation_quaternion = Quaternion()
            pb.location = Vector()
        for leg in self.targets:
            self.leg_influence(leg, 1.0)
            self.place(leg)

    def key(self, frame):
        for pb in self.rig.pose.bones:
            pb.keyframe_insert("rotation_quaternion", frame=frame)
            pb.keyframe_insert("location", frame=frame)
            for constraint in pb.constraints:
                constraint.keyframe_insert("influence", frame=frame)
        for target in self.targets.values():
            target.keyframe_insert("location", frame=frame)
            target.keyframe_insert("rotation_quaternion", frame=frame)


def pitch(degrees):
    """Raises the front: a forward-pointing bone tips up, a hanging limb swings forward."""
    return Matrix.Rotation(math.radians(-degrees), 3, "X")


def yaw(degrees):
    """Turns to the animal's left."""
    return Matrix.Rotation(math.radians(degrees), 3, "Z")


def roll(degrees):
    """Rolls about the forward axis, left side up."""
    return Matrix.Rotation(math.radians(-degrees), 3, "Y")


# ---------------------------------------------------------------- the clips

def gait_pose(c, settings, t, spine_flex):
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        forward, up = gait.foot(phase, duty, stride, settings["liftHind" if leg.startswith("hind") else "liftFore"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-25.0 * swing)
        if leg.startswith("fore"):
            c.bone_rotation(f"claw.{leg.split('.')[1]}", pitch(-30.0 * swing))
    sway = settings["swayDegrees"]
    bob = settings["bob"]
    c.bone_offset("hips", (0, 0, -bob * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ yaw(0.6 * sway * gait.wave(t, lag=0.1)))
    c.bone_rotation("spine", yaw(-0.5 * sway * gait.wave(t, lag=0.15)) @ pitch(spine_flex * gait.wave(t, 2)))
    c.bone_rotation("chest", roll(-0.6 * sway * gait.wave(t, lag=0.2)))
    c.bone_rotation("neck", pitch(-2.0 * gait.wave(t, 2, lag=0.1)))
    c.bone_rotation("head", pitch(-3.0 * gait.wave(t, 2, lag=0.2)) @ yaw(-sway * gait.wave(t, lag=0.3)))
    for i in range(1, 5):
        c.bone_rotation(f"tail{i}", yaw(2.0 * i * gait.wave(t, lag=0.2 + 0.08 * i)))


def clip_walk(c, s, t):
    gait_pose(c, s["walk"], t, spine_flex=1.0)


def clip_run(c, s, t):
    gait_pose(c, s["run"], t, spine_flex=4.0)


def clip_idle(c, s, t):
    c.bone_rotation("spine", pitch(0.8 * gait.wave(t, 2)))
    c.bone_rotation("chest", pitch(-0.6 * gait.wave(t, 2, lag=0.1)))
    c.bone_rotation("neck", pitch(-4.0 + 2.0 * gait.wave(t)) @ yaw(8.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", pitch(3.0 * gait.wave(t, 2)) @ yaw(6.0 * gait.wave(t, lag=0.3)))
    c.bone_rotation("jaw", pitch(-2.0 * max(0.0, gait.wave(t, 4))))
    for i in range(1, 5):
        c.bone_rotation(f"tail{i}", yaw(1.5 * i * gait.wave(t, lag=0.1 * i)))


def clip_graze(c, s, t):
    tug = math.exp(-((t - 0.55) / 0.05) ** 2)
    c.bone_rotation("chest", pitch(-6.0))
    c.bone_rotation("neck", pitch(-38.0 + 8.0 * tug) @ yaw(5.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(-28.0 + 10.0 * tug))
    c.bone_rotation("jaw", pitch(-9.0 * (0.5 + 0.5 * gait.wave(t, 6))))
    c.bone_offset("hips", (0, 0, -0.04))
    for i in range(1, 5):
        c.bone_rotation(f"tail{i}", yaw(1.2 * i * gait.wave(t, lag=0.1 * i)))


def clip_alert(c, s, t):
    c.bone_rotation("spine", pitch(3.0))
    c.bone_rotation("chest", pitch(4.0))
    c.bone_rotation("neck", pitch(16.0) @ yaw(20.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(10.0) @ yaw(10.0 * gait.wave(t, lag=0.1)))
    for i in range(1, 5):
        c.bone_rotation(f"tail{i}", pitch(-2.0) @ yaw(0.6 * i * gait.wave(t, 2)))


def rear(c, amount, pitch_degrees):
    """The tripod stance: body pitched up about the pelvis, sitting back onto the tail."""
    c.bone_rotation("hips", pitch(pitch_degrees * amount))
    c.bone_offset("hips", (0, 0.12 * amount, -0.18 * amount))
    c.bone_rotation("spine", pitch(5.0 * amount))
    c.bone_rotation("chest", pitch(2.0 * amount))
    for i, degrees in enumerate((-30.0, -12.0, 4.0, 10.0), start=1):
        c.bone_rotation(f"tail{i}", pitch(degrees * amount))


def clip_rear_browse(c, s, t):
    amount = gait.rise_hold_fall(t, 0.25, 0.75)
    rear(c, amount, s["rear-browse"]["pitchDegrees"])
    browse = gait.ease_in_out(t, 0.2, 0.32) * (1.0 - gait.ease_in_out(t, 0.68, 0.8))
    for side, lag in (("L", 0.0), ("R", 0.5)):
        c.leg_influence(f"fore.{side}", 1.0 - gait.ease_in_out(t, 0.02, 0.18) * (1.0 - gait.ease_in_out(t, 0.82, 0.98)))
        pull = gait.wave((t - 0.25) / 0.5, 3, lag=lag) * browse
        c.bone_rotation(f"upperarm.{side}", pitch(70.0 * amount + 25.0 * pull))
        c.bone_rotation(f"forearm.{side}", pitch(20.0 * amount - 25.0 * pull))
        c.bone_rotation(f"hand.{side}", pitch(15.0 * amount))
        c.bone_rotation(f"claw.{side}", pitch(-45.0 * browse * (0.5 + 0.5 * pull)))
    c.bone_rotation("neck", pitch(-4.0 * amount))
    c.bone_rotation("head", pitch(-6.0 * amount + 4.0 * gait.wave(t, 3) * browse))
    c.bone_rotation("jaw", pitch(-8.0 * browse * (0.5 + 0.5 * gait.wave(t, 9))))


def clip_defend_swipe(c, s, t):
    amount = gait.rise_hold_fall(t, 0.3, 0.7)
    rear(c, amount, s["defend-swipe"]["pitchDegrees"])
    c.leg_influence("fore.R", 1.0 - gait.ease_in_out(t, 0.05, 0.2) * (1.0 - gait.ease_in_out(t, 0.8, 0.95)))
    wind = gait.ease_in_out(t, 0.1, 0.38) * (1.0 - gait.ease_in_out(t, 0.42, 0.52))
    strike = gait.ease_in_out(t, 0.42, 0.52) * (1.0 - gait.ease_in_out(t, 0.7, 0.9))
    c.bone_rotation("upperarm.R", pitch(110.0 * wind + 35.0 * strike) @ yaw(-30.0 * strike))
    c.bone_rotation("forearm.R", pitch(30.0 * wind - 10.0 * strike))
    c.bone_rotation("claw.R", pitch(-20.0 * wind + 30.0 * strike))
    c.bone_rotation("neck", pitch(-10.0 * strike + 8.0 * wind))
    c.bone_rotation("head", pitch(-8.0 * strike) @ yaw(-12.0 * strike))
    c.bone_rotation("jaw", pitch(-14.0 * (wind + strike)))


def clip_die(c, s, t):
    buckle = gait.ease_in_out(t, 0.0, 0.4)
    fall = gait.ease_in_out(t, 0.3, 0.75)
    limp = gait.ease_in_out(t, 0.25, 0.6)
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-25.0 * limp))
        c.bone_rotation(f"shin.{side}", pitch(35.0 * limp))
        c.bone_rotation(f"upperarm.{side}", pitch(20.0 * limp))
        c.bone_rotation(f"forearm.{side}", pitch(-30.0 * limp))
        c.bone_rotation(f"claw.{side}", pitch(-20.0 * limp))
    angle = 82.0 * fall
    c.bone_rotation("root", roll(angle))
    c.bone_offset("root", (0, 0, max(0.0, 0.95 - 1.7 * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, 0, -0.45 * buckle))
    c.bone_rotation("neck", pitch(-25.0 * fall) @ yaw(-15.0 * fall))
    c.bone_rotation("head", pitch(-15.0 * fall))
    c.bone_rotation("jaw", pitch(-10.0 * fall))
    for i in range(1, 5):
        c.bone_rotation(f"tail{i}", pitch(4.0 * fall))


def clip_rest(c, s, t):
    pass


# clip id: (pose function, loops, feet planted). Planted clips keep every foot on the ground, so
# the product may move their feet onto uneven ground with IK; the others lift or fold their legs.
CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (clip_idle, True, True),
    "walk": (clip_walk, True, True),
    "run": (clip_run, True, True),
    "graze": (clip_graze, True, True),
    "alert": (clip_alert, True, True),
    "rear-browse": (clip_rear_browse, False, False),
    "defend-swipe": (clip_defend_swipe, False, False),
    "die": (clip_die, False, False),
}
REST_SECONDS = 1.0


def bake_clips(rig, controls, species):
    settings = species["clips"]
    fps = settings["fps"]
    bpy.context.scene.render.fps = fps
    manifest = {}
    for clip, (pose, loops, plants) in CLIPS.items():
        seconds = settings.get(clip, {}).get("seconds", REST_SECONDS)
        frames = round(seconds * fps)
        # A looping clip's last frame repeats its first, so the cycle closes.
        for frame in range(frames + 1):
            t = frame / frames
            controls.reset()
            pose(controls, settings, t if not loops else t % 1.0 if frame < frames else 0.0)
            controls.key(frame + 1)
        options = anim_utils.BakeOptions(
            only_selected=False, do_pose=True, do_object=False, do_visual_keying=True,
            do_constraint_clear=False, do_parents_clear=False, do_clean=False,
            do_location=True, do_rotation=True, do_scale=False, do_bbone=False, do_custom_props=False)
        source = rig.animation_data.action
        baked = anim_utils.bake_action(rig, action=None, frames=range(1, frames + 2), bake_options=options)
        baked.name = clip
        baked.use_fake_user = True
        rig.animation_data.action = None
        bpy.data.actions.remove(source)
        for target in controls.targets.values():
            bpy.data.actions.remove(target.animation_data.action)
            target.animation_data_clear()
        entry = {"seconds": frames / fps, "loops": loops, "plantsFeet": plants}
        if clip in ("walk", "run"):
            entry["speed"] = round(gait.speed(settings[clip]["stride"], settings[clip]["seconds"]), 4)
        manifest[clip] = entry
        print(f"clip {clip}: {frames + 1} frames")
    return manifest


def strip_controls(rig, controls):
    for pb in rig.pose.bones:
        for constraint in list(pb.constraints):
            pb.constraints.remove(constraint)
        pb.rotation_quaternion = Quaternion()
        pb.location = Vector()
    for target in controls.targets.values():
        bpy.data.objects.remove(target)
    for pole in [o for o in bpy.data.objects if o.name.startswith("pole.")]:
        bpy.data.objects.remove(pole)


# ---------------------------------------------------------------- export

def export(rig, mesh, out_path):
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = rig
    actions = [a for a in bpy.data.actions if a.name in CLIPS]
    # Every clip gets its own NLA track, so the ACTIONS export finds all of them.
    rig.animation_data_create()
    for action in actions:
        track = rig.animation_data.nla_tracks.new()
        track.name = action.name
        strip = track.strips.new(action.name, 1, action)
        strip.action_slot = action.slots[0]
        track.mute = True
    bpy.ops.export_scene.gltf(
        filepath=str(out_path), export_format="GLB", use_selection=True,
        export_animations=True, export_animation_mode="ACTIONS", export_force_sampling=True,
        export_anim_slide_to_zero=True, export_def_bones=False, export_leaf_bone=False,
        export_yup=True, export_apply=False, export_skins=True, export_influence_nb=4,
        export_morph=False, export_cameras=False, export_lights=False, export_reset_pose_bones=True,
        export_rest_position_armature=True, export_image_format="AUTO", export_materials="EXPORT")
    patch_glb(out_path)


def patch_glb(path):
    """Nearest samplers for both filters, and a check that every clip id is lowercase."""
    data = Path(path).read_bytes()
    magic, version, _length = struct.unpack_from("<III", data, 0)
    json_length, json_type = struct.unpack_from("<II", data, 12)
    doc = json.loads(data[20:20 + json_length])
    rest = data[20 + json_length:]
    for sampler in doc.get("samplers", []):
        sampler["magFilter"] = NEAREST
        sampler["minFilter"] = NEAREST
    names = [a.get("name", "") for a in doc.get("animations", [])]
    bad = [n for n in names if n != n.lower()]
    if bad:
        raise SystemExit(f"clip ids must be lowercase: {bad}")
    missing = sorted(set(CLIPS) - set(names))
    if missing:
        raise SystemExit(f"clips missing from the export: {missing} (exported {names})")
    payload = json.dumps(doc, separators=(",", ":")).encode()
    payload += b" " * (-len(payload) % 4)
    out = struct.pack("<III", magic, version, 12 + 8 + len(payload) + len(rest))
    out += struct.pack("<II", len(payload), json_type) + payload + rest
    Path(path).write_bytes(out)
    print(f"wrote {path}: clips {names}")


def main(argv):
    species_path = Path(argv[0])
    out_path = Path(argv[1]).resolve()
    mesh_path = argv[argv.index("--mesh") + 1] if "--mesh" in argv else None
    blend_path = argv[argv.index("--blend") + 1] if "--blend" in argv else None
    species = json.loads(species_path.read_text())
    if species["family"] != "plantigrade":
        raise SystemExit(f"family {species['family']} has no skeleton yet")
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    rig = build_armature(species)
    mesh = import_mesh(mesh_path) if mesh_path else build_proxy(species, rig)
    skin(rig, mesh)
    controls = Controls(rig)
    points = species["points"]
    manifest = {
        "species": species["id"],
        "family": species["family"],
        # Where the hind and fore feet stand along the body, metres forward of the origin: the
        # product pitches the body to the ground under these two points.
        "body": {"hindMetres": points["ankle"][1], "foreMetres": points["wrist"][1]},
        # Each leg as (root, mid, end) joints for two-bone IK; end is the joint that stands.
        "legs": [[f"{a}.{side}", f"{b}.{side}", f"{c}.{side}"]
                 for side in SIDES for a, b, c in (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand"))],
        "clips": bake_clips(rig, controls, species),
    }
    strip_controls(rig, controls)
    if blend_path:
        bpy.ops.wm.save_as_mainfile(filepath=str(Path(blend_path).resolve()))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    export(rig, mesh, out_path)
    manifest_path = out_path.with_suffix(".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"wrote {manifest_path}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
