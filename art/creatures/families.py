"""The rig families (#10570): each family's skeleton, legs and clips, for build_creature.py.

A family is what animals built alike share. A species file picks one and places its joints. Clips
are pose functions over a Controls object: `pose(c, settings, t)` for t from 0 to 1 across the clip.
A family's clip table gives each clip its pose, whether it loops, and whether all its feet stay on
the ground; a species bakes the ones it has settings for, and `rest` always.
"""
import math
from dataclasses import dataclass, field

from mathutils import Matrix

import gait

SIDES = {"L": 1.0, "R": -1.0}


def pitch(degrees):
    """Raises the front: a forward-pointing bone tips up, a hanging limb swings forward."""
    return Matrix.Rotation(math.radians(-degrees), 3, "X")


def yaw(degrees):
    """Turns to the animal's left."""
    return Matrix.Rotation(math.radians(degrees), 3, "Z")


def roll(degrees):
    """Rolls about the forward axis, left side up."""
    return Matrix.Rotation(math.radians(-degrees), 3, "Y")



# ---------------------------------------------------------------- plantigrade: sloths, bears

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
PLANTIGRADE_LEGS = {
    "hind": ("shin", "foot", "knee", 1.0),
    "fore": ("forearm", "hand", "elbow", -1.0),
}


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
PLANTIGRADE_CLIPS = {
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


# ---------------------------------------------------------------- ungulate: horses, deer, goats, pigs

def ungulate_bones():
    """Hoofed legs have a third long segment: the cannon, below the hock and the carpus."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "neck2", 1.0),
        ("neck2", "neck", "neck2", "head", 1.0),
        ("head", "neck2", "head", "nose", 1.0),
        ("jaw", "head", "jaw", "jaw_tip", 1.0),
        ("tail1", "hips", "tail0", "tail1", 1.0),
        ("tail2", "tail1", "tail1", "tail2", 1.0),
        ("tail3", "tail2", "tail2", "tail3", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"thigh.{side}", "hips", "hip_socket", "stifle", sign),
            (f"shin.{side}", f"thigh.{side}", "stifle", "hock", sign),
            (f"cannon.{side}", f"shin.{side}", "hock", "fetlock", sign),
            (f"hoof.{side}", f"cannon.{side}", "fetlock", "toe", sign),
            (f"upperarm.{side}", "chest", "shoulder", "elbow", sign),
            (f"forearm.{side}", f"upperarm.{side}", "elbow", "carpus", sign),
            (f"fcannon.{side}", f"forearm.{side}", "carpus", "ffetlock", sign),
            (f"fhoof.{side}", f"fcannon.{side}", "ffetlock", "ftoe", sign),
        ]
    return bones


# IK runs on the lower two segments, which is also what the product's two-bone foot IK moves. The
# Engine has two-bone IK only, so the top segment (thigh, upper arm) is keyed from the stride.
UNGULATE_LEGS = {
    "hind": ("cannon", "hoof", "hock", -1.0),
    "fore": ("fcannon", "fhoof", "carpus", 1.0),
}

UNGULATE_TAIL = ("tail1", "tail2", "tail3")
# How much of a foot's reach forward or back the top segment takes, the lower two doing the rest.
TOP_SEGMENT_SHARE = 0.55


def ungulate_tail(c, t, swish, droop=0.0):
    for i, name in enumerate(UNGULATE_TAIL, start=1):
        c.bone_rotation(name, pitch(droop) @ yaw(swish * i * gait.wave(t, lag=0.12 * i)))


def ungulate_gait(c, settings, t, spine_flex):
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        kind, side = leg.split(".")
        forward, up = gait.foot(phase, duty, stride, settings["liftHind" if kind == "hind" else "liftFore"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-35.0 * swing)
        top = "thigh" if kind == "hind" else "upperarm"
        reach = math.degrees(math.asin(max(-0.9, min(0.9, forward / c.leg_length(leg)))))
        c.bone_rotation(f"{top}.{side}", pitch(TOP_SEGMENT_SHARE * reach))
    sway, bob = settings["swayDegrees"], settings["bob"]
    c.bone_offset("hips", (0, 0, -bob * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ pitch(spine_flex * gait.wave(t, 1, lag=0.25)))
    c.bone_rotation("spine", pitch(spine_flex * gait.wave(t, 1)))
    c.bone_rotation("chest", pitch(-spine_flex * gait.wave(t, 1, lag=0.25)) @ roll(-0.6 * sway * gait.wave(t, lag=0.2)))
    nod = settings.get("nodDegrees", 3.0)
    c.bone_rotation("neck", pitch(nod * gait.wave(t, 2, lag=0.1)))
    c.bone_rotation("neck2", pitch(-0.6 * nod * gait.wave(t, 2, lag=0.2)))
    c.bone_rotation("head", pitch(-0.5 * nod * gait.wave(t, 2, lag=0.3)))
    ungulate_tail(c, t, 1.5 * sway, droop=settings.get("tailLift", 0.0))


def ungulate_walk(c, s, t):
    ungulate_gait(c, s["walk"], t, spine_flex=0.8)


def ungulate_run(c, s, t):
    ungulate_gait(c, s["run"], t, spine_flex=s["run"].get("spineFlex", 7.0))


def ungulate_idle(c, s, t):
    c.bone_rotation("spine", pitch(0.6 * gait.wave(t, 2)))
    c.bone_rotation("neck", pitch(-3.0 + 2.0 * gait.wave(t)) @ yaw(5.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("neck2", yaw(4.0 * gait.wave(t, lag=0.3)))
    c.bone_rotation("head", pitch(3.0 * gait.wave(t, 2)) @ yaw(6.0 * gait.wave(t, lag=0.35)))
    ungulate_tail(c, t, 4.0)


def ungulate_graze(c, s, t):
    bite = 0.5 + 0.5 * gait.wave(t, 8)
    reach = s["graze"].get("reachDegrees", 34.0)
    c.bone_rotation("chest", pitch(-5.0))
    # The neck swings down from its root almost straight, and the head comes back level to the ground.
    c.bone_rotation("neck", pitch(-1.6 * reach) @ yaw(4.0 * gait.wave(t)))
    c.bone_rotation("neck2", pitch(-0.4 * reach))
    c.bone_rotation("head", pitch(0.6 * reach + 3.0 * gait.wave(t, 4)))
    c.bone_rotation("jaw", pitch(-6.0 * bite))
    c.bone_offset("hips", (0, 0, -0.02))
    ungulate_tail(c, t, 5.0)


def ungulate_alert(c, s, t):
    """Heads up: the neck stands, the head scans, and the body sets itself."""
    c.bone_rotation("chest", pitch(3.0))
    c.bone_rotation("neck", pitch(16.0) @ yaw(10.0 * gait.wave(t)))
    c.bone_rotation("neck2", pitch(10.0) @ yaw(10.0 * gait.wave(t, lag=0.1)))
    c.bone_rotation("head", pitch(-6.0) @ yaw(8.0 * gait.wave(t, lag=0.15)))
    c.bone_offset("hips", (0, 0, -0.015))
    ungulate_tail(c, t, 1.5, droop=-6.0)


def ungulate_die(c, s, t):
    buckle = gait.ease_in_out(t, 0.0, 0.45)
    fall = gait.ease_in_out(t, 0.3, 0.78)
    limp = gait.ease_in_out(t, 0.2, 0.55)
    height = c.leg_length("hind.L")
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-20.0 * limp))
        c.bone_rotation(f"shin.{side}", pitch(30.0 * limp))
        c.bone_rotation(f"cannon.{side}", pitch(-20.0 * limp))
        c.bone_rotation(f"upperarm.{side}", pitch(18.0 * limp))
        c.bone_rotation(f"forearm.{side}", pitch(-28.0 * limp))
        c.bone_rotation(f"fcannon.{side}", pitch(25.0 * limp))
    angle = 84.0 * fall
    c.bone_rotation("root", roll(angle))
    # How high the fallen body rests, as a share of the leg: a long leg lifts a body off the ground if it keeps the usual share.
    lies = s["die"].get("restShare", 0.5)
    c.bone_offset("root", (0, 0, max(0.0, lies * height - height * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, 0, -0.3 * height * buckle))
    c.bone_rotation("neck", pitch(-18.0 * fall) @ yaw(-14.0 * fall))
    c.bone_rotation("neck2", pitch(-10.0 * fall))
    c.bone_rotation("head", pitch(-12.0 * fall))
    c.bone_rotation("jaw", pitch(-8.0 * fall))


def ungulate_bolt(c, s, t):
    """The instant before flight: the head flings up, the hindquarters sink and drive."""
    set_ = gait.rise_hold_fall(t, 0.35, 0.8)
    c.bone_rotation("chest", pitch(9.0 * set_))
    c.bone_rotation("neck", pitch(26.0 * set_))
    c.bone_rotation("neck2", pitch(10.0 * set_))
    c.bone_rotation("head", pitch(-12.0 * set_))
    c.bone_offset("hips", (0, 0, -0.08 * set_))
    c.place("fore.L", up=0.16 * set_, forward=0.08 * set_, toe_pitch=-40.0 * set_)
    ungulate_tail(c, t, 3.0, droop=-18.0 * set_)


def ungulate_bellow(c, s, t):
    """The stag's call: neck out and low, muzzle lifted, mouth open and shaking with the sound."""
    call = gait.rise_hold_fall(t, 0.25, 0.75)
    shake = gait.wave(t, 24) * call
    c.bone_rotation("chest", pitch(-3.0 * call))
    c.bone_rotation("neck", pitch(-12.0 * call))
    c.bone_rotation("neck2", pitch(8.0 * call))
    c.bone_rotation("head", pitch(30.0 * call + 1.5 * shake))
    c.bone_rotation("jaw", pitch(-26.0 * call - 3.0 * shake))
    ungulate_tail(c, t, 1.5)


def ungulate_gape(c, s, t):
    """The entelodont's warning: the head swings side to side and the jaws gape."""
    swing = gait.wave(t, 2)
    open_ = 0.5 + 0.5 * gait.wave(t, 2, lag=0.25)
    c.bone_rotation("neck", pitch(-6.0) @ yaw(14.0 * swing))
    c.bone_rotation("neck2", yaw(12.0 * swing))
    c.bone_rotation("head", pitch(6.0 * open_) @ yaw(16.0 * gait.wave(t, 2, lag=0.08)))
    c.bone_rotation("jaw", pitch(-42.0 * open_))
    c.bone_offset("hips", (0, 0, -0.03))


def ungulate_threat(c, s, t):
    """A hunter's threat: the head rears up and back and the jaws gape as far as they go, shaking."""
    up = gait.rise_hold_fall(t, 0.18, 0.85)
    shake = 0.5 + 0.5 * gait.wave(t, 8)
    c.bone_rotation("chest", pitch(5.0 * up))
    c.bone_rotation("neck", pitch(16.0 * up) @ yaw(5.0 * up * gait.wave(t, 2)))
    c.bone_rotation("neck2", pitch(20.0 * up))
    c.bone_rotation("head", pitch(14.0 * up) @ yaw(3.0 * up * gait.wave(t, 8)))
    c.bone_rotation("jaw", pitch((-46.0 - 5.0 * shake) * up))
    c.bone_offset("hips", (0, -0.1 * up, -0.08 * up))


def ungulate_bite(c, s, t):
    """A lunge: the neck draws back with the jaws wide, strikes out and down, and they snap shut."""
    back = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.34, 0.46))
    strike = gait.ease_in_out(t, 0.36, 0.48) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    wide = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.46, 0.54))
    shut = gait.ease_in_out(t, 0.46, 0.54) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    c.bone_rotation("chest", pitch(4.0 * back - 7.0 * strike))
    c.bone_rotation("neck", pitch(16.0 * back - 16.0 * strike))
    c.bone_rotation("neck2", pitch(14.0 * back - 10.0 * strike))
    c.bone_rotation("head", pitch(-8.0 * back - 10.0 * strike))
    c.bone_rotation("jaw", pitch(-44.0 * wide + 7.0 * shut))
    c.bone_offset("hips", (0, -0.22 * back + 0.42 * strike, -0.1 * strike))


def ungulate_hurt(c, s, t):
    """A blow lands: it flinches off to one side and shudders back."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_rotation("chest", roll(5.0 * hit))
    c.bone_rotation("neck", pitch(10.0 * hit) @ yaw(-16.0 * hit))
    c.bone_rotation("neck2", yaw(-12.0 * hit))
    c.bone_rotation("head", pitch(10.0 * hit))
    c.bone_rotation("jaw", pitch(-30.0 * hit))
    c.bone_offset("hips", (0, -0.12 * hit, -0.05 * hit))


def ungulate_rear(c, s, t):
    """Up on the hind legs, the forelegs hooked out in front: a chalicothere reaching for a branch, or warning."""
    up = gait.rise_hold_fall(t, 0.3, 0.75)
    hook = gait.wave(t, 3) * up
    degrees = s["rear"]["degrees"]
    for leg in ("fore.L", "fore.R"):
        c.leg_influence(leg, 1.0 - up)
    c.bone_rotation("hips", pitch(degrees * up))
    c.bone_offset("hips", (0, 0, -s["rear"]["sinkMetres"] * up))
    c.bone_rotation("neck", pitch(-0.45 * degrees * up))
    c.bone_rotation("neck2", pitch(-0.2 * degrees * up))
    c.bone_rotation("head", pitch(-0.25 * degrees * up))
    for side in SIDES:
        c.bone_rotation(f"upperarm.{side}", pitch(28.0 * up + 7.0 * hook))
        c.bone_rotation(f"forearm.{side}", pitch(-46.0 * up - 9.0 * hook))
    ungulate_tail(c, t, 1.5)


def ungulate_shove(c, s, t):
    """Standing its ground: the head goes down, horn first, and thrusts up twice."""
    low = gait.rise_hold_fall(t, 0.2, 0.85)
    thrust = max(0.0, gait.wave(t, 2)) * low
    c.bone_rotation("chest", pitch(-3.0 * low))
    c.bone_rotation("neck", pitch(-16.0 * low))
    c.bone_rotation("neck2", pitch(-8.0 * low + 6.0 * thrust))
    c.bone_rotation("head", pitch(-14.0 * low + 16.0 * thrust))
    c.bone_offset("hips", (0, 0.06 * thrust, -0.03 * low))
    ungulate_tail(c, t, 2.0)


def ungulate_spar(c, s, t):
    """A male's blow thrown at the air: the neck swings wide and the heavy head follows it round."""
    swing = gait.wave(t, 2) * gait.rise_hold_fall(t, 0.15, 0.85)
    c.bone_rotation("neck", pitch(-8.0) @ yaw(22.0 * swing))
    c.bone_rotation("neck2", yaw(18.0 * swing))
    c.bone_rotation("head", pitch(-10.0) @ yaw(14.0 * gait.wave(t, 2, lag=0.1)))
    ungulate_tail(c, t, 2.0)


def ungulate_ledge_hop(c, s, t):
    """A small goat's spring onto a ledge: it gathers, leaves the ground, and lands with its feet under it."""
    gather = gait.ease_in_out(t, 0.0, 0.25) * (1.0 - gait.ease_in_out(t, 0.25, 0.4))
    air = gait.rise_hold_fall(t, 0.35, 0.72)
    lift = s["ledge-hop"]["metres"] * air
    c.bone_offset("root", (0, 0, lift))
    c.bone_offset("hips", (0, 0, -0.08 * gather))
    c.bone_rotation("root", pitch(14.0 * (air - gait.ease_in_out(t, 0.55, 0.8))))
    for leg in c.targets:
        kind = leg.split(".")[0]
        c.place(leg, up=lift + (0.1 * air if kind == "fore" else 0.03 * air), forward=0.06 * air, toe_pitch=-25.0 * air)
    c.bone_rotation("neck", pitch(10.0 * gather + 6.0 * air))
    ungulate_tail(c, t, 2.0, droop=-10.0 * air)


UNGULATE_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (ungulate_idle, True, True),
    "walk": (ungulate_walk, True, True),
    "run": (ungulate_run, True, True),
    "graze": (ungulate_graze, True, True),
    "alert": (ungulate_alert, True, True),
    "die": (ungulate_die, False, False),
    "bolt": (ungulate_bolt, False, False),
    "bellow": (ungulate_bellow, False, True),
    "gape": (ungulate_gape, True, True),
    "threat": (ungulate_threat, False, True),
    "bite": (ungulate_bite, False, True),
    "hurt": (ungulate_hurt, False, True),
    "rear": (ungulate_rear, False, False),
    "shove": (ungulate_shove, False, True),
    "spar": (ungulate_spar, False, True),
    "ledge-hop": (ungulate_ledge_hop, False, False),
}


# ---------------------------------------------------------------- what two-bone families share

def plain_legs(side, sign):
    """Two long segments and a foot: the columns of a mammoth, the short sprawl of a glyptodon."""
    return [
        (f"thigh.{side}", "hips", "hip_socket", "knee", sign),
        (f"shin.{side}", f"thigh.{side}", "knee", "ankle", sign),
        (f"foot.{side}", f"shin.{side}", "ankle", "toe", sign),
        (f"upperarm.{side}", "chest", "shoulder", "elbow", sign),
        (f"forearm.{side}", f"upperarm.{side}", "elbow", "wrist", sign),
        (f"hand.{side}", f"forearm.{side}", "wrist", "knuckle", sign),
    ]


def trunk_body():
    return [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
        ("jaw", "head", "jaw", "jaw_tip", 1.0),
    ]


def tail_chain(count):
    """A tail of so many bones, tail1 on the hips, along the points tail0 to tail<count>."""
    return [(f"tail{i}", "hips" if i == 1 else f"tail{i - 1}", f"tail{i - 1}", f"tail{i}", 1.0) for i in range(1, count + 1)]


def tail_names(c):
    return [name for name in c.rig.pose.bones.keys() if name.startswith("tail")]


def swing_tail(c, t, swish, droop=0.0):
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, pitch(droop) @ yaw(swish * i * gait.wave(t, lag=0.1 * i)))


def plain_gait(c, settings, t, spine_flex, rigid=False):
    """Feet on their trajectories through the two-bone chains; the body rides them."""
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        forward, up = gait.foot(phase, duty, stride, settings["liftHind" if leg.startswith("hind") else "liftFore"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-20.0 * swing)
    sway, bob = settings["swayDegrees"], settings["bob"]
    c.bone_offset("hips", (0, 0, -bob * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ yaw(0.6 * sway * gait.wave(t, lag=0.1)))
    if not rigid:
        c.bone_rotation("spine", yaw(-0.5 * sway * gait.wave(t, lag=0.15)) @ pitch(spine_flex * gait.wave(t, 2)))
        c.bone_rotation("chest", roll(-0.6 * sway * gait.wave(t, lag=0.2)))
    c.bone_rotation("neck", pitch(-2.0 * gait.wave(t, 2, lag=0.1)))
    c.bone_rotation("head", pitch(-3.0 * gait.wave(t, 2, lag=0.2)) @ yaw(-sway * gait.wave(t, lag=0.3)))
    swing_tail(c, t, 2.0)


def fall_over(c, t, height, thigh=-22.0, knee=32.0):
    """Limbs give, and the body rolls onto its side."""
    fall = gait.ease_in_out(t, 0.3, 0.78)
    limp = gait.ease_in_out(t, 0.2, 0.55)
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(thigh * limp))
        c.bone_rotation(f"shin.{side}", pitch(knee * limp))
        c.bone_rotation(f"upperarm.{side}", pitch(-0.8 * thigh * limp))
        c.bone_rotation(f"forearm.{side}", pitch(-knee * limp))
    angle = 82.0 * fall
    c.bone_rotation("root", roll(angle))
    c.bone_offset("root", (0, 0, max(0.0, 0.5 * height - height * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, 0, -0.3 * height * gait.ease_in_out(t, 0.0, 0.45)))
    c.bone_rotation("neck", pitch(-20.0 * fall) @ yaw(-14.0 * fall))
    c.bone_rotation("head", pitch(-12.0 * fall))
    c.bone_rotation("jaw", pitch(-8.0 * fall))


# ---------------------------------------------------------------- graviportal: mammoths

TRUNK_BONES = 6


def graviportal_bones():
    bones = trunk_body() + tail_chain(3)
    bones += [(f"trunk{i}", "head" if i == 1 else f"trunk{i - 1}", f"trunk{i - 1}", f"trunk{i}", 1.0) for i in range(1, TRUNK_BONES + 1)]
    for side, sign in SIDES.items():
        bones += [(f"ear.{side}", "head", "ear", "ear_tip", sign)]
        bones += plain_legs(side, sign)
    return bones


def set_trunk(c, curl, sway=0.0, t=0.0):
    """Curl in degrees at the tip's bone, less toward the root. Each bone adds to the last, so the whole trunk turns about 3.5 times this. Positive lifts the tip forward and up; negative tucks it back under, to the mouth."""
    for i in range(1, TRUNK_BONES + 1):
        share = i / TRUNK_BONES
        c.bone_rotation(f"trunk{i}", pitch(curl * share) @ yaw(sway * share * gait.wave(t, lag=0.07 * i)))


def set_ears(c, out):
    for side, sign in SIDES.items():
        c.bone_rotation(f"ear.{side}", yaw(-sign * out))


def graviportal_walk(c, s, t):
    plain_gait(c, s["walk"], t, spine_flex=0.5)
    set_trunk(c, 6.0 * gait.wave(t, 2), sway=6.0, t=t)
    set_ears(c, 5.0 * gait.wave(t, 2))


def graviportal_run(c, s, t):
    plain_gait(c, s["run"], t, spine_flex=1.5)
    set_trunk(c, 8.0, sway=9.0, t=t)
    set_ears(c, 16.0)


def graviportal_idle(c, s, t):
    c.bone_rotation("neck", pitch(1.5 * gait.wave(t)) @ yaw(3.0 * gait.wave(t, lag=0.25)))
    set_trunk(c, 5.0 * gait.wave(t, 2), sway=8.0, t=t)
    set_ears(c, 7.0 * max(0.0, gait.wave(t, 3)))
    swing_tail(c, t, 3.0)


def graviportal_graze(c, s, t):
    """Trunk feeding: down to pluck a tuft, then curled up to the mouth."""
    lift = gait.rise_hold_fall(t, 0.45, 0.8)
    c.bone_rotation("neck", pitch(-7.0 + 4.0 * lift))
    c.bone_rotation("head", pitch(-8.0 + 6.0 * lift))
    set_trunk(c, 3.0 - 45.0 * lift, sway=3.0, t=t)
    c.bone_rotation("jaw", pitch(-9.0 * lift))
    set_ears(c, 4.0 * gait.wave(t, 4))
    swing_tail(c, t, 3.0)


def graviportal_alert(c, s, t):
    c.bone_rotation("neck", pitch(6.0) @ yaw(8.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(6.0) @ yaw(7.0 * gait.wave(t, lag=0.1)))
    set_trunk(c, 22.0, sway=10.0, t=t)
    set_ears(c, 24.0)
    swing_tail(c, t, 1.5, droop=-5.0)


def graviportal_threat(c, s, t):
    """Holding its ground: head tossed, tusks lifted, trunk thrown up and ears spread."""
    toss = gait.rise_hold_fall(t, 0.25, 0.7)
    c.bone_rotation("chest", pitch(3.0 * toss))
    c.bone_rotation("neck", pitch(10.0 * toss) @ yaw(10.0 * gait.wave(t, 2)))
    c.bone_rotation("head", pitch(16.0 * toss))
    set_trunk(c, 38.0 * toss, sway=8.0, t=t)
    set_ears(c, 34.0 * toss)
    c.bone_rotation("jaw", pitch(-14.0 * toss))


def graviportal_die(c, s, t):
    fall_over(c, t, c.leg_length("hind.L"))
    set_trunk(c, -8.0 * gait.ease_in_out(t, 0.3, 0.8))


GRAVIPORTAL_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (graviportal_idle, True, True),
    "walk": (graviportal_walk, True, True),
    "run": (graviportal_run, True, True),
    "graze": (graviportal_graze, True, True),
    "alert": (graviportal_alert, True, True),
    "threat": (graviportal_threat, False, True),
    "die": (graviportal_die, False, False),
}


# ---------------------------------------------------------------- low-slung: glyptodons, beavers

def low_slung_bones():
    """A rigid shell is a body that keeps its spine still: the species says so, not the skeleton."""
    bones = trunk_body() + tail_chain(4)
    for side, sign in SIDES.items():
        bones += plain_legs(side, sign)
    return bones


def low_slung_walk(c, s, t):
    plain_gait(c, s["walk"], t, spine_flex=s.get("spineFlex", 0.0), rigid=s.get("rigid", False))


def low_slung_run(c, s, t):
    plain_gait(c, s["run"], t, spine_flex=2.0 * s.get("spineFlex", 0.0), rigid=s.get("rigid", False))


def low_slung_idle(c, s, t):
    c.bone_rotation("neck", pitch(2.0 * gait.wave(t)) @ yaw(6.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", pitch(2.0 * gait.wave(t, 2)) @ yaw(5.0 * gait.wave(t, lag=0.3)))
    swing_tail(c, t, 1.0)


def low_slung_graze(c, s, t):
    c.bone_rotation("neck", pitch(s["graze"].get("neckDegrees", -20.0)))
    c.bone_rotation("head", pitch(s["graze"].get("headDegrees", -22.0) + 4.0 * gait.wave(t, 5)))
    c.bone_rotation("jaw", pitch(-10.0 * (0.5 + 0.5 * gait.wave(t, 7))))
    swing_tail(c, t, 1.0)


def low_slung_alert(c, s, t):
    c.bone_rotation("neck", pitch(12.0) @ yaw(10.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(6.0) @ yaw(8.0 * gait.wave(t, lag=0.1)))
    swing_tail(c, t, 0.5, droop=-3.0)


def low_slung_hunker(c, s, t):
    """Down into the shell: legs draw in, the body settles, and the head comes under the rim."""
    down = gait.ease_in_out(t, 0.0, 0.6)
    drop = s["hunker"]["metres"] * down
    for leg in c.targets:
        c.place(leg, side=-0.08 * down, up=0.0)
    c.bone_offset("root", (0, 0, -drop))
    for leg in c.targets:
        c.place(leg, up=drop, side=-0.08 * down)
    c.bone_rotation("neck", pitch(-30.0 * down))
    c.bone_rotation("head", pitch(-28.0 * down))
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, yaw(5.0 * i * down))


def low_slung_slip(c, s, t):
    """Slipping into water: the front tips down and slides ahead, the tail lifting behind."""
    slide = gait.ease_in_out(t, 0.05, 0.75)
    tip = s["slip"]["pitchDegrees"] * slide
    c.bone_rotation("root", pitch(-tip))
    c.bone_offset("root", (0, s["slip"]["metres"] * slide, -0.5 * s["slip"]["metres"] * slide))
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - gait.ease_in_out(t, 0.0, 0.3))
    for side in SIDES:
        c.bone_rotation(f"upperarm.{side}", pitch(-30.0 * slide))
        c.bone_rotation(f"thigh.{side}", pitch(-40.0 * slide))
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, pitch(-5.0 * i * slide))


def low_slung_die(c, s, t):
    if s.get("rigid", False):
        # A shell does not roll: the legs give and it settles where it stood.
        low_slung_hunker(c, {"hunker": s["die"]}, t)
        c.bone_rotation("head", pitch(-40.0 * gait.ease_in_out(t, 0.2, 0.7)))
        return
    fall_over(c, t, c.leg_length("hind.L"))


LOW_SLUNG_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (low_slung_idle, True, True),
    "walk": (low_slung_walk, True, True),
    "run": (low_slung_run, True, True),
    "graze": (low_slung_graze, True, True),
    "alert": (low_slung_alert, True, True),
    "hunker": (low_slung_hunker, False, False),
    "slip": (low_slung_slip, False, False),
    "die": (low_slung_die, False, False),
}


# ---------------------------------------------------------------- bird: two legs and a long neck

def bird_bones():
    """The ungulate's hind legs under a long neck: the top segment is the thigh, then shin, tarsus (cannon) and toes."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "neck2", 1.0),
        ("neck2", "neck", "neck2", "neck3", 1.0),
        ("neck3", "neck2", "neck3", "head", 1.0),
        ("head", "neck3", "head", "nose", 1.0),
        ("jaw", "head", "jaw", "jaw_tip", 1.0),
        ("tail1", "hips", "tail0", "tail1", 1.0),
        ("tail2", "tail1", "tail1", "tail2", 1.0),
        ("tail3", "tail2", "tail2", "tail3", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"wing.{side}", "chest", "shoulder", "wing_tip", sign),
            (f"thigh.{side}", "hips", "hip_socket", "stifle", sign),
            (f"shin.{side}", f"thigh.{side}", "stifle", "hock", sign),
            (f"cannon.{side}", f"shin.{side}", "hock", "fetlock", sign),
            (f"hoof.{side}", f"cannon.{side}", "fetlock", "toe", sign),
        ]
    return bones


BIRD_LEGS = {"hind": ("cannon", "hoof", "hock", -1.0)}


def bird_gait(c, settings, t, spine_flex):
    """Two legs in turn: the body swings over the foot that stands, and the neck pumps once a stride to balance it."""
    ungulate_gait(c, settings, t, spine_flex)
    nod = settings.get("nodDegrees", 3.0)
    c.bone_offset("hips", (settings.get("swingMetres", 0.1) * gait.wave(t), 0, -settings["bob"] * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("neck3", pitch(-0.8 * nod * gait.wave(t, 2, lag=0.25)) @ yaw(0.5 * settings["swayDegrees"] * gait.wave(t, lag=0.3)))


def bird_walk(c, s, t):
    bird_gait(c, s["walk"], t, spine_flex=0.8)


def bird_run(c, s, t):
    bird_gait(c, s["run"], t, spine_flex=s["run"].get("spineFlex", 3.0))


def bird_idle(c, s, t):
    ungulate_idle(c, s, t)
    c.bone_rotation("neck3", pitch(1.5 * gait.wave(t, lag=0.4)) @ yaw(4.0 * gait.wave(t, lag=0.35)))


def bird_alert(c, s, t):
    ungulate_alert(c, s, t)
    c.bone_rotation("neck3", pitch(-8.0) @ yaw(8.0 * gait.wave(t, lag=0.12)))


def bird_threat(c, s, t):
    """The neck rears with the head kept level, the beak gapes and the useless wings lift off the body."""
    ungulate_threat(c, s, t)
    up = gait.rise_hold_fall(t, 0.18, 0.85)
    shake = 0.5 + 0.5 * gait.wave(t, 8)
    c.bone_rotation("neck3", pitch(-6.0 * up))
    c.bone_rotation("head", pitch(-14.0 * up) @ yaw(3.0 * up * gait.wave(t, 8)))
    # The beak's rest is already half open.
    c.bone_rotation("jaw", pitch((-20.0 - 4.0 * shake) * up))
    for side, sign in SIDES.items():
        c.bone_rotation(f"wing.{side}", yaw(-sign * 28.0 * up))


def bird_bite(c, s, t):
    """A hatchet blow: the neck draws back with the beak wide, the head comes down through the target and the beak shuts."""
    ungulate_bite(c, s, t)
    back = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.34, 0.46))
    strike = gait.ease_in_out(t, 0.36, 0.48) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    wide = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.46, 0.54))
    shut = gait.ease_in_out(t, 0.46, 0.54) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    c.bone_rotation("neck3", pitch(-4.0 * back - 18.0 * strike))
    c.bone_rotation("head", pitch(-16.0 * back - 10.0 * strike))
    c.bone_rotation("jaw", pitch(-18.0 * wide + s["bite"].get("shutDegrees", 26.0) * shut))


def bird_hurt(c, s, t):
    ungulate_hurt(c, s, t)
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_rotation("neck3", yaw(-10.0 * hit))


def bird_die(c, s, t):
    """The legs go and it drops onto its side, the neck following it down."""
    limp = gait.ease_in_out(t, 0.1, 0.5)
    fall = gait.ease_in_out(t, 0.25, 0.8)
    height = c.leg_length("hind.L")
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-25.0 * limp))
        c.bone_rotation(f"shin.{side}", pitch(45.0 * limp))
        c.bone_rotation(f"cannon.{side}", pitch(-35.0 * limp))
    angle = 80.0 * fall
    c.bone_rotation("root", roll(angle))
    lies = s["die"].get("restShare", 0.5)
    c.bone_offset("root", (0, 0, max(0.0, lies * height - height * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, 0, -0.3 * height * limp))
    c.bone_rotation("neck", pitch(-12.0 * fall) @ yaw(-16.0 * fall))
    c.bone_rotation("neck2", pitch(-8.0 * fall) @ yaw(-12.0 * fall))
    c.bone_rotation("neck3", pitch(-8.0 * fall))
    c.bone_rotation("head", pitch(-10.0 * fall))
    c.bone_rotation("jaw", pitch(-8.0 * fall))


BIRD_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (bird_idle, True, True),
    "walk": (bird_walk, True, True),
    "run": (bird_run, True, True),
    "alert": (bird_alert, True, True),
    "die": (bird_die, False, False),
    "threat": (bird_threat, False, True),
    "bite": (bird_bite, False, True),
    "hurt": (bird_hurt, False, True),
}


# ---------------------------------------------------------------- theropod: a bird's legs, arms and a long tail

THEROPOD_TAIL = tuple(f"tail{index}" for index in range(1, 7))


def theropod_bones():
    """The bird's body and legs, with two small arms in place of wings and a tail long enough to balance on."""
    bones = [bone for bone in bird_bones() if not bone[0].startswith("wing")]
    bones += [(f"tail{i}", f"tail{i - 1}", f"tail{i - 1}", f"tail{i}", 1.0) for i in range(4, 7)]
    for side, sign in SIDES.items():
        bones += [
            (f"upperarm.{side}", "chest", "shoulder", "elbow", sign),
            (f"forearm.{side}", f"upperarm.{side}", "elbow", "wrist", sign),
            (f"hand.{side}", f"forearm.{side}", "wrist", "hand_tip", sign),
        ]
    return bones


def theropod_tail(c, t, swing, droop=0.0):
    """The tail swings against the stride, each bone a little behind the last and further out."""
    for index, bone in enumerate(THEROPOD_TAIL):
        c.bone_rotation(bone, yaw(swing * gait.wave(t, 1, lag=0.08 * index)) @ pitch(droop))


def theropod_arms(c, reach, wave=0.0):
    """The arms draw up and out from the chest by `reach` degrees, with a little `wave` on top."""
    for side in SIDES:
        c.bone_rotation(f"upperarm.{side}", pitch(reach + wave))
        c.bone_rotation(f"forearm.{side}", pitch(0.8 * reach))


def theropod_walk(c, s, t):
    bird_walk(c, s, t)
    theropod_tail(c, t, 1.5 * s["walk"]["swayDegrees"])
    theropod_arms(c, 8.0, 3.0 * gait.wave(t, 2))


def theropod_run(c, s, t):
    bird_run(c, s, t)
    theropod_tail(c, t, 1.2 * s["run"]["swayDegrees"], droop=s["run"].get("tailLift", 0.0) / 3.0)
    theropod_arms(c, 26.0, 4.0 * gait.wave(t, 2))


def theropod_idle(c, s, t):
    bird_idle(c, s, t)
    theropod_tail(c, t, 3.0)
    theropod_arms(c, 6.0, 1.5 * gait.wave(t))


def theropod_alert(c, s, t):
    bird_alert(c, s, t)
    theropod_tail(c, t, 1.5, droop=-2.0)
    theropod_arms(c, 16.0)


def theropod_threat(c, s, t):
    """The neck rears with the head kept level, the jaws gape, the arms open and the tail lashes."""
    ungulate_threat(c, s, t)
    up = gait.rise_hold_fall(t, 0.18, 0.85)
    shake = 0.5 + 0.5 * gait.wave(t, 8)
    c.bone_rotation("neck3", pitch(-6.0 * up))
    c.bone_rotation("head", pitch(-14.0 * up) @ yaw(3.0 * up * gait.wave(t, 8)))
    c.bone_rotation("jaw", pitch((-22.0 - 4.0 * shake) * up))
    theropod_arms(c, 40.0 * up)
    theropod_tail(c, t * 3.0, 6.0 * up)


def theropod_bite(c, s, t):
    bird_bite(c, s, t)
    strike = gait.ease_in_out(t, 0.36, 0.48) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    theropod_arms(c, 34.0 * strike)


THEROPOD_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (theropod_idle, True, True),
    "walk": (theropod_walk, True, True),
    "run": (theropod_run, True, True),
    "alert": (theropod_alert, True, True),
    "die": (bird_die, False, False),
    "threat": (theropod_threat, False, True),
    "bite": (theropod_bite, False, True),
    "hurt": (bird_hurt, False, True),
}


# ---------------------------------------------------------------- the table

@dataclass(frozen=True)
class Family:
    bones: object
    legs: dict
    clips: dict
    # The joints the product's two-bone IK moves, hind then fore, and the points its hind and fore feet stand at.
    chains: tuple
    hind_point: str
    fore_point: str
    # Bone name stems drawn as legs and as claw or hoof on a stand-in body.
    leg_bones: tuple = field(default=())
    tip_bones: tuple = field(default=())


FAMILIES = {
    "plantigrade": Family(plantigrade_bones, PLANTIGRADE_LEGS, PLANTIGRADE_CLIPS,
                          (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                          ("thigh", "shin", "foot", "upperarm", "forearm", "hand"), ("claw",)),
    "ungulate": Family(ungulate_bones, UNGULATE_LEGS, UNGULATE_CLIPS,
                       (("shin", "cannon", "hoof"), ("forearm", "fcannon", "fhoof")), "fetlock", "ffetlock",
                       ("thigh", "shin", "cannon", "upperarm", "forearm", "fcannon"), ("hoof", "fhoof")),
    "graviportal": Family(graviportal_bones, PLANTIGRADE_LEGS, GRAVIPORTAL_CLIPS,
                          (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                          ("thigh", "shin", "upperarm", "forearm"), ("foot", "hand")),
    "bird": Family(bird_bones, BIRD_LEGS, BIRD_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                   ("thigh", "shin", "cannon"), ("hoof",)),
    "theropod": Family(theropod_bones, BIRD_LEGS, THEROPOD_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                       ("thigh", "shin", "cannon"), ("hoof", "hand")),
    "low-slung": Family(low_slung_bones, PLANTIGRADE_LEGS, LOW_SLUNG_CLIPS,
                        (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                        ("thigh", "shin", "upperarm", "forearm"), ("foot", "hand")),
}
