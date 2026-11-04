"""Builds a creature GLB from a species file: rig, body, skin and clips (#10568).

usage: blender -b --factory-startup --python-exit-code 1 --python art/creatures/build_creature.py -- \
           <species.json> <out.glb> [--mesh <body.glb> [--texture <base colour.png>]] [--blend <review.blend>]

- The rig is the species' family skeleton (families.py), placed from the species' key points. Every bone's X axis is the animal's left, so a pitch is the same rotation whatever
  the bone points at.
- The body is the mesh given with --mesh (prepare_mesh.py's body.glb), already in the species' rest
  pose, with --texture replacing its base colour (the pixel-reduced game texture); or else a
  stand-in built from metaball capsules along the bones. Either is skinned by automatic (heat)
  weights; vertices heat weighting cannot reach take their nearest bone.
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

import bmesh
import bpy
from bpy_extras import anim_utils
from mathutils import Matrix, Quaternion, Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
from families import FAMILIES, SIDES  # noqa: E402
import gait  # noqa: E402

NEAREST = 9728
# A pole sits this far in front of a knee or behind an elbow.
POLE_METRES = 1.2
IK_CHAIN = 2
WELD_METRES = 1e-5
# More unweighted than this share means the rig doesn't fit the body: stop rather than patch it.
UNWEIGHTED_SHARE = 0.05
# A watertight copy for the heat solver has this many voxels along the body's longest side.
VOXELS_ALONG_BODY = 220


# ---------------------------------------------------------------- the family skeleton

def to_blender(point, side=1.0):
    """Species [side, forward, up] to Blender: the animal faces -Y, so glTF export faces +Z."""
    s, f, u = point
    return Vector((s * side, -f, u))


def limb_point(points, name, sign):
    """A limb point mirrors to the right side, unless a staggered body names its own: "elbow.R", still in left-side metres."""
    return to_blender(points.get(f"{name}.R", points[name]) if sign < 0 else points[name], sign)


def build_armature(species, family):
    points = species["points"]
    data = bpy.data.armatures.new(species["id"])
    rig = bpy.data.objects.new(species["id"], data)
    bpy.context.scene.collection.objects.link(rig)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode="EDIT")
    for name, parent, head, tail, sign in family.bones():
        bone = data.edit_bones.new(name)
        bone.head = limb_point(points, head, sign)
        bone.tail = limb_point(points, tail, sign)
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


def region_of(bone_name, family):
    base = bone_name.split(".")[0]
    if base in family.leg_bones:
        return "legs"
    if base in family.tip_bones:
        return "claw"
    return "coat"


def build_proxy(species, rig, family):
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
    paint_proxy(species, rig, mesh, family)
    return mesh


def paint_proxy(species, rig, mesh, family):
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
        region = region_of(nearest, family)
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


def import_mesh(path, texture=None):
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.context.selected_objects if o.type == "MESH"]
    if len(meshes) != 1:
        raise SystemExit(f"{path}: expected one mesh, found {len(meshes)}")
    mesh = meshes[0]
    mesh.parent = None
    # glTF splits vertices along UV seams; weld them (UVs are per corner, so the seams stay in the
    # charts), or each side of a seam is weighted on its own and the skin tears there.
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.remove_doubles(threshold=WELD_METRES)
    bpy.ops.object.mode_set(mode="OBJECT")
    if texture:
        image = bpy.data.images.load(str(Path(texture).resolve()))
        for material in mesh.data.materials:
            for node in material.node_tree.nodes:
                if node.type == "TEX_IMAGE":
                    node.image = image
                    node.interpolation = "Closest"
    return mesh


def unweighted_vertices(mesh):
    return [v for v in mesh.data.vertices if not any(g.weight > 0 for g in v.groups)]


def auto_weights(rig, mesh):
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_AUTO")


def keep_largest_island(mesh):
    """Drops every loose island but the body: one that no bone can see stops the whole solve."""
    data = bmesh.new()
    data.from_mesh(mesh.data)
    data.verts.ensure_lookup_table()
    seen, islands = set(), []
    for start in data.verts:
        if start.index in seen:
            continue
        island, stack = [], [start]
        seen.add(start.index)
        while stack:
            vertex = stack.pop()
            island.append(vertex)
            for edge in vertex.link_edges:
                other = edge.other_vert(vertex)
                if other.index not in seen:
                    seen.add(other.index)
                    stack.append(other)
        islands.append(island)
    islands.sort(key=len, reverse=True)
    print(f"watertight copy: {len(islands)} islands, keeping {len(islands[0])} of {len(data.verts)} vertices")
    bmesh.ops.delete(data, geom=[v for island in islands[1:] for v in island], context="VERTS")
    data.to_mesh(mesh.data)
    data.free()


def weights_from_watertight_copy(rig, mesh):
    """
    The heat solver can fail outright on a body with loose parts (a mane's flaps): solve it on a
    voxel copy that is one closed surface, then carry the weights to the body's own vertices.
    """
    mesh.parent = None
    for modifier in list(mesh.modifiers):
        mesh.modifiers.remove(modifier)
    mesh.vertex_groups.clear()
    copy = mesh.copy()
    copy.data = mesh.data.copy()
    bpy.context.scene.collection.objects.link(copy)
    bpy.ops.object.select_all(action="DESELECT")
    copy.select_set(True)
    bpy.context.view_layer.objects.active = copy
    remesh = copy.modifiers.new("watertight", "REMESH")
    remesh.mode = "VOXEL"
    remesh.voxel_size = max(copy.dimensions) / VOXELS_ALONG_BODY
    bpy.ops.object.modifier_apply(modifier=remesh.name)
    keep_largest_island(copy)
    auto_weights(rig, copy)
    print(f"watertight copy: {len(copy.data.vertices)} vertices, {len(unweighted_vertices(copy))} unweighted")
    copy.parent = None
    for modifier in list(copy.modifiers):
        copy.modifiers.remove(modifier)
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    rig.select_set(True)
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.parent_set(type="ARMATURE_NAME")
    bpy.ops.object.select_all(action="DESELECT")
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = mesh
    transfer = mesh.modifiers.new("weights", "DATA_TRANSFER")
    transfer.object = copy
    transfer.use_vert_data = True
    transfer.data_types_verts = {"VGROUP_WEIGHTS"}
    transfer.vert_mapping = "POLYINTERP_NEAREST"
    transfer.layers_vgroup_select_src = "ALL"
    transfer.layers_vgroup_select_dst = "NAME"
    bpy.ops.object.modifier_apply(modifier=transfer.name)
    bpy.data.objects.remove(copy)


def skin(rig, mesh):
    auto_weights(rig, mesh)
    if len(unweighted_vertices(mesh)) > UNWEIGHTED_SHARE * len(mesh.data.vertices):
        print("automatic weights failed on the body; solving on a watertight copy")
        weights_from_watertight_copy(rig, mesh)
    # Heat weighting misses loose flaps (generated fur); those take their nearest bone outright.
    bones = [(b.name, b.head_local, b.tail_local) for b in rig.data.bones if b.name != "root"]
    unweighted = unweighted_vertices(mesh)
    if len(unweighted) > UNWEIGHTED_SHARE * len(mesh.data.vertices):
        raise SystemExit(f"automatic weights left {len(unweighted)} of {len(mesh.data.vertices)} vertices unweighted")
    for vertex in unweighted:
        nearest = min(bones, key=lambda b: segment_distance(vertex.co, b[1], b[2]))[0]
        group = mesh.vertex_groups.get(nearest) or mesh.vertex_groups.new(name=nearest)
        group.add([vertex.index], 1.0, "REPLACE")
    print(f"skin: {len(unweighted)} of {len(mesh.data.vertices)} vertices took their nearest bone")


def pin_shell(species, mesh):
    """A shell does not flex: all but the legs below its rim follows one bone, whatever they do beneath."""
    shell = species.get("shell")
    if not shell:
        return
    start, end = shell["forward"]

    def leg(vertex):
        return vertex.co.z < shell["aboveMetres"] and any(a <= -vertex.co.y <= b for a, b in shell["legs"])

    pinned = [v.index for v in mesh.data.vertices if start <= -v.co.y <= end and not leg(v)]
    for group in mesh.vertex_groups:
        group.remove(pinned)
    group = mesh.vertex_groups.get(shell["bone"]) or mesh.vertex_groups.new(name=shell["bone"])
    group.add(pinned, 1.0, "REPLACE")
    print(f"shell: {len(pinned)} of {len(mesh.data.vertices)} vertices follow {shell['bone']}")


# ---------------------------------------------------------------- IK controls

class Controls:
    """IK targets and poles for each leg, and helpers that pose bones by world-axis rotations."""

    def __init__(self, rig, family):
        self.rig = rig
        self.legs = family.legs
        self.targets = {}
        self.rest = {}
        for kind, (ik_bone, end_bone, _joint, bend) in self.legs.items():
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
        for kind, (ik_bone, _end, _joint, _bend) in self.legs.items():
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
        ik_bone, end_bone, _joint, _bend = self.legs[kind]
        self.rig.pose.bones[f"{ik_bone}.{side}"].constraints["IK"].influence = value
        self.rig.pose.bones[f"{end_bone}.{side}"].constraints["TURN"].influence = value

    def leg_length(self, leg):
        """How far a leg reaches: from its target's rest down the chain to the body, in metres."""
        kind, side = leg.split(".")
        top = self.rig.data.bones[f"{self.legs[kind][0]}.{side}"].parent.parent
        return (self.rest[leg].to_translation() - top.head_local).length

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


REST_SECONDS = 1.0


def species_clips(species, family):
    """The family's clips this species has settings for, and `rest` always."""
    settings = species["clips"]
    return {clip: entry for clip, entry in family.clips.items() if clip == "rest" or clip in settings}


def bake_clips(rig, controls, species, clips):
    settings = species["clips"]
    fps = settings["fps"]
    bpy.context.scene.render.fps = fps
    manifest = {}
    for clip, (pose, loops, plants) in clips.items():
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

def export(rig, mesh, out_path, clips):
    bpy.ops.object.select_all(action="DESELECT")
    rig.select_set(True)
    mesh.select_set(True)
    bpy.context.view_layer.objects.active = rig
    actions = [a for a in bpy.data.actions if a.name in clips]
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
    patch_glb(out_path, clips)


def patch_glb(path, clips):
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
    missing = sorted(set(clips) - set(names))
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
    if species["family"] not in FAMILIES:
        raise SystemExit(f"family {species['family']} has no skeleton yet")
    family = FAMILIES[species["family"]]
    clips = species_clips(species, family)
    for obj in list(bpy.data.objects):
        bpy.data.objects.remove(obj)
    rig = build_armature(species, family)
    texture = argv[argv.index("--texture") + 1] if "--texture" in argv else None
    mesh = import_mesh(mesh_path, texture) if mesh_path else build_proxy(species, rig, family)
    skin(rig, mesh)
    pin_shell(species, mesh)
    controls = Controls(rig, family)
    points = species["points"]
    manifest = {
        "species": species["id"],
        "family": species["family"],
        # Where the hind and fore feet stand along the body, metres forward of the origin: the
        # product pitches the body to the ground under these two points.
        "body": {"hindMetres": points[family.hind_point][1], "foreMetres": points[family.fore_point][1]},
        # Each leg as (root, mid, end) joints for two-bone IK; end is the joint that stands.
        "legs": [[f"{a}.{side}", f"{b}.{side}", f"{c}.{side}"]
                 for side in SIDES for a, b, c in family.chains],
        "clips": bake_clips(rig, controls, species, clips),
    }
    strip_controls(rig, controls)
    if blend_path:
        bpy.ops.wm.save_as_mainfile(filepath=str(Path(blend_path).resolve()))
    out_path.parent.mkdir(parents=True, exist_ok=True)
    export(rig, mesh, out_path, clips)
    manifest_path = out_path.with_suffix(".manifest.json")
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"wrote {manifest_path}")


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:])
