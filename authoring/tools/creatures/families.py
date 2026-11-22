"""The rig families (#10570): each family's skeleton, legs and clips, for build_creature.py.

A family is what animals built alike share. A species file picks one and places its joints. Clips
are pose functions over a Controls object: `pose(c, settings, t)` for t from 0 to 1 across the clip.
A family's clip table gives each clip its pose, whether it loops, and whether all its feet stay on
the ground; a species bakes the ones it has settings for, and `rest` always.
"""
import math
from dataclasses import dataclass, field

from mathutils import Matrix, Vector

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


def spread(side, degrees):
    """Swings a limb out from the body on its own side."""
    return roll(degrees if side == "L" else -degrees)


def plantigrade_threat(c, s, t):
    """A cave hunter's display (#10877): up on the hind legs, forelimbs raised and spread with the claws out, jaws wide."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.22, 0.8)
    shake = gait.wave(t, 7)
    rear(c, up, settings["pitchDegrees"])
    for side in SIDES:
        c.leg_influence(f"fore.{side}", 1.0 - gait.ease_in_out(t, 0.0, 0.16) * (1.0 - gait.ease_in_out(t, 0.84, 1.0)))
        c.bone_rotation(f"upperarm.{side}", pitch(settings.get("armDegrees", 95.0) * up + 6.0 * shake * up) @ spread(side, settings.get("spreadDegrees", 25.0) * up))
        c.bone_rotation(f"forearm.{side}", pitch(-25.0 * up))
        c.bone_rotation(f"hand.{side}", pitch(-20.0 * up))
        c.bone_rotation(f"claw.{side}", pitch(-35.0 * up))
    c.bone_rotation("neck", pitch(-0.3 * settings["pitchDegrees"] * up + 6.0 * up))
    c.bone_rotation("head", pitch(8.0 * up) @ yaw(4.0 * up * shake))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 32.0) * up - 3.0 * up * shake))


def plantigrade_bite(c, s, t):
    """A lunge (#10877): the body draws back, throws itself forward with the jaws wide, they snap shut;
    with `swipe`, the right forelimb rakes down as it lands."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.32, 0.44))
    strike = gait.ease_in_out(t, 0.34, 0.46) * (1.0 - gait.ease_in_out(t, 0.7, 1.0))
    wide = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.44, 0.52))
    shut = gait.ease_in_out(t, 0.44, 0.52) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    reach = settings.get("lungeMetres", 0.35)
    c.bone_offset("hips", (0, 0.4 * reach * back - reach * strike, -0.06 * strike))
    c.bone_rotation("spine", pitch(3.0 * back - 4.0 * strike))
    c.bone_rotation("chest", pitch(5.0 * back - 8.0 * strike))
    c.bone_rotation("neck", pitch(8.0 * back - 8.0 * strike))
    c.bone_rotation("head", pitch(6.0 * back - 6.0 * strike))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 32.0) * wide + 4.0 * shut))
    if settings.get("swipe"):
        lift = gait.ease_in_out(t, 0.05, 0.34) * (1.0 - gait.ease_in_out(t, 0.4, 0.5))
        rake = gait.ease_in_out(t, 0.4, 0.5) * (1.0 - gait.ease_in_out(t, 0.75, 0.95))
        c.leg_influence("fore.R", 1.0 - gait.ease_in_out(t, 0.02, 0.15) * (1.0 - gait.ease_in_out(t, 0.8, 0.95)))
        c.bone_rotation("upperarm.R", pitch(85.0 * lift + 20.0 * rake) @ spread("R", 15.0 * lift))
        c.bone_rotation("forearm.R", pitch(25.0 * lift - 15.0 * rake))
        c.bone_rotation("claw.R", pitch(-30.0 * lift + 30.0 * rake))


def plantigrade_hurt(c, s, t):
    """A blow lands (#10877): it flinches back and away and shudders."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.12 * hit, -0.04 * hit))
    c.bone_rotation("chest", roll(6.0 * hit))
    c.bone_rotation("neck", pitch(8.0 * hit) @ yaw(-14.0 * hit))
    c.bone_rotation("head", pitch(8.0 * hit) @ yaw(-8.0 * hit))
    c.bone_rotation("jaw", pitch(-22.0 * hit))


def plantigrade_lie(c, s, t):
    """At rest, flat to the ground (#10877): the body sinks between feet that stay put, the head down on its
    forepaws, breathing slowly, so a mouldback lies as one more patch of fungus."""
    settings = s["lie"]
    sink = settings["sinkMetres"]
    breathe = gait.wave(t)
    for leg in c.targets:
        c.place(leg, side=settings.get("sprawlMetres", 0.08))
    c.bone_offset("hips", (0, 0, -sink))
    c.bone_rotation("spine", pitch(-2.0 + 0.6 * breathe))
    c.bone_rotation("chest", pitch(-settings.get("chestDegrees", 4.0)) @ pitch(0.6 * breathe))
    c.bone_rotation("neck", pitch(-settings.get("headDegrees", 12.0)))
    c.bone_rotation("head", pitch(-0.4 * settings.get("headDegrees", 12.0)))
    c.bone_rotation("jaw", pitch(1.5 * (0.5 + 0.5 * breathe)))


def hanging(c, s, amount, t):
    """Upside down from the ground plane, back to the floor: the feet's rest height becomes the grip, at the origin,
    which the product sets at the ceiling (#10877). The limbs go free of their targets and reach straight up."""
    settings = s["hang"]
    c.bone_rotation("root", roll(180.0 * amount))
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - amount)
    sway = gait.wave(t)
    for side in SIDES:
        c.bone_rotation(f"upperarm.{side}", pitch(settings.get("armDegrees", -18.0) * amount))
        c.bone_rotation(f"thigh.{side}", pitch(settings.get("legDegrees", 12.0) * amount))
        c.bone_rotation(f"claw.{side}", pitch(-50.0 * amount))
    c.bone_rotation("hips", roll(2.5 * sway * amount))
    c.bone_rotation("chest", roll(-2.0 * sway * amount))
    c.bone_rotation("neck", pitch(settings.get("lookDegrees", 30.0) * amount) @ yaw(10.0 * gait.wave(t, lag=0.3) * amount))
    c.bone_rotation("head", pitch(0.5 * settings.get("lookDegrees", 30.0) * amount))


def plantigrade_hang(c, s, t):
    """At rest hanging from the ceiling (#10877: the drop-sloth), swaying a little, looking down at the floor."""
    hanging(c, s, 1.0, t)


def plantigrade_drop(c, s, t):
    """Letting go (#10877): it turns over as the product lowers it from the ceiling, the limbs flailing, and lands on all
    fours. The turn is done by 0.6 of the clip, the feet back on their targets by 0.9."""
    turn = gait.ease_in_out(t, 0.0, 0.6)
    c.bone_rotation("root", roll(180.0 + 180.0 * turn))
    land = gait.ease_in_out(t, 0.5, 0.9)
    for leg in c.targets:
        c.leg_influence(leg, land)
    flail = gait.wave(t, 3) * (1.0 - land)
    for side in SIDES:
        c.bone_rotation(f"upperarm.{side}", spread(side, 30.0 * (1.0 - land)) @ pitch(20.0 * flail))
        c.bone_rotation(f"thigh.{side}", pitch(-15.0 * flail))
        c.bone_rotation(f"claw.{side}", pitch(-30.0 * (1.0 - land)))
    c.bone_rotation("jaw", pitch(-20.0 * (1.0 - land)))


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
    "threat": (plantigrade_threat, False, False),
    "bite": (plantigrade_bite, False, False),
    "hurt": (plantigrade_hurt, False, True),
    "lie": (plantigrade_lie, True, True),
    "hang": (plantigrade_hang, True, False),
    "drop": (plantigrade_drop, False, False),
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
    """A hunter's threat: the head rears up and back and the jaws gape as far as they go, shaking.
    With `headLow` (#10878) it crouches instead: forequarters sunk, head low and thrust out, jaws wide."""
    up = gait.rise_hold_fall(t, 0.18, 0.85)
    shake = 0.5 + 0.5 * gait.wave(t, 8)
    if s["threat"].get("headLow"):
        crouch_threat(c, up, shake, t)
        return
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


def crouch_threat(c, up, shake, t):
    """A low threat (#10878): the forequarters sink and the head comes down and forward, jaws gaping, shaking."""
    c.bone_offset("hips", (0, -0.04 * up, -0.04 * up))
    c.bone_rotation("spine", pitch(-3.0 * up))
    c.bone_rotation("chest", pitch(-7.0 * up))
    c.bone_rotation("neck", pitch(-14.0 * up) @ yaw(4.0 * up * gait.wave(t, 2)))
    c.bone_rotation("neck2", pitch(6.0 * up))
    c.bone_rotation("head", pitch(16.0 * up) @ yaw(3.0 * up * gait.wave(t, 8)))
    c.bone_rotation("jaw", pitch((-44.0 - 5.0 * shake) * up))
    for leg in ("fore.L", "fore.R"):
        c.place(leg, forward=0.05 * up, side=0.04 * up)
    ungulate_tail(c, t, 1.0, droop=10.0 * up)


def fold_leg(c, side, bones, degrees):
    """Keys a leg's bones to pitches from hanging straight down (positive swings forward), each turned from its own rest
    direction and less what the bone above has turned, for a pose IK cannot fold."""
    turned = 0.0
    for bone, angle in zip(bones, degrees):
        rest = c.rig.data.bones[f"{bone}.{side}"]
        along = rest.tail_local - rest.head_local
        rest_angle = math.degrees(math.atan2(-along.y, -along.z))
        c.bone_rotation(f"{bone}.{side}", pitch(angle - rest_angle - turned))
        turned = angle - rest_angle


def ungulate_lie(c, s, t):
    """At rest, lying flat (#10878, the glass-courser): the body down on the ground, hind legs folded under it and the
    forelegs stretched out in front, head up and watching, breathing slowly. The legs are keyed, not IK'd: `hind` and
    `fore` give each segment's pitch from hanging straight down (positive swings forward), top to toe."""
    settings = s["lie"]
    breathe = gait.wave(t)
    for leg in c.targets:
        c.leg_influence(leg, 0.0)
    c.bone_offset("hips", (0, 0, -settings["sinkMetres"]))
    for side in SIDES:
        fold_leg(c, side, ("thigh", "shin", "cannon", "hoof"), settings["hind"])
        fold_leg(c, side, ("upperarm", "forearm", "fcannon", "fhoof"), settings["fore"])
    c.bone_rotation("spine", pitch(0.8 * breathe))
    c.bone_rotation("neck", pitch(settings.get("neckDegrees", 6.0)) @ yaw(4.0 * gait.wave(t, lag=0.2)))
    c.bone_rotation("head", pitch(-settings.get("neckDegrees", 6.0)) @ yaw(5.0 * gait.wave(t, lag=0.3)))
    ungulate_tail(c, t, 2.0, droop=settings.get("tailDroop", 4.0))


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
    "lie": (ungulate_lie, True, False),
}


# ---------------------------------------------------------------- ungulate with ear-fans: the draft-hound (#10878)

def ungulate_eared_bones():
    bones = ungulate_bones()
    for side, sign in SIDES.items():
        bones.append((f"ear.{side}", "head", "ear", "ear_tip", sign))
    return bones


def set_fan(c, back=0.0, down=0.0, forward=0.0, swivel=(0.0, 0.0)):
    """Poses both ear-fans from their rest, held into the draft: `back` swings each back towards the neck (flattened at
    about 50), `down` droops it, `forward` flares it to face ahead, and `swivel` turns each on its own (left, right)."""
    for (side, sign), turn in zip(SIDES.items(), swivel):
        c.bone_rotation(f"ear.{side}", yaw(sign * (back - forward + turn)) @ pitch(down))


def fan_still(c, s, t):
    set_fan(c, swivel=(3.0 * gait.wave(t, 2), -3.0 * gait.wave(t, 2, lag=0.3)))


def fan_run(c, s, t):
    set_fan(c, back=32.0, down=6.0)


def fan_flat(c, s, t):
    up = gait.rise_hold_fall(t, 0.15, 0.85)
    set_fan(c, back=52.0 * up, down=16.0 * up)


def fan_bite(c, s, t):
    set_fan(c, back=52.0, down=16.0)


def fan_hurt(c, s, t):
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    set_fan(c, back=40.0 * hit, down=10.0 * hit)


def fan_die(c, s, t):
    fall = gait.ease_in_out(t, 0.2, 0.8)
    set_fan(c, back=30.0 * fall, down=34.0 * fall)


def eared_search(c, s, t):
    """The draft-hound's search (#10878): it stands and flares its ear-fans wide and forward, turning each on its own,
    while the head comes up and sweeps slowly from side to side, nose lifted, testing the draft."""
    flare = gait.rise_hold_fall(t, 0.12, 0.9)
    sweep = gait.wave(t)
    c.bone_rotation("chest", pitch(3.0 * flare))
    c.bone_rotation("neck", pitch(14.0 * flare) @ yaw(14.0 * flare * sweep))
    c.bone_rotation("neck2", pitch(6.0 * flare) @ yaw(10.0 * flare * gait.wave(t, lag=0.08)))
    c.bone_rotation("head", pitch((10.0 + 4.0 * gait.wave(t, 6)) * flare) @ yaw(8.0 * flare * gait.wave(t, lag=0.15)))
    c.bone_rotation("jaw", pitch(-4.0 * flare * (0.5 + 0.5 * gait.wave(t, 6))))
    set_fan(c, forward=22.0 * flare, down=-6.0 * flare,
            swivel=(16.0 * flare * gait.wave(t, 2), -16.0 * flare * gait.wave(t, 2, lag=0.35)))
    ungulate_tail(c, t, 2.0, droop=-4.0 * flare)


EAR_POSES = {
    "rest": lambda c, s, t: None, "run": fan_run, "threat": fan_flat, "bite": fan_bite, "hurt": fan_hurt, "die": fan_die, "lie": fan_run,
}


def with_ears(pose, ears):
    def posed(c, s, t):
        pose(c, s, t)
        ears(c, s, t)
    return posed


UNGULATE_EARED_CLIPS = {
    name: (with_ears(pose, EAR_POSES.get(name, fan_still)), loops, plants)
    for name, (pose, loops, plants) in UNGULATE_CLIPS.items()
}
UNGULATE_EARED_CLIPS["search"] = (eared_search, True, True)


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

def low_slung_bones(points):
    """A rigid shell is a body that keeps its spine still: the species says so, not the skeleton. The tail is as long
    as the species draws it (tail1 to tailN). A species that places `dome_hinge` and `dome_hinge_tip` has a dome cut
    down the middle (#10880, the hummock): each half on its own bone along its hinge at the dome's side, on the spine."""
    bones = trunk_body() + tail_chain(sum(1 for name in points if name.startswith("tail") and name != "tail0"))
    for side, sign in SIDES.items():
        bones += plain_legs(side, sign)
        if "dome_hinge" in points:
            bones += [(f"dome.{side}", "spine", "dome_hinge", "dome_hinge_tip", sign)]
    return bones


def open_dome(c, degrees):
    """Both halves of a split dome heave apart about their hinges, the left half rolling left and the right half
    right. A species without a split dome has no halves to open."""
    if "dome.L" not in c.rig.pose.bones:
        return
    c.bone_rotation("dome.L", roll(-degrees))
    c.bone_rotation("dome.R", roll(degrees))


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


def low_slung_threat(c, s, t):
    """A threat display: the body braced low at the front, the head low or raised, the mouth wide and shaking. A split
    dome (the hummock) heaves open on its plates and pulses with breath (#10880)."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.25, 0.82)
    shake = gait.wave(t, 6)
    rear = settings.get("rearDegrees", 0.0)
    if rear:
        # The front rears off its forefeet about the hind feet (#10959, the threshold lurker bursting up, the wall runner),
        # the forelimbs raised under it and spread by `armSpreadDegrees`.
        for side in SIDES:
            c.leg_influence(f"fore.{side}", 1.0 - up)
            c.bone_rotation(f"upperarm.{side}", pitch(settings.get("armDegrees", -40.0) * up) @ spread(side, settings.get("armSpreadDegrees", 15.0) * up))
            c.bone_rotation(f"forearm.{side}", pitch(settings.get("forearmDegrees", 50.0) * up))
        c.bone_rotation("root", pitch(rear * up + 1.5 * up * shake))
    c.bone_offset("root", (0, -settings.get("braceMetres", 0.0) * up, -settings.get("crouchMetres", 0.05) * up))
    c.bone_rotation("chest", pitch(settings.get("chestDegrees", 0.0) * up))
    c.bone_rotation("neck", pitch(settings.get("neckDegrees", 10.0) * up) @ yaw(4.0 * up * gait.wave(t, 2)))
    c.bone_rotation("head", pitch(settings.get("headDegrees", 0.0) * up) @ yaw(3.0 * up * shake))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 30.0) * up - 2.0 * up * shake))
    heave = settings.get("domeHeaveDegrees", 3.0) * gait.wave(t, 3)
    open_dome(c, (settings.get("domeDegrees", 0.0) + heave) * up)
    swing_tail(c, t, 4.0 * up, droop=-settings.get("tailCurlDegrees", 6.0) * up)


def low_slung_bite(c, s, t):
    """The attack: drawn back, then a lunge forward with the jaws wide that snap shut. With `rearDegrees` (the hummock's
    charge and crush) the front rears up off its forefeet as it drives forward and slams down on its prey."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.32, 0.45))
    strike = gait.ease_in_out(t, 0.32, 0.48) * (1.0 - gait.ease_in_out(t, 0.7, 1.0))
    wide = gait.ease_in_out(t, 0.05, 0.32) * (1.0 - gait.ease_in_out(t, 0.45, 0.52))
    shut = gait.ease_in_out(t, 0.45, 0.52) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    reach = settings.get("lungeMetres", 0.3)
    rear = settings.get("rearDegrees", 0.0)
    if rear:
        # Up as it draws back and drives in, then down hard at the strike.
        lift = gait.ease_in_out(t, 0.05, 0.36) * (1.0 - gait.ease_in_out(t, 0.4, 0.5))
        slam = gait.ease_in_out(t, 0.4, 0.5) * (1.0 - gait.ease_in_out(t, 0.7, 1.0))
        for side in SIDES:
            c.leg_influence(f"fore.{side}", 1.0 - lift)
            c.bone_rotation(f"upperarm.{side}", pitch(-35.0 * lift))
            c.bone_rotation(f"forearm.{side}", pitch(40.0 * lift))
        c.bone_rotation("root", pitch(rear * lift - 0.15 * rear * slam))
        c.bone_offset("root", (0, -0.4 * reach * lift - reach * slam, -0.06 * slam))
    else:
        c.bone_offset("hips", (0, 0.3 * reach * back - reach * strike, 0.03 * strike))
    c.bone_rotation("chest", pitch(4.0 * back - 4.0 * strike))
    c.bone_rotation("neck", pitch(settings.get("neckDegrees", 12.0) * back - 8.0 * strike))
    c.bone_rotation("head", pitch(6.0 * back - settings.get("headDegrees", 10.0) * strike))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 30.0) * wide + 3.0 * shut))
    swing_tail(c, t, 3.0 * (back + strike))


def low_slung_hurt(c, s, t):
    """A blow lands: it flinches back and aside, head jerked away, mouth gaping; a split dome clamps shut."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.08 * hit, -0.03 * hit))
    c.bone_rotation("chest", roll(-5.0 * hit))
    c.bone_rotation("neck", pitch(8.0 * hit) @ yaw(-16.0 * hit))
    c.bone_rotation("head", pitch(6.0 * hit))
    c.bone_rotation("jaw", pitch(-s.get("hurt", {}).get("jawDegrees", 18.0) * hit))
    swing_tail(c, t, 6.0 * hit)


def low_slung_hide(c, s, t):
    """At rest hidden (#10880, the hummock): legs and head drawn in under the dome, which sits on the ground as one more
    mound, rising and falling with slow breath. Its feet leave the ground, so it does not plant them."""
    settings = s["hide"]
    breathe = gait.wave(t)
    drop = settings["metres"]
    # The body sinks until the dome's rim meets the ground, the belly into it. The feet go down into the ground by
    # `feetSinkShare` of that, so the legs fold only the rest of the way and stay under the dome.
    c.bone_offset("root", (0, 0, -drop))
    for leg in c.targets:
        c.place(leg, up=-settings.get("feetSinkShare", 0.5) * drop, side=-0.04)
    c.bone_offset("neck", (0, settings.get("retractMetres", 0.0), 0))
    c.bone_rotation("neck", pitch(-settings.get("neckDegrees", 25.0)))
    c.bone_rotation("head", pitch(-settings.get("headDegrees", 15.0)))
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, pitch(-4.0 * i) @ yaw(5.0 * i))
    c.bone_offset("spine", (0, 0, 0.012 * breathe))
    open_dome(c, 0.6 * breathe)


def low_slung_lie(c, s, t):
    """At rest, belly-down between sprawled feet, chin on the ground, the flanks breathing (#10880, the seep-rat)."""
    settings = s["lie"]
    breathe = gait.wave(t)
    for leg in c.targets:
        c.place(leg, side=settings.get("sprawlMetres", 0.05))
    c.bone_offset("root", (0, 0, -settings["sinkMetres"]))
    c.bone_rotation("spine", pitch(0.6 * breathe))
    c.bone_rotation("neck", pitch(-settings.get("headDegrees", 8.0)))
    c.bone_rotation("head", pitch(-0.4 * settings.get("headDegrees", 8.0)) @ yaw(3.0 * gait.wave(t, lag=0.3)))
    swing_tail(c, t, 1.0, droop=-4.0)


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
    "threat": (low_slung_threat, False, True),
    "bite": (low_slung_bite, False, False),
    "hurt": (low_slung_hurt, False, True),
    "lie": (low_slung_lie, True, True),
    "hide": (low_slung_hide, True, False),
}


# ---------------------------------------------------------------- saurian: monitors, salamanders (#10879)

def saurian_bones(points):
    """A sprawling lizard's body: the low-slung trunk with a second spine bone, so the body can bend side to side, and
    as long a tail as the species places points for (tail1 to tailN)."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "spine2", 1.0),
        ("spine2", "spine", "spine2", "chest", 1.0),
        ("chest", "spine2", "chest", "neck", 1.0),
    ]
    if "neck2" in points:
        # A long neck (#10882, the plume-neck) bends in two.
        bones += [("neck", "chest", "neck", "neck2", 1.0), ("neck2", "neck", "neck2", "head", 1.0),
                  ("head", "neck2", "head", "nose", 1.0)]
    else:
        bones += [("neck", "chest", "neck", "head", 1.0), ("head", "neck", "head", "nose", 1.0)]
    bones += [("jaw", "head", "jaw", "jaw_tip", 1.0)]
    # A ruff (#10882): each frill grown by add_ruff.py on its own bone on the head, from its root to its tip.
    index = 1
    while f"ruff{index}" in points:
        bones += [(f"ruff{index}", "head", f"ruff{index}", f"ruff{index}_tip", 1.0)]
        index += 1
    count = 1
    while f"tail{count + 1}" in points:
        count += 1
    bones += tail_chain(count)
    if "tongue" in points:
        # A forked tongue grown into the mouth (add_tongue.py), riding the lower jaw.
        bones += [("tongue", "jaw", "tongue", "tongue_tip", 1.0)]
    for side, sign in SIDES.items():
        bones += plain_legs(side, sign)
    return bones


SAURIAN_SPINE = ("hips", "spine", "spine2", "chest")
# How long a tongue flick takes, out and back, in seconds.
FLICK_SECONDS = 0.4


def flare_ruff(c, degrees, ripple=0.0, t=0.0):
    """A ruff's frills swing out from lying back along the neck, each along its own radius round the neck, by degrees
    (#10882); a ripple runs round them. Each frill's radius is the part of its line that leans away from the ruff's
    mean line, as add_ruff.py splays it. A body without a ruff has nothing to flare."""
    bones = c.rig.data.bones
    frills = [bones[f"ruff{k}"] for k in range(1, 64) if f"ruff{k}" in bones]
    if not frills:
        return
    mean = sum(((b.tail_local - b.head_local).normalized() for b in frills), Vector()).normalized()
    for k, bone in enumerate(frills, start=1):
        line = (bone.tail_local - bone.head_local).normalized()
        radial = (line - mean * line.dot(mean)).normalized()
        axis = bone.matrix_local.to_3x3().inverted() @ line.cross(radial).normalized()
        angle = degrees + ripple * gait.wave(t, 6, lag=0.08 * k)
        c.bone_rotation(bone.name, Matrix.Rotation(math.radians(angle), 3, axis))


def flick(c, s, clip, t, starts):
    """Tongue flicks at clip-time fractions `starts`: out the species' `tongue.reachMetres` and back, fluttering, the jaw
    parting a little to let it through. Nothing for a species without a tongue."""
    if "tongue" not in c.rig.pose.bones:
        return
    span = FLICK_SECONDS / s[clip]["seconds"]
    out = 0.0
    for start in starts:
        u = (t - start) / span
        if 0.0 <= u <= 1.0:
            out = max(out, math.sin(math.pi * u) * (0.8 + 0.2 * math.sin(6.0 * math.pi * u)))
    c.bone_offset("tongue", (0, -s["tongue"]["reachMetres"] * out, -0.01 * out))
    c.bone_rotation("jaw", pitch(-s["tongue"].get("jawDegrees", 5.0) * min(1.0, 3.0 * out)))


def bend(c, degrees, sway=0.0):
    """Bends the trunk side to side as a standing wave: the girdles turn opposite ways, so the body is a C, concave on
    the left for positive degrees; `sway` rolls the body with it."""
    c.bone_rotation("hips", yaw(-degrees) @ roll(sway))
    c.bone_rotation("spine", yaw(0.9 * degrees))
    c.bone_rotation("spine2", yaw(0.9 * degrees))
    c.bone_rotation("chest", yaw(0.5 * degrees) @ roll(-sway))


def saurian_tail(c, t, swish, cycles=1.0, lag=0.25, droop=0.0, curl=0.0):
    """A wave running down the tail, each bone a little later than the one before."""
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, pitch(droop) @ yaw(curl + swish * gait.wave(t, cycles, lag=lag + 0.09 * i)))


def saurian_gait(c, settings, t):
    """A sprawling walk: diagonal feet together, the trunk bending so each girdle turns its reaching limb forward, the
    head swinging against the bend, and a wave down the tail."""
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        forward, up = gait.foot(phase, duty, stride, settings["liftHind" if leg.startswith("hind") else "liftFore"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        # A sprawled foot swings out to the side as it comes forward.
        c.place(leg, forward=forward, up=up, side=settings.get("swingOutMetres", 0.04) * swing, toe_pitch=-15.0 * swing)
    # The bend follows the left hind foot: concave on the left while it reaches forward.
    lean = gait.wave(t, lag=settings["phases"].get("hind.L", 0.0) + 0.25 - duty / 2)
    bend(c, settings["bendDegrees"] * lean, sway=settings.get("swayDegrees", 2.0) * lean)
    c.bone_offset("hips", (0, 0, -settings.get("bob", 0.0) * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("neck", yaw(-settings.get("headSwingDegrees", 0.6) * settings["bendDegrees"] * lean) @ pitch(-1.5 * gait.wave(t, 2)))
    c.bone_rotation("head", yaw(-0.4 * settings["bendDegrees"] * lean))
    saurian_tail(c, t, settings.get("tailDegrees", 6.0), lag=0.3)


def saurian_walk(c, s, t):
    saurian_gait(c, s["walk"], t)
    flick(c, s, "walk", t, (0.35,))


def saurian_run(c, s, t):
    saurian_gait(c, s["run"], t)


def saurian_idle(c, s, t):
    """Still, breathing, the head turning slowly, the tongue flicking now and then."""
    breathe = gait.wave(t, 2)
    c.bone_rotation("spine", pitch(0.6 * breathe))
    c.bone_rotation("chest", pitch(-0.5 * breathe))
    c.bone_rotation("neck", pitch(1.5 * gait.wave(t)) @ yaw(10.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", pitch(2.0 * gait.wave(t, 2, lag=0.1)) @ yaw(6.0 * gait.wave(t, lag=0.35)))
    saurian_tail(c, t, 1.5)
    flick(c, s, "idle", t, (0.15, 0.6))


def saurian_alert(c, s, t):
    """Up on its forelegs, neck raised, head turning to find what it heard."""
    up = s.get("alert", {}).get("raiseMetres", 0.08)
    c.bone_offset("chest", (0, 0, up))
    c.bone_rotation("spine2", pitch(4.0))
    c.bone_rotation("neck", pitch(16.0) @ yaw(14.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(-8.0) @ yaw(8.0 * gait.wave(t, lag=0.1)))
    saurian_tail(c, t, 2.0, droop=-2.0)
    # Tasting the air for what it heard.
    flick(c, s, "alert", t, (0.1, 0.4, 0.7))


def saurian_threat(c, s, t):
    """A monitor's threat on four legs: the body raised high on straightened legs and bent side-on to the threat, the
    neck arched, jaws wide, the tail curled ready to lash."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.22, 0.82)
    shake = gait.wave(t, 6)
    c.bone_offset("root", (0, 0, settings.get("raiseMetres", 0.15) * up))
    bend(c, settings.get("bendDegrees", 8.0) * up)
    c.bone_rotation("neck", pitch(settings.get("neckDegrees", 20.0) * up) @ yaw(-6.0 * up))
    c.bone_rotation("head", pitch(-settings.get("neckDegrees", 20.0) * 0.6 * up) @ yaw(3.0 * up * shake))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 30.0) * up - 2.0 * up * shake))
    saurian_tail(c, t, 4.0 * up, cycles=3.0, curl=settings.get("tailCurlDegrees", 8.0) * up)
    # A ruff flares wide and shivers (#10882): the plume-neck's display before it strikes.
    flare_ruff(c, settings.get("ruffDegrees", 0.0) * gait.rise_hold_fall(t, 0.15, 0.85), 6.0 * up, t)


def saurian_bite(c, s, t):
    """A lunge and a sideways snap: the head draws back and to one side, the body throws itself forward and the head
    sweeps across as the jaws shut, then it draws back."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.28) * (1.0 - gait.ease_in_out(t, 0.3, 0.42))
    strike = gait.ease_in_out(t, 0.32, 0.44) * (1.0 - gait.ease_in_out(t, 0.68, 1.0))
    wide = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.42, 0.5))
    shut = gait.ease_in_out(t, 0.42, 0.5) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    reach = settings.get("lungeMetres", 0.4)
    across = settings.get("sweepDegrees", 18.0)
    lift = settings.get("liftForeFeet", False)
    if lift:
        for side in SIDES:
            c.leg_influence(f"fore.{side}", 1.0 - strike)
    c.bone_offset("hips", (0, 0.35 * reach * back - reach * strike, 0.04 * strike))
    bend(c, 6.0 * back - 4.0 * strike)
    c.bone_rotation("neck", pitch(10.0 * back - 6.0 * strike) @ yaw(across * back - across * strike))
    c.bone_rotation("head", pitch(6.0 * back - 4.0 * strike) @ yaw(0.5 * across * back - 0.6 * across * strike))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 30.0) * wide + 3.0 * shut))
    saurian_tail(c, t, 3.0 * (back + strike), cycles=2.0)
    flare_ruff(c, settings.get("ruffDegrees", 0.0) * (1.0 - gait.ease_in_out(t, 0.7, 1.0)))


def saurian_hurt(c, s, t):
    """A blow lands: it flinches back and away, bending, and gapes."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.1 * hit, 0))
    bend(c, -10.0 * hit)
    c.bone_rotation("neck", pitch(8.0 * hit) @ yaw(-16.0 * hit))
    c.bone_rotation("head", pitch(6.0 * hit))
    c.bone_rotation("jaw", pitch(-20.0 * hit))
    saurian_tail(c, t, 6.0 * hit, cycles=2.0)


def saurian_lie(c, s, t):
    """At rest, belly to the ground (#10879): the body sinks between sprawled feet that stay put, chin down, the tail
    laid in a slow curve, breathing."""
    settings = s["lie"]
    breathe = gait.wave(t)
    for leg in c.targets:
        c.place(leg, side=settings.get("sprawlMetres", 0.1))
    c.bone_offset("root", (0, 0, -settings["sinkMetres"]))
    bend(c, settings.get("bendDegrees", 6.0))
    c.bone_rotation("spine", yaw(0.9 * settings.get("bendDegrees", 6.0)) @ pitch(0.5 * breathe))
    c.bone_rotation("neck", pitch(-settings.get("headDegrees", 6.0)))
    c.bone_rotation("head", pitch(-0.5 * settings.get("headDegrees", 6.0)))
    saurian_tail(c, 0.0, 0.0, curl=settings.get("tailCurlDegrees", 5.0))
    flick(c, s, "lie", t, (0.55,))


def saurian_upright(c, s, t):
    """Standing straight and still, reared up on the hind legs and the tail (#10882, the plume-neck): the body tips up
    about the hind feet, the forelegs fold against the chest, the tail lies back along the ground and the neck rises
    straight, the head level; it breathes and turns its head once a clip, as one more tube among the tubes."""
    settings = s["upright"]
    rise = settings["degrees"]
    breathe = gait.wave(t)
    c.bone_rotation("root", pitch(rise))
    c.bone_offset("root", (0, 0, settings.get("liftMetres", 0.0)))
    for side in SIDES:
        c.leg_influence(f"fore.{side}", 0.0)
        c.bone_rotation(f"upperarm.{side}", pitch(-settings.get("armDegrees", 40.0)))
        c.bone_rotation(f"forearm.{side}", pitch(settings.get("forearmDegrees", 70.0)))
    c.bone_rotation("chest", pitch(0.5 * breathe))
    neck = settings.get("neckDegrees", 0.0)
    c.bone_rotation("neck", pitch(neck))
    turn = settings.get("turnDegrees", 6.0) * gait.rise_hold_fall(t, 0.35, 0.55, 0.85)
    c.bone_rotation("head", pitch(-rise - neck - settings.get("neck2Degrees", 0.0)) @ yaw(turn))
    if "neck2" in c.rig.pose.bones:
        c.bone_rotation("neck2", pitch(settings.get("neck2Degrees", 0.0)))
    # The tail bends back down to the ground behind it.
    for i, name in enumerate(tail_names(c), start=1):
        c.bone_rotation(name, pitch(-settings.get("tailDegrees", 12.0) if i <= 3 else 0.0))


def saurian_die(c, s, t):
    fall_over(c, t, c.leg_length("hind.L"), thigh=-12.0, knee=18.0)


def tuck_legs(c, amount):
    """Legs folded back along the body, as a salamander swims: IK off, each limb laid back."""
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - amount)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-60.0 * amount) @ spread(side, -10.0 * amount))
        c.bone_rotation(f"shin.{side}", pitch(-30.0 * amount))
        c.bone_rotation(f"upperarm.{side}", pitch(-70.0 * amount) @ spread(side, -10.0 * amount))
        c.bone_rotation(f"forearm.{side}", pitch(30.0 * amount))


def saurian_swim(c, s, t):
    """Lying in the shallows (#10879, the drowner): the legs laid back, a slow wave down the body and tail holding it
    in place, the head up at the surface and turning."""
    settings = s["swim"]
    tuck_legs(c, 1.0)
    c.bone_offset("root", (0, 0, -settings.get("sinkMetres", 0.2)))
    sweep = settings.get("bendDegrees", 5.0)
    c.bone_rotation("hips", yaw(-sweep * gait.wave(t, lag=0.5)))
    c.bone_rotation("spine", yaw(sweep * gait.wave(t, lag=0.35)))
    c.bone_rotation("spine2", yaw(sweep * gait.wave(t, lag=0.2)))
    c.bone_rotation("chest", yaw(0.5 * sweep * gait.wave(t, lag=0.1)))
    c.bone_rotation("neck", pitch(6.0) @ yaw(-0.6 * sweep * gait.wave(t, lag=0.05)))
    c.bone_rotation("head", pitch(-3.0) @ yaw(5.0 * gait.wave(t, lag=0.4)))
    saurian_tail(c, t, settings.get("tailDegrees", 10.0), lag=0.55)


def saurian_lunge(c, s, t):
    """The strike from the water's edge (#10879, the drowner): low and drawn back, then the whole body throws forward
    with the forelegs off the ground and the jaws wide, snaps shut and settles back."""
    settings = s["lunge"]
    coil = gait.ease_in_out(t, 0.0, 0.25) * (1.0 - gait.ease_in_out(t, 0.28, 0.36))
    throw = gait.ease_in_out(t, 0.28, 0.4) * (1.0 - gait.ease_in_out(t, 0.62, 1.0))
    wide = gait.ease_in_out(t, 0.1, 0.32) * (1.0 - gait.ease_in_out(t, 0.4, 0.46))
    shut = gait.ease_in_out(t, 0.4, 0.46) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    reach = settings.get("metres", 1.0)
    for side in SIDES:
        c.leg_influence(f"fore.{side}", 1.0 - throw)
        c.bone_rotation(f"upperarm.{side}", pitch(-50.0 * throw))
    c.bone_offset("root", (0, 0.25 * reach * coil - reach * throw, -0.08 * coil + 0.12 * throw))
    bend(c, 10.0 * coil * gait.wave(t, 3))
    c.bone_rotation("chest", pitch(-4.0 * coil + 10.0 * throw))
    c.bone_rotation("neck", pitch(-6.0 * coil + 8.0 * throw))
    c.bone_rotation("head", pitch(-4.0 * coil - 6.0 * throw))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 40.0) * wide + 3.0 * shut))
    saurian_tail(c, t, 8.0 * (coil + throw), cycles=2.0)


SAURIAN_LEGS = PLANTIGRADE_LEGS

SAURIAN_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (saurian_idle, True, True),
    "walk": (saurian_walk, True, True),
    "run": (saurian_run, True, True),
    "alert": (saurian_alert, True, True),
    "threat": (saurian_threat, False, True),
    "bite": (saurian_bite, False, False),
    "hurt": (saurian_hurt, False, True),
    "lie": (saurian_lie, True, True),
    "die": (saurian_die, False, False),
    "swim": (saurian_swim, True, False),
    "lunge": (saurian_lunge, False, False),
    "upright": (saurian_upright, True, False),
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


def bird_stand(c, s, t):
    """Standing still for hours (#10881, the stalk-bill and the ribbon-stalker): the neck drawn in and the head level,
    one slow turn of the head a clip, the chest breathing and the body swaying a degree, as a stalk or a hanging
    ribbon would. A theropod keeps its arms folded and its tail hanging still."""
    settings = s["stand"]
    breathe = gait.wave(t)
    turn = settings.get("turnDegrees", 6.0) * gait.rise_hold_fall(t, 0.35, 0.55, 0.85)
    c.bone_rotation("root", roll(settings.get("swayDegrees", 0.8) * gait.wave(t, lag=0.2)))
    c.bone_rotation("chest", pitch(0.6 * breathe))
    c.bone_rotation("neck", pitch(-settings.get("neckDegrees", 0.0)))
    c.bone_rotation("neck3", pitch(settings.get("neckDegrees", 0.0)) @ yaw(turn))
    c.bone_rotation("head", yaw(0.5 * turn))
    if "upperarm.L" in c.rig.pose.bones:
        theropod_arms(c, settings.get("armDegrees", -6.0))
        theropod_tail(c, t, 0.6)


def bird_stalk(c, s, t):
    """A slow stalk: the walk at its own pace with the neck stretched low and forward and the head held steady over
    the ground ahead (#10881)."""
    bird_gait(c, s["stalk"], t, spine_flex=0.5)
    low = s["stalk"].get("neckDegrees", 14.0)
    c.bone_rotation("neck", pitch(-low))
    c.bone_rotation("neck2", pitch(0.4 * low))
    c.bone_rotation("head", pitch(0.4 * low))


def bird_spear(c, s, t):
    """The spearing strike (#10881, the stalk-bill): the neck draws back into an S with the bill opening, then the body
    tips forward over its planted feet and the neck straightens out to full length, the head aimed down at the ground
    ahead with the bill wide, which snaps shut there before the neck draws back up."""
    settings = s["spear"]
    coil = gait.ease_in_out(t, 0.0, 0.32) * (1.0 - gait.ease_in_out(t, 0.34, 0.42))
    throw = gait.ease_in_out(t, 0.34, 0.44) * (1.0 - gait.ease_in_out(t, 0.68, 1.0))
    wide = gait.ease_in_out(t, 0.1, 0.36) * (1.0 - gait.ease_in_out(t, 0.5, 0.56))
    shut = gait.ease_in_out(t, 0.5, 0.56) * (1.0 - gait.ease_in_out(t, 0.82, 1.0))
    reach = settings.get("reachDegrees", 50.0)
    lean = settings.get("leanDegrees", 10.0)
    # The body tips at the spine, so the legs keep their feet where they stand.
    c.bone_offset("hips", (0, -settings.get("lungeMetres", 0.12) * throw, -0.03 * throw))
    c.bone_rotation("spine", pitch(-lean * throw + 3.0 * coil))
    c.bone_rotation("chest", pitch(-0.5 * lean * throw))
    # The neck swings forward out of its S, and the head tips back up to aim the bill down and ahead.
    c.bone_rotation("neck", pitch(16.0 * coil - reach * throw))
    c.bone_rotation("neck2", pitch(-22.0 * coil + 0.1 * reach * throw))
    c.bone_rotation("neck3", pitch(14.0 * coil + 0.1 * reach * throw))
    c.bone_rotation("head", pitch(-8.0 * coil + 0.6 * reach * throw))
    c.bone_rotation("jaw", pitch(-settings.get("jawDegrees", 30.0) * wide + 3.0 * shut))


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
    "stand": (bird_stand, True, True),
    "stalk": (bird_stalk, True, True),
    "spear": (bird_spear, False, True),
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
    "stand": (bird_stand, True, True),
}


# ---------------------------------------------------------------- percher: a small bird on the ground

def percher_idle(c, s, t):
    """Standing still and looking about in quick small turns of the head, the tail ticking."""
    c.bone_rotation("neck3", yaw(16.0 * gait.wave(t, lag=0.1)) @ pitch(3.0 * gait.wave(t, 2, lag=0.3)))
    c.bone_rotation("tail1", pitch(3.0 * gait.wave(t, 2)))


def percher_peck(c, s, t):
    """Head down to the ground and up, twice a clip, the body tipping forward over its feet to reach."""
    settle = s["peck"]
    dip = 0.5 - 0.5 * math.cos(2 * gait.TAU * t)
    c.bone_rotation("hips", pitch(-settle["tipDegrees"]))
    c.bone_rotation("neck", pitch(-settle["neckDegrees"] * (0.6 + 0.4 * dip)))
    c.bone_rotation("neck3", pitch(-settle["headDegrees"] * dip))
    c.bone_rotation("tail1", pitch(0.5 * settle["tipDegrees"]))


def percher_hop(c, s, t):
    """One hop on both feet: legs fold on the way up and open to land. The clip plays for the length of the hop, so it holds no travel."""
    settle = s["hop"]
    tuck = math.sin(math.pi * t)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(settle["tuckDegrees"] * tuck))
        c.bone_rotation(f"shin.{side}", pitch(-1.6 * settle["tuckDegrees"] * tuck))
    c.bone_rotation("tail1", pitch(settle["tailDegrees"] * tuck))


PERCHER_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (percher_idle, True, True),
    "peck": (percher_peck, True, True),
    "hop": (percher_hop, True, False),
}


# ---------------------------------------------------------------- flyer: wings and no legs

def flyer_bones():
    """A bird in the air, seen from far below: a stiff body, jointed wings and a tail. Its legs stay tucked, so it has none."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("body", "root", "body", "chest", 1.0),
        ("chest", "body", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
        ("tail", "body", "tail0", "tail1", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"wing1.{side}", "chest", "shoulder", "elbow", sign),
            (f"wing2.{side}", f"wing1.{side}", "elbow", "wrist", sign),
            (f"wing3.{side}", f"wing2.{side}", "wrist", "wing_tip", sign),
            (f"streamer1.{side}", "tail", "streamer0", "streamer1", sign),
            (f"streamer2.{side}", f"streamer1.{side}", "streamer1", "streamer2", sign),
        ]
    return bones


def flyer_wings(c, shoulder, elbow, wrist):
    """Both wings, by degrees of lift at each joint: positive raises the wing toward the back."""
    for side, sign in SIDES.items():
        c.bone_rotation(f"wing1.{side}", roll(sign * shoulder))
        c.bone_rotation(f"wing2.{side}", roll(sign * elbow))
        c.bone_rotation(f"wing3.{side}", roll(sign * wrist))


def flyer_streamers(c, t, swing):
    """The streamers trail, one a little behind the other, the second bone further out."""
    for side, sign in SIDES.items():
        lag = 0.08 if sign > 0 else 0.0
        c.bone_rotation(f"streamer1.{side}", pitch(swing * gait.wave(t, lag=lag)))
        c.bone_rotation(f"streamer2.{side}", pitch(1.6 * swing * gait.wave(t, lag=lag + 0.12)))


def flyer_glide(c, s, t):
    """Wings held a little raised, the tips feeling the air, the tail working to steer."""
    settle = s["glide"]
    flyer_wings(c, settle["dihedralDegrees"] + 0.8 * gait.wave(t), -0.5 * gait.wave(t, lag=0.1), 1.5 * gait.wave(t, lag=0.2))
    c.bone_rotation("tail", yaw(2.0 * gait.wave(t, lag=0.3)))
    c.bone_rotation("head", yaw(4.0 * gait.wave(t, lag=0.15)))
    flyer_streamers(c, t, 3.0)


def flyer_flap(c, s, t):
    """Slow deep beats: the downstroke leads from the shoulder and the tips follow it."""
    settle = s["flap"]
    beat = settle["beatDegrees"]
    flyer_wings(c, settle["dihedralDegrees"] + beat * gait.wave(t), 0.3 * beat * gait.wave(t, lag=0.1), 0.5 * beat * gait.wave(t, lag=0.2))
    c.bone_offset("body", (0, 0, -settle["bobMetres"] * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", pitch(-0.1 * beat * gait.wave(t, lag=0.3)))
    flyer_streamers(c, t, 6.0)


FLYER_CLIPS = {
    "rest": (clip_rest, False, False),
    "glide": (flyer_glide, True, False),
    "flap": (flyer_flap, True, False),
}


# ---------------------------------------------------------------- the table

# ---------------------------------------------------------------- serpent: legless (#10883, the root-adder)

def serpent_segments(points):
    """How many body segments the species places: s0 (the tail tip) to sN (the neck's base)."""
    count = 0
    while f"s{count + 1}" in points:
        count += 1
    return count


def serpent_bones(points):
    """A snake: one chain of body segments from the tail tip forward (body1 on s0..s1, bodyN ending at the neck's base),
    then the neck, head and jaw. A turn of a segment carries everything ahead of it, so a curve along the chain bends the
    whole snake. It has no legs."""
    count = serpent_segments(points)
    bones = [("root", None, "root", "root_tip", 1.0)]
    bones += [(f"body{k}", "root" if k == 1 else f"body{k - 1}", f"s{k - 1}", f"s{k}", 1.0) for k in range(1, count + 1)]
    bones += [
        ("neck", f"body{count}", f"s{count}", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
        ("jaw", "head", "jaw", "jaw_tip", 1.0),
    ]
    return bones


def serpent_body(c):
    """The body segments' bone names, tail first."""
    return sorted((name for name in c.rig.pose.bones.keys() if name.startswith("body")), key=lambda name: int(name[4:]))


def serpent_lay(c, turns, lifts=None, centre=True):
    """Lays the body by a turn (degrees to the left) and a lift (degrees up) at each segment, the tail first. With
    `centre`, turns and moves the whole snake so its head points forward and the flat shape is centred on the origin,
    where its capsule is. Returns the head's heading before that turn, in degrees."""
    names = serpent_body(c)
    lifts = lifts or [0.0] * len(names)
    for name, turn, lift in zip(names, turns, lifts):
        c.bone_rotation(name, yaw(turn) @ pitch(lift))
    heading = sum(turns)
    if not centre:
        return heading
    # The flat shape the turns make, centred on the origin once the root turns it so the head points forward.
    points = serpent_flat(c, turns)
    xs, ys = [point[0] for point in points], [point[1] for point in points]
    cx, cy = sum(xs) / len(xs), sum(ys) / len(ys)
    # The root turns the whole shape about the origin so the head points forward; then it moves the turned centre
    # onto the origin.
    back = -math.radians(heading)
    rx = cx * math.cos(back) - cy * math.sin(back)
    ry = cx * math.sin(back) + cy * math.cos(back)
    c.bone_rotation("root", yaw(-heading))
    c.bone_offset("root", (-rx, -ry, 0.0))
    return heading


def serpent_flat(c, turns):
    """The flat shape a set of turns lays the body in, as Blender ground (x, y) points from the tail tip, before the
    root moves it."""
    names = serpent_body(c)
    x, y = c.rig.data.bones[names[0]].head_local.x, c.rig.data.bones[names[0]].head_local.y
    angle = 0.0
    points = [(x, y)]
    for turn, name in zip(turns, names):
        angle += math.radians(turn)
        length = c.rig.data.bones[name].length
        x += math.sin(angle) * length
        y -= math.cos(angle) * length
        points.append((x, y))
    return points


def serpent_coil(count, loops, inner_share=0.35):
    """Turns that wind `count` segments into a flat spiral of so many loops, the tail innermost and the curve easing
    outward."""
    weights = [1.0 / (inner_share + k / max(count - 1, 1)) for k in range(count)]
    scale = 360.0 * loops / sum(weights)
    return [weight * scale for weight in weights]


def serpent_coiled(c, settings, amount, front_lift=0.0):
    """Coiled flat by `amount`, the last `riseSegments` free to rise, by `front_lift` degrees, into an S over the coil."""
    names = serpent_body(c)
    front = settings.get("riseSegments", 4)
    coil = serpent_coil(len(names) - front, settings.get("loops", 1.3))
    turns = [turn * amount for turn in coil] + [settings.get("frontTurnDegrees", 30.0) * amount / front] * front
    lifts = [0.0] * (len(names) - front) + [front_lift if k == 0 else 0.0 for k in range(front)]
    return serpent_lay(c, turns, lifts)


def serpent_front(c, lift, jaw=0.0, sway=0.0, head_down=0.0):
    """The neck curves the raised front back to level, so the head looks forward over the coil."""
    c.bone_rotation("neck", pitch(-0.75 * lift) @ yaw(sway))
    c.bone_rotation("head", pitch(-0.25 * lift - head_down) @ yaw(-0.5 * sway))
    c.bone_rotation("jaw", pitch(-jaw))


def serpent_wave(count, amplitude, wavelength_segments, t):
    """A travelling wave down the body: each segment's turn, moving from the head toward the tail as t goes 0 to 1."""
    return [amplitude * math.sin(gait.TAU * (t + k / wavelength_segments)) for k in range(count)]


def serpent_slither(c, settings, t):
    """Lateral undulation in place: a wave of turns runs down the body from the head to the tail, so the snake moves
    forward through it at the clip's speed; the root turns against the wave and the head steers to keep looking ahead."""
    names = serpent_body(c)
    count = len(names)
    turns = serpent_wave(count, settings["bendDegrees"], settings.get("waveSegments", 7.0), t)
    turns = [turn * (0.45 + 0.55 * math.sin(math.pi * (k + 0.5) / count)) for k, turn in enumerate(turns)]
    heading = serpent_lay(c, turns, centre=False)
    c.bone_rotation("root", yaw(-0.5 * heading))
    c.bone_rotation("neck", yaw(-0.5 * heading) @ pitch(settings.get("headLiftDegrees", 4.0)))
    c.bone_rotation("head", pitch(-settings.get("headLiftDegrees", 4.0)))


def serpent_walk(c, s, t):
    serpent_slither(c, s["walk"], t)


def serpent_run(c, s, t):
    serpent_slither(c, s["run"], t)


def serpent_idle(c, s, t):
    """Lying stretched out in a lazy curve, the head lifting a little and turning, tasting the air."""
    names = serpent_body(c)
    curve = s["idle"].get("curveDegrees", 8.0)
    serpent_lay(c, [curve * math.sin(math.pi * 1.5 * k / len(names)) for k in range(len(names))], centre=False)
    c.bone_rotation("neck", pitch(5.0 + 3.0 * gait.wave(t)) @ yaw(10.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", pitch(-3.0 * gait.wave(t, 2)) @ yaw(6.0 * gait.wave(t, lag=0.4)))


def serpent_alert(c, s, t):
    """The head and neck raised off the ground, swaying."""
    serpent_idle(c, s, 0.0)
    c.bone_rotation("neck", pitch(30.0) @ yaw(12.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(-22.0) @ yaw(6.0 * gait.wave(t, lag=0.2)))


def serpent_lie(c, s, t):
    """At rest on the ground (#10883): coiled flat, the head laid on the coil, breathing."""
    serpent_coiled(c, s["lie"], 1.0)
    c.bone_rotation("neck", pitch(3.0 + 1.5 * gait.wave(t)) @ yaw(20.0))
    c.bone_rotation("head", pitch(-3.0) @ yaw(8.0 * gait.wave(t, lag=0.3)))


def serpent_threat(c, s, t):
    """Coiled, the front raised in an S, the jaws wide on the fangs, swaying."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.2, 0.85)
    lift = settings.get("riseDegrees", 60.0) * up
    serpent_coiled(c, settings, 1.0, lift)
    serpent_front(c, lift, jaw=settings.get("jawDegrees", 40.0) * up, sway=8.0 * up * gait.wave(t, 2))


def serpent_bite(c, s, t):
    """The strike from the coil: the S draws back, then the front shoots out and down at the prey with the jaws wide,
    snaps shut and recoils."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.3) * (1.0 - gait.ease_in_out(t, 0.32, 0.42))
    strike = gait.ease_in_out(t, 0.32, 0.44) * (1.0 - gait.ease_in_out(t, 0.66, 1.0))
    wide = gait.ease_in_out(t, 0.1, 0.36) * (1.0 - gait.ease_in_out(t, 0.46, 0.52))
    shut = gait.ease_in_out(t, 0.46, 0.52) * (1.0 - gait.ease_in_out(t, 0.8, 1.0))
    rise = settings.get("riseDegrees", 60.0)
    lift = rise * (1.0 + 0.25 * back - 0.85 * strike)
    serpent_coiled(c, settings, 1.0, lift)
    serpent_front(c, lift, jaw=settings.get("jawDegrees", 50.0) * wide - 3.0 * shut, head_down=settings.get("diveDegrees", 15.0) * strike)
    # The lunge: the front bends forward over the coil as the S straightens.
    names = serpent_body(c)
    front = settings.get("riseSegments", 4)
    for name in names[-front + 1:]:
        c.bone_rotation(name, yaw(settings.get("frontTurnDegrees", 30.0) / front) @ pitch(-settings.get("reachDegrees", 8.0) * strike))


def serpent_hurt(c, s, t):
    """A blow lands: it flinches, the body jerking aside and the head thrown back, mouth gaping."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    names = serpent_body(c)
    serpent_lay(c, [18.0 * hit * math.sin(math.pi * k / len(names)) for k in range(len(names))], centre=False)
    c.bone_rotation("neck", pitch(20.0 * hit) @ yaw(-25.0 * hit))
    c.bone_rotation("jaw", pitch(-25.0 * hit))


def serpent_die(c, s, t):
    """It goes limp: the body slumps into a loose curve and rolls a little onto its side, the jaws falling open."""
    limp = gait.ease_in_out(t, 0.1, 0.8)
    names = serpent_body(c)
    serpent_lay(c, [limp * 14.0 * math.sin(math.pi * 2.2 * k / len(names)) for k in range(len(names))], centre=False)
    c.bone_rotation("root", roll(60.0 * limp))
    c.bone_rotation("neck", pitch(-6.0 * limp) @ yaw(30.0 * limp))
    c.bone_rotation("jaw", pitch(-22.0 * limp))


def serpent_hanging(c, s, amount, t):
    """Upside down along the ceiling, which the product puts at the origin (#10883): the rear in loose loops along it,
    the front hanging down and the head turned to look at the floor."""
    settings = s["hang"]
    names = serpent_body(c)
    hang = settings.get("hangSegments", 4)
    rear = len(names) - hang
    loops = [settings.get("loopDegrees", 22.0) * math.sin(math.pi * 3.0 * k / rear) for k in range(rear)]
    turns = loops + [settings.get("swayDegrees", 4.0) * gait.wave(t)] * hang
    # Lying belly up along the ceiling, the front bends down over its first few segments (in the flipped body a lift
    # turns it toward the floor), and its last curls a little back so the head hangs looking forward.
    bend = settings.get("bendSegments", 3)
    lifts = [0.0] * rear + [settings.get("dropDegrees", 90.0) * amount / bend] * bend + [-settings.get("curlDegrees", 15.0) * amount] * (hang - bend)
    turns = [turn * amount for turn in turns]
    serpent_lay(c, turns, lifts, centre=False)
    # Turned over about the forward axis and moved so the bend where the front drops sits over the origin: the capsule
    # on the floor below is where it lands.
    heading = sum(turns[:rear])
    names_flat = serpent_flat(c, turns)
    bx, by = names_flat[rear]
    turn = roll(180.0 * amount) @ yaw(-heading)
    bend = turn @ Vector((bx, by, 0.0))
    c.bone_rotation("root", turn)
    # Hanging, the bend is over the origin; turned right way up and straight it eases back to its rest, centred.
    c.bone_offset("root", (-bend.x * amount, -bend.y * amount, 0.0))
    c.bone_rotation("neck", pitch(settings.get("lookDegrees", -60.0) * amount) @ yaw(10.0 * gait.wave(t, lag=0.3) * amount))


def serpent_hang(c, s, t):
    """At rest hanging from the roots of a cave's mouth (#10883), swaying a little."""
    serpent_hanging(c, s, 1.0, t)


def serpent_drop(c, s, t):
    """Letting go (#10883): it turns right way up as the product lowers it, and lands coiled."""
    serpent_hanging(c, s, 1.0 - gait.ease_in_out(t, 0.0, 0.6), t)
    if t > 0.6:
        serpent_coiled(c, s["lie"], gait.ease_in_out(t, 0.6, 1.0))
    c.bone_rotation("jaw", pitch(-30.0 * (1.0 - gait.ease_in_out(t, 0.4, 1.0))))


SERPENT_CLIPS = {
    "rest": (clip_rest, False, False),
    "idle": (serpent_idle, True, False),
    "walk": (serpent_walk, True, False),
    "run": (serpent_run, True, False),
    "alert": (serpent_alert, True, False),
    "lie": (serpent_lie, True, False),
    "threat": (serpent_threat, False, False),
    "bite": (serpent_bite, False, False),
    "hurt": (serpent_hurt, False, False),
    "die": (serpent_die, False, False),
    "hang": (serpent_hang, True, False),
    "drop": (serpent_drop, False, False),
}


# ---------------------------------------------------------------- strider: two legs and two long arms, no tail (#10956)

def strider_bones():
    """The bird's legs under a short spine with no tail, two long arms that hang to the ground and grip, and a short neck
    to a hanging head (#10956, the main ruin inhabitant). Its shells are not skinned: each is a rigid mesh the product
    hangs from a joint of this rig (the species' `shells`), so the spine flexes little."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"thigh.{side}", "hips", "hip_socket", "stifle", sign),
            (f"shin.{side}", f"thigh.{side}", "stifle", "hock", sign),
            (f"cannon.{side}", f"shin.{side}", "hock", "fetlock", sign),
            (f"hoof.{side}", f"cannon.{side}", "fetlock", "toe", sign),
            (f"upperarm.{side}", "chest", "shoulder", "elbow", sign),
            (f"forearm.{side}", f"upperarm.{side}", "elbow", "wrist", sign),
            (f"hand.{side}", f"forearm.{side}", "wrist", "knuckle", sign),
            (f"claw.{side}", f"hand.{side}", "knuckle", "claw_tip", sign),
        ]
    return bones


def strider_arms(c, settings, swing=(0.0, 0.0), reach=0.0, out=0.0, curl=0.0):
    """Both arms from hanging: `swing` pitches each (left, right) forward, `reach` raises both forward from the shoulder
    with the elbow opening, `out` spreads them, and `curl` closes the fingers."""
    for (side, sign), own in zip(SIDES.items(), swing):
        c.bone_rotation(f"upperarm.{side}", pitch(own + reach) @ spread(side, out))
        c.bone_rotation(f"forearm.{side}", pitch(0.35 * own - 0.3 * reach))
        c.bone_rotation(f"claw.{side}", pitch(-curl))


def strider_gait(c, settings, t, spine_flex):
    """Two legs in turn, the hips swinging over the foot that stands; the long arms swing against the legs, each a little
    behind, and the spine stays stiff enough under its shell."""
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        side = leg.split(".")[1]
        forward, up = gait.foot(phase, duty, stride, settings["liftHind"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-30.0 * swing)
        reach = math.degrees(math.asin(max(-0.9, min(0.9, forward / c.leg_length(leg)))))
        c.bone_rotation(f"thigh.{side}", pitch(TOP_SEGMENT_SHARE * reach))
    sway, bob = settings["swayDegrees"], settings["bob"]
    c.bone_offset("hips", (settings.get("swingMetres", 0.06) * gait.wave(t), 0, -bob * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ yaw(0.6 * sway * gait.wave(t, lag=0.1)))
    c.bone_rotation("spine", pitch(spine_flex * gait.wave(t, 2)))
    c.bone_rotation("chest", roll(-0.7 * sway * gait.wave(t, lag=0.2)) @ yaw(-0.8 * sway * gait.wave(t, lag=0.15)))
    nod = settings.get("nodDegrees", 3.0)
    c.bone_rotation("neck", pitch(-nod * gait.wave(t, 2, lag=0.15)))
    c.bone_rotation("head", pitch(0.6 * nod * gait.wave(t, 2, lag=0.3)) @ yaw(-0.5 * sway * gait.wave(t, lag=0.3)))
    arm = settings.get("armSwingDegrees", 14.0)
    left, right = arm * gait.wave(t, lag=0.5 + 0.08), arm * gait.wave(t, lag=0.08)
    strider_arms(c, settings, swing=(left, right), reach=settings.get("armReachDegrees", 0.0), curl=10.0)


def strider_walk(c, s, t):
    strider_gait(c, s["walk"], t, spine_flex=0.6)


def strider_run(c, s, t):
    strider_gait(c, s["run"], t, spine_flex=s["run"].get("spineFlex", 1.5))


def strider_idle(c, s, t):
    """Still, breathing; the hanging head turns a little, the arms drift and the fingers flex."""
    breathe = gait.wave(t, 2)
    c.bone_rotation("spine", pitch(0.5 * breathe))
    c.bone_rotation("chest", pitch(-0.5 * gait.wave(t, 2, lag=0.1)))
    c.bone_rotation("neck", pitch(1.5 * gait.wave(t)) @ yaw(6.0 * gait.wave(t, lag=0.25)))
    c.bone_rotation("head", yaw(5.0 * gait.wave(t, lag=0.3)))
    drift = 3.0 * gait.wave(t, lag=0.2)
    strider_arms(c, s, swing=(drift, -drift), curl=10.0 + 12.0 * max(0.0, gait.wave(t, 3)))


def strider_alert(c, s, t):
    """It lifts its head from the hang and turns it slowly, arms drawn a little forward, fingers open."""
    c.bone_rotation("chest", pitch(3.0))
    c.bone_rotation("neck", pitch(14.0) @ yaw(18.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(6.0) @ yaw(8.0 * gait.wave(t, lag=0.1)))
    strider_arms(c, s, reach=12.0, out=6.0, curl=-6.0)


def strider_crouch(c, s, t):
    """At rest (#10956): sunk low on folded legs with its hands on the ground before it and its head hanging, still as
    the ruin around it; it only breathes."""
    settings = s.get("crouch", {})
    sink = settings.get("sinkMetres", 0.35)
    c.bone_offset("hips", (0, 0, -sink))
    c.bone_rotation("spine", pitch(-3.0 + 0.4 * gait.wave(t)))
    c.bone_rotation("neck", pitch(-10.0))
    c.bone_rotation("head", pitch(-6.0))
    strider_arms(c, s, reach=settings.get("armDegrees", 10.0), curl=20.0)


def strider_threat(c, s, t):
    """The threat stays low and wide (Patch, 2026-11-21: it never stands up like a person): knees bend deep, the head
    thrusts out under the shell, both arms reach forward and wide with the hands open, and the right, which holds the
    blade, rises. A tremor runs through it."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.2, 0.85)
    shake = gait.wave(t, 10)
    if settings.get("style") == "duck":
        strider_duck(c, settings, up, shake)
        return
    if settings.get("style") == "thrust":
        strider_present(c, settings, up, shake)
        return
    c.bone_offset("hips", (0, 0.04 * up, -settings.get("sinkMetres", 0.3) * up))
    c.bone_rotation("hips", pitch(-4.0 * up))
    c.bone_rotation("spine", pitch(-3.0 * up))
    c.bone_rotation("chest", pitch(4.0 * up) @ roll(0.8 * up * shake))
    c.bone_rotation("neck", pitch(-12.0 * up) @ yaw(3.0 * up * gait.wave(t, 2)))
    c.bone_rotation("head", pitch(18.0 * up) @ yaw(2.0 * up * shake))
    for leg in ("hind.L", "hind.R"):
        c.place(leg, side=settings.get("widenMetres", 0.12) * up)
    reach, out = settings.get("reachDegrees", 55.0), settings.get("spreadDegrees", 32.0)
    strider_arms(c, s, reach=reach * up, out=out * up, curl=-14.0 * up)
    # The blade arm: the upper arm a little higher than the other, the forearm raised from the elbow.
    c.bone_rotation("upperarm.R", pitch((reach + 0.4 * settings.get("bladeDegrees", 35.0)) * up) @ spread("R", out * up))
    c.bone_rotation("forearm.R", pitch(settings.get("bladeDegrees", 35.0) * up))
    c.bone_rotation("hand.R", pitch(-15.0 * up))


def strider_duck(c, settings, up, shake):
    """The skitterer's threat (#10957): it ducks behind its hood as behind a shield, legs folded deep and the front
    pitched down, the knife arm drawn back and up to stab and the free hand reaching forward low, trembling."""
    c.bone_offset("hips", (0, 0.06 * up, -settings.get("sinkMetres", 0.25) * up))
    c.bone_rotation("hips", pitch(-settings.get("duckDegrees", 14.0) * up))
    c.bone_rotation("chest", pitch(-6.0 * up) @ roll(0.8 * up * shake))
    c.bone_rotation("neck", pitch(-10.0 * up))
    for leg in ("hind.L", "hind.R"):
        c.place(leg, side=settings.get("widenMetres", 0.08) * up)
    # The knife arm swings back behind the body, the elbow bent so the blade points forward, ready to stab.
    c.bone_rotation("upperarm.R", pitch(-70.0 * up) @ spread("R", 20.0 * up))
    c.bone_rotation("forearm.R", pitch(55.0 * up))
    c.bone_rotation("hand.R", pitch(-20.0 * up))
    c.bone_rotation("upperarm.L", pitch(45.0 * up) @ spread("L", 10.0 * up))
    c.bone_rotation("forearm.L", pitch(-10.0 * up))
    c.bone_rotation("claw.L", pitch(12.0 * up))


def strider_present(c, settings, up, shake):
    """The implement-carrier's threat (#10957): legs spread and bent, the body lowered, the implement thrust forward at
    arm's length (its core flares: the product's light), the free arm drawn back with the hand open behind it."""
    c.bone_offset("hips", (0, 0.05 * up, -settings.get("sinkMetres", 0.35) * up))
    c.bone_rotation("hips", pitch(-6.0 * up))
    c.bone_rotation("chest", pitch(-4.0 * up) @ roll(0.6 * up * shake))
    for leg in ("hind.L", "hind.R"):
        c.place(leg, side=settings.get("widenMetres", 0.18) * up)
    c.bone_rotation("upperarm.R", pitch(settings.get("reachDegrees", 80.0) * up) @ spread("R", 5.0 * up))
    c.bone_rotation("forearm.R", pitch(-5.0 * up))
    c.bone_rotation("hand.R", pitch(8.0 * up * (0.6 + 0.4 * shake)))
    c.bone_rotation("upperarm.L", pitch(-30.0 * up) @ spread("L", 30.0 * up))
    c.bone_rotation("forearm.L", pitch(-20.0 * up))
    c.bone_rotation("claw.L", pitch(-15.0 * up))


def strider_thrust(c, s, t):
    """A straight thrust (#10957): the held arm draws back as the body coils, then the body lunges over its planted feet
    and the arm punches straight forward at the target, low for the skitterer's knife (`lowDegrees`), level for the
    implement, and draws back."""
    settings = s["bite"]
    wind = gait.ease_in_out(t, 0.0, 0.32) * (1.0 - gait.ease_in_out(t, 0.38, 0.46))
    strike = gait.ease_in_out(t, 0.38, 0.48) * (1.0 - gait.ease_in_out(t, 0.7, 1.0))
    low = settings.get("lowDegrees", 0.0)
    c.bone_offset("hips", (0, -settings.get("lungeMetres", 0.3) * strike + 0.05 * wind, -0.1 * (wind + strike)))
    c.bone_rotation("spine", pitch(-5.0 * strike))
    c.bone_rotation("chest", yaw(-12.0 * wind + 10.0 * strike) @ pitch(-5.0 * strike))
    c.bone_rotation("upperarm.R", pitch(-30.0 * wind + (85.0 - low) * strike))
    c.bone_rotation("forearm.R", pitch(80.0 * wind - 5.0 * strike))
    c.bone_rotation("hand.R", pitch(-20.0 * wind))
    c.bone_rotation("upperarm.L", pitch(20.0 * wind - 25.0 * strike) @ spread("L", 15.0 * strike))
    c.bone_rotation("claw.L", pitch(-15.0))


def strider_strike(c, s, t):
    """The hunter's blow, by the species' `bite.style`: `slash` (the collector's blade stroke) or `thrust`."""
    if s["bite"].get("style") == "thrust":
        strider_thrust(c, s, t)
    else:
        strider_slash(c, s, t)


def strider_slash(c, s, t):
    """The blade stroke (the hunter's bite): the right arm draws up and back as the body coils, then the body lunges
    over its planted feet and the arm sweeps forward and down across to the left, the left hand reaching to grab."""
    settings = s["bite"]
    wind = gait.ease_in_out(t, 0.0, 0.34) * (1.0 - gait.ease_in_out(t, 0.4, 0.5))
    strike = gait.ease_in_out(t, 0.4, 0.52) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    lunge = settings.get("lungeMetres", 0.3)
    c.bone_offset("hips", (0, -lunge * strike + 0.06 * wind, -0.12 * (wind + strike)))
    c.bone_rotation("spine", pitch(-6.0 * strike))
    c.bone_rotation("chest", yaw(-18.0 * wind + 22.0 * strike) @ pitch(-6.0 * strike))
    c.bone_rotation("neck", pitch(-8.0 * strike))
    c.bone_rotation("head", pitch(10.0 * strike))
    c.bone_rotation("upperarm.R", pitch(80.0 * wind + 55.0 * strike) @ spread("R", 30.0 * wind - 20.0 * strike))
    c.bone_rotation("forearm.R", pitch(45.0 * wind - 25.0 * strike))
    c.bone_rotation("hand.R", pitch(-25.0 * wind + 20.0 * strike))
    c.bone_rotation("upperarm.L", pitch(30.0 * wind + 65.0 * strike) @ spread("L", 12.0 * wind))
    c.bone_rotation("forearm.L", pitch(-15.0 * strike))
    c.bone_rotation("claw.L", pitch(14.0 * wind - 30.0 * strike))
    c.bone_rotation("claw.R", pitch(-25.0))


def strider_hurt(c, s, t):
    """A blow lands: it recoils back and aside, the arms flung out, and settles."""
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.1 * hit, -0.05 * hit))
    c.bone_rotation("chest", pitch(8.0 * hit) @ roll(6.0 * hit))
    c.bone_rotation("neck", pitch(10.0 * hit) @ yaw(-14.0 * hit))
    c.bone_rotation("head", pitch(8.0 * hit))
    strider_arms(c, s, reach=20.0 * hit, out=25.0 * hit, curl=-15.0 * hit)


def strider_die(c, s, t):
    """The legs fold and it pitches forward onto its arms, then slumps over onto its side."""
    limp = gait.ease_in_out(t, 0.1, 0.5)
    fall = gait.ease_in_out(t, 0.35, 0.85)
    height = c.leg_length("hind.L")
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-30.0 * limp))
        c.bone_rotation(f"shin.{side}", pitch(50.0 * limp))
        c.bone_rotation(f"cannon.{side}", pitch(-40.0 * limp))
    angle = 75.0 * fall
    c.bone_rotation("root", roll(angle))
    lies = s["die"].get("restShare", 0.3)
    c.bone_offset("root", (0, 0, max(0.0, lies * height - height * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, -0.15 * limp, -0.35 * height * limp))
    c.bone_rotation("spine", pitch(-6.0 * limp))
    c.bone_rotation("neck", pitch(-18.0 * fall) @ yaw(-14.0 * fall))
    c.bone_rotation("head", pitch(-12.0 * fall))
    strider_arms(c, s, swing=(25.0 * limp, 10.0 * limp), out=10.0 * fall, curl=30.0 * fall)


# ---------------------------------------------------------------- strider-bladed: the strider with a pair of scythe limbs

def strider_bladed_bones():
    """The strider's bones with a pair of blade limbs rising from the top of the spine (#10958, the blade-limbed special):
    each an upper limb from its root on the back to a high elbow, a fore-limb out to the wrist, and the scythe blade."""
    bones = strider_bones()
    for side, sign in SIDES.items():
        bones += [
            (f"bladeupper.{side}", "spine", "blade_root", "blade_elbow", sign),
            (f"bladefore.{side}", f"bladeupper.{side}", "blade_elbow", "blade_wrist", sign),
            (f"blade.{side}", f"bladefore.{side}", "blade_wrist", "blade_tip", sign),
        ]
    return bones


def blades(c, raise_=0.0, out=0.0, fold=0.0, curl=0.0, each=(0.0, 0.0)):
    """Poses both blade limbs from their rest (raised and folded forward over the back): `raise_` swings the upper limbs
    back and up, `out` spreads them to the sides, `fold` closes the fore-limb on the upper, `curl` turns the scythe down;
    `each` adds a (left, right) swing of its own."""
    for (side, sign), own in zip(SIDES.items(), each):
        c.bone_rotation(f"bladeupper.{side}", pitch(raise_ + own) @ spread(side, out))
        c.bone_rotation(f"bladefore.{side}", pitch(fold))
        c.bone_rotation(f"blade.{side}", pitch(-curl))


def bladed(base, pose):
    """A strider clip with a blade layer over it: `pose(c, s, t)` sets the blades after the base has set the rest."""
    def clip(c, s, t):
        base(c, s, t)
        pose(c, s, t)
    return clip


def blades_still(c, s, t):
    """Held over the back, swaying a little out of step with each other."""
    blades(c, each=(2.0 * gait.wave(t, lag=0.1), 2.0 * gait.wave(t, lag=0.35)))


def blades_walking(c, s, t):
    blades(c, each=(3.0 * gait.wave(t, 2, lag=0.2), 3.0 * gait.wave(t, 2, lag=0.7)))


def blades_folded(c, s, t):
    """At rest the blades fold down along the dome."""
    blades(c, raise_=-25.0, fold=25.0, curl=10.0)


def blades_threat(c, s, t):
    """The threat (#10958): both blades swept up and out to the sides, ready to strike down, trembling."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.2, 0.85)
    shake = gait.wave(t, 10)
    blades(c, raise_=settings.get("bladeRaiseDegrees", 35.0) * up, out=settings.get("bladeSpreadDegrees", 35.0) * up,
           fold=-20.0 * up, curl=-15.0 * up, each=(1.5 * up * shake, -1.5 * up * shake))


def blades_strike(c, s, t):
    """The blow (#10958): both blades rise back over the body as it coils, then come down together through the target
    in front, the scythes curling in, and draw back up."""
    wind = gait.ease_in_out(t, 0.0, 0.34) * (1.0 - gait.ease_in_out(t, 0.4, 0.5))
    strike = gait.ease_in_out(t, 0.4, 0.52) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    settings = s["bite"]
    blades(c, raise_=settings.get("bladeRaiseDegrees", 40.0) * wind - settings.get("bladeStrikeDegrees", 75.0) * strike,
           out=12.0 * wind - 8.0 * strike, fold=-25.0 * wind + 20.0 * strike, curl=-20.0 * wind + 35.0 * strike)


def blades_hurt(c, s, t):
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    blades(c, raise_=15.0 * hit, out=20.0 * hit)


def blades_dying(c, s, t):
    limp = gait.ease_in_out(t, 0.1, 0.6)
    blades(c, raise_=-30.0 * limp, out=25.0 * limp, fold=30.0 * limp, curl=20.0 * limp)


STRIDER_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (strider_idle, True, True),
    "walk": (strider_walk, True, True),
    "run": (strider_run, True, True),
    "alert": (strider_alert, True, True),
    "crouch": (strider_crouch, True, True),
    "die": (strider_die, False, False),
    "threat": (strider_threat, False, True),
    "bite": (strider_strike, False, True),
    "hurt": (strider_hurt, False, True),
}

STRIDER_BLADED_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (bladed(strider_idle, blades_still), True, True),
    "walk": (bladed(strider_walk, blades_walking), True, True),
    "run": (bladed(strider_run, blades_walking), True, True),
    "alert": (bladed(strider_alert, blades_still), True, True),
    "crouch": (bladed(strider_crouch, blades_folded), True, True),
    "die": (bladed(strider_die, blades_dying), False, False),
    "threat": (bladed(strider_threat, blades_threat), False, True),
    "bite": (bladed(strider_strike, blades_strike), False, True),
    "hurt": (bladed(strider_hurt, blades_hurt), False, True),
}


# ---------------------------------------------------------------- tripod: three legs, two in front and one behind (#10960)

TRIPOD_LEGS = {"hind": ("cannon", "hoof", "hock", -1.0)}
TRIPOD_SIDES = ("L", "R", "C")


def tripod_bones(points):
    """A body on three legs, the two front ones mirrored and a back one on the middle line (`.C` points), each the
    bird's leg: thigh, shin, cannon and toe. A species that places `whip0` to `whipN` hangs a whip arm of N segments
    under its chest (the stilt pack's); one that places `fan` and `fan_tip` has a fan on its head that spreads (the
    channel sifter's combs)."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
    ]
    for side, sign in SIDES.items():
        bones += [
            (f"thigh.{side}", "chest", "hip_socket", "stifle", sign),
            (f"shin.{side}", f"thigh.{side}", "stifle", "hock", sign),
            (f"cannon.{side}", f"shin.{side}", "hock", "fetlock", sign),
            (f"hoof.{side}", f"cannon.{side}", "fetlock", "toe", sign),
        ]
    bones += [
        ("thigh.C", "hips", "hip_socket.C", "stifle.C", 1.0),
        ("shin.C", "thigh.C", "stifle.C", "hock.C", 1.0),
        ("cannon.C", "shin.C", "hock.C", "fetlock.C", 1.0),
        ("hoof.C", "cannon.C", "fetlock.C", "toe.C", 1.0),
    ]
    whips = sum(1 for name in points if name.startswith("whip") and name != "whip0")
    parent = "chest"
    for i in range(1, whips + 1):
        bones.append((f"whip{i}", parent, f"whip{i - 1}", f"whip{i}", 1.0))
        parent = f"whip{i}"
    if "fan" in points:
        bones.append(("fan", "head", "fan", "fan_tip", 1.0))
    return bones


def whip(c, curl=0.0, lash=0.0, t=0.0, sway=0.0):
    """The whip arm hanging under the body: `curl` swings it forward and up, each segment further, so it rises in front
    and arcs over the body like a raised sting; `lash` throws it straight out ahead, the first segment swinging and the
    rest straightening; `sway` swings it side to side."""
    i = 1
    while f"whip{i}" in c.rig.pose.bones:
        own = curl * (0.3 + 0.15 * i) + (lash if i == 1 else -0.15 * lash)
        c.bone_rotation(f"whip{i}", pitch(own) @ yaw(sway * gait.wave(t, lag=0.1 * i)))
        i += 1


def fan(c, spread):
    if "fan" in c.rig.pose.bones:
        c.bone_rotation("fan", pitch(spread))


def tripod_gait(c, settings, t, spine_flex):
    """Three legs in turn, a third of a cycle apart: the body leans over the two that stand and bobs as each lifts."""
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        phase = t + offset
        side = leg.split(".")[1]
        forward, up = gait.foot(phase, duty, stride, settings["liftHind"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-25.0 * swing)
        reach = math.degrees(math.asin(max(-0.9, min(0.9, forward / c.leg_length(leg)))))
        c.bone_rotation(f"thigh.{side}", pitch(TOP_SEGMENT_SHARE * reach))
    sway, bob = settings["swayDegrees"], settings["bob"]
    c.bone_offset("hips", (0, 0, -bob * math.cos(3 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ pitch(spine_flex * gait.wave(t, 3)))
    c.bone_rotation("chest", roll(-0.5 * sway * gait.wave(t, lag=0.2)))
    c.bone_rotation("head", pitch(settings.get("nodDegrees", 2.0) * gait.wave(t, 3, lag=0.3)))
    whip(c, curl=8.0, t=t, sway=settings.get("whipSwayDegrees", 8.0))
    fan(c, 3.0 * gait.wave(t, 3, lag=0.4))


def tripod_walk(c, s, t):
    tripod_gait(c, s["walk"], t, spine_flex=0.8)


def tripod_run(c, s, t):
    tripod_gait(c, s["run"], t, spine_flex=s["run"].get("spineFlex", 2.0))


def tripod_idle(c, s, t):
    """Still, swaying a little over its three feet; the head dips and the fan or whip drifts."""
    c.bone_rotation("hips", roll(1.0 * gait.wave(t)))
    c.bone_rotation("head", pitch(-3.0 + 2.0 * gait.wave(t, 2)) @ yaw(5.0 * gait.wave(t, lag=0.25)))
    whip(c, curl=6.0, t=t, sway=5.0)
    fan(c, 2.0 * gait.wave(t, 2, lag=0.3))


def tripod_alert(c, s, t):
    c.bone_rotation("chest", pitch(4.0))
    c.bone_rotation("head", pitch(10.0) @ yaw(15.0 * gait.wave(t)))
    whip(c, curl=20.0, t=t, sway=3.0)
    fan(c, 8.0)


def tripod_feed(c, s, t):
    """At rest it feeds (#10960): the head and fan dip to the water or the relic light and sift, the body sunk a
    little on its legs, the whip pecking."""
    settings = s.get("feed", {})
    sift = gait.wave(t, 4)
    c.bone_offset("hips", (0, 0, -settings.get("sinkMetres", 0.15)))
    c.bone_rotation("chest", pitch(-settings.get("dipDegrees", 15.0)))
    c.bone_rotation("head", pitch(-settings.get("dipDegrees", 15.0) + 4.0 * sift))
    fan(c, 6.0 * sift)
    whip(c, lash=12.0 * max(0.0, sift), t=t)


def tripod_threat(c, s, t):
    """Its threat (#10960): the body rears back over its back leg, the head and fan lift and the fan spreads wide (the
    sifter puffs its pods: the product's breath), or the whip arm is drawn up and back over the body to lash (the
    pack runner), trembling."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.2, 0.85)
    shake = gait.wave(t, 10)
    c.bone_offset("hips", (0, 0.05 * up, -settings.get("sinkMetres", 0.1) * up))
    c.bone_rotation("hips", pitch(settings.get("rearDegrees", 12.0) * up))
    c.bone_rotation("chest", pitch(4.0 * up) @ roll(0.8 * up * shake))
    c.bone_rotation("head", pitch(settings.get("headDegrees", 15.0) * up) @ yaw(2.0 * up * shake))
    fan(c, settings.get("fanDegrees", 35.0) * up)
    whip(c, curl=settings.get("whipCurlDegrees", 50.0) * up, t=t * 3.0, sway=4.0 * up)


def tripod_strike(c, s, t):
    """The blow: the body drops forward between its legs as the whip arm lashes down at the target ahead, or, with no
    whip, the head and hood drive forward and down (the sifter's shove)."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.32) * (1.0 - gait.ease_in_out(t, 0.36, 0.46))
    strike = gait.ease_in_out(t, 0.36, 0.48) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    c.bone_offset("hips", (0, -settings.get("lungeMetres", 0.2) * strike, -0.08 * strike))
    c.bone_rotation("hips", pitch(8.0 * back - 14.0 * strike))
    c.bone_rotation("head", pitch(10.0 * back - 20.0 * strike))
    whip(c, curl=60.0 * back * (1.0 - strike), lash=80.0 * strike)
    fan(c, 20.0 * back - 10.0 * strike)


def tripod_hurt(c, s, t):
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.06 * hit, -0.04 * hit))
    c.bone_rotation("hips", roll(8.0 * hit))
    c.bone_rotation("head", pitch(10.0 * hit) @ yaw(-12.0 * hit))
    whip(c, curl=30.0 * hit)
    fan(c, 15.0 * hit)


def tripod_die(c, s, t):
    """The legs fold and it tips over onto its side."""
    limp = gait.ease_in_out(t, 0.1, 0.55)
    fall = gait.ease_in_out(t, 0.3, 0.85)
    height = c.leg_length("hind.C")
    for leg in c.targets:
        c.leg_influence(leg, 1.0 - limp)
    for side in TRIPOD_SIDES:
        c.bone_rotation(f"thigh.{side}", pitch(-20.0 * limp))
        c.bone_rotation(f"shin.{side}", pitch(45.0 * limp))
        c.bone_rotation(f"cannon.{side}", pitch(-35.0 * limp))
    angle = 80.0 * fall
    c.bone_rotation("root", roll(angle))
    lies = s["die"].get("restShare", 0.3)
    c.bone_offset("root", (0, 0, max(0.0, lies * height - height * math.cos(math.radians(angle))) * fall))
    c.bone_offset("hips", (0, 0, -0.3 * height * limp))
    c.bone_rotation("head", pitch(-15.0 * fall))
    whip(c, curl=-20.0 * fall)
    fan(c, -10.0 * fall)


TRIPOD_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (tripod_idle, True, True),
    "walk": (tripod_walk, True, True),
    "run": (tripod_run, True, True),
    "alert": (tripod_alert, True, True),
    "feed": (tripod_feed, True, True),
    "die": (tripod_die, False, False),
    "threat": (tripod_threat, False, True),
    "bite": (tripod_strike, False, True),
    "hurt": (tripod_hurt, False, True),
}


# The many-limbed family (#10961): a broad body on up to three pairs of legs, numbered from the front, with an
# optional ring of up to three pairs of handling arms under its front. The brood bearer stands on three pairs; the
# mender on the first and third, with all three pairs of arms.
MANY_PAIRS = (1, 2, 3)
MANY_ARMS = (1, 2, 3)
MANY6_LEGS = {
    "fore": ("shin1", "foot1", "knee1", 1.0),
    "mid": ("shin2", "foot2", "knee2", 1.0),
    "hind": ("shin3", "foot3", "knee3", 1.0),
}
MANY4_LEGS = {"fore": MANY6_LEGS["fore"], "hind": MANY6_LEGS["hind"]}
# Stems drawn as legs, and as feet or pads, on a stand-in body: every numbered pair's.
MANY_LIMB_BONES = tuple(f"{stem}{n}" for stem in ("thigh", "shin", "arm", "forearm") for n in MANY_PAIRS)
MANY_TIP_BONES = tuple(f"{stem}{n}" for stem in ("foot", "pad") for n in MANY_PAIRS)


def many_limbed_bones(points):
    """A body on the leg pairs its species places (`hip1` to `hip3`, front to back, each with its knee, ankle and toe),
    each pair hung from whichever of the hips, spine and chest is nearest along the body, and the arms it places
    (`shoulder1` to `shoulder3`, each with its elbow, wrist and pad) under the chest: upper arm, forearm, pad."""
    bones = [
        ("root", None, "root", "root_tip", 1.0),
        ("hips", "root", "hips", "spine", 1.0),
        ("spine", "hips", "spine", "chest", 1.0),
        ("chest", "spine", "chest", "neck", 1.0),
        ("neck", "chest", "neck", "head", 1.0),
        ("head", "neck", "head", "nose", 1.0),
    ]
    trunk = {name: points[name][1] for name in ("hips", "spine", "chest")}
    for side, sign in SIDES.items():
        for k in MANY_PAIRS:
            if f"hip{k}" not in points:
                continue
            parent = min(trunk, key=lambda name: abs(trunk[name] - points[f"hip{k}"][1]))
            bones += [
                (f"thigh{k}.{side}", parent, f"hip{k}", f"knee{k}", sign),
                (f"shin{k}.{side}", f"thigh{k}.{side}", f"knee{k}", f"ankle{k}", sign),
                (f"foot{k}.{side}", f"shin{k}.{side}", f"ankle{k}", f"toe{k}", sign),
            ]
        for a in MANY_ARMS:
            if f"shoulder{a}" not in points:
                continue
            bones += [
                (f"arm{a}.{side}", "chest", f"shoulder{a}", f"elbow{a}", sign),
                (f"forearm{a}.{side}", f"arm{a}.{side}", f"elbow{a}", f"wrist{a}", sign),
                (f"pad{a}.{side}", f"forearm{a}.{side}", f"wrist{a}", f"pad{a}", sign),
            ]
    return bones


def many_arms(c, lift=0.0, spread=0.0, reach=0.0, t=0.0, work=0.0):
    """The arm ring: `lift` swings every arm forward and up from the shoulder, `spread` fans them out to the sides,
    `reach` straightens the elbows so the pads go forward, and `work` presses them in turn, each pair a third of a
    cycle after the last (smoothing paste, or sealing)."""
    for a in MANY_ARMS:
        for side, sign in SIDES.items():
            if f"arm{a}.{side}" not in c.rig.pose.bones:
                continue
            press = work * max(0.0, gait.wave(t, 2, lag=(a - 1) / 3.0 + (0.5 if sign < 0 else 0.0)))
            c.bone_rotation(f"arm{a}.{side}", pitch(lift + 12.0 * press) @ yaw(sign * spread * (0.6 + 0.2 * a)))
            c.bone_rotation(f"forearm{a}.{side}", pitch(reach - 25.0 * press))
            c.bone_rotation(f"pad{a}.{side}", pitch(10.0 * press))


def many_gait(c, settings, t, spine_flex):
    """Each leg steps at its own phase (the settings' `phases`, by leg name): an alternating tripod for six legs, a
    walk for four. The body rolls a little over the side that stands."""
    duty, stride = settings["duty"], settings["stride"]
    for leg, offset in settings["phases"].items():
        if leg not in c.targets:
            continue
        phase = t + offset
        kind, side = leg.split(".")
        k = c.legs[kind][0][-1]
        forward, up = gait.foot(phase, duty, stride, settings["lift"])
        swing = 0.0 if gait.planted(phase, duty) else math.sin(math.pi * ((phase % 1.0) - duty) / (1.0 - duty))
        c.place(leg, forward=forward, up=up, toe_pitch=-15.0 * swing)
        reach = math.degrees(math.asin(max(-0.9, min(0.9, forward / c.leg_length(leg)))))
        c.bone_rotation(f"thigh{k}.{side}", pitch(TOP_SEGMENT_SHARE * reach))
    sway, bob = settings["swayDegrees"], settings["bob"]
    c.bone_offset("hips", (0, 0, -bob * math.cos(2 * gait.TAU * t)))
    c.bone_rotation("hips", roll(sway * gait.wave(t)) @ pitch(spine_flex * gait.wave(t, 2)))
    c.bone_rotation("chest", roll(-0.5 * sway * gait.wave(t, lag=0.2)))
    many_arms(c, lift=settings.get("armLiftDegrees", 0.0), t=t, work=0.0)


def many_walk(c, s, t):
    many_gait(c, s["walk"], t, spine_flex=0.6)


def many_run(c, s, t):
    many_gait(c, s["run"], t, spine_flex=s["run"].get("spineFlex", 1.0))


def many_idle(c, s, t):
    """Still on its feet, breathing: the body rises and settles, the front turns a little, the arms drift."""
    c.bone_offset("hips", (0, 0, 0.01 * gait.wave(t, 2)))
    c.bone_rotation("chest", pitch(1.0 * gait.wave(t, 2, lag=0.2)))
    c.bone_rotation("head", yaw(4.0 * gait.wave(t, lag=0.25)))
    many_arms(c, lift=4.0 * gait.wave(t, 2, lag=0.3), spread=3.0 * gait.wave(t, lag=0.1))


def many_alert(c, s, t):
    """It rises a little on its legs and turns its front toward what it heard; the arms lift."""
    c.bone_offset("hips", (0, 0, 0.04))
    c.bone_rotation("chest", pitch(5.0))
    c.bone_rotation("head", pitch(4.0) @ yaw(10.0 * gait.wave(t)))
    many_arms(c, lift=20.0, spread=8.0)


def many_feed(c, s, t):
    """Its work at rest (#10961): the body settles low on its legs and the front dips; the arms press forward in turn,
    smoothing paste over a crack, which is also the mender's sealing at a doorway. The bearer, with no arms, settles
    and sways, tending its pods."""
    settings = s.get("feed", {})
    work = gait.wave(t, 2)
    c.bone_offset("hips", (0, 0, -settings.get("sinkMetres", 0.1)))
    c.bone_rotation("chest", pitch(-settings.get("dipDegrees", 8.0) + 2.0 * work))
    c.bone_rotation("hips", roll(1.5 * gait.wave(t)))
    many_arms(c, lift=settings.get("armLiftDegrees", 35.0), spread=6.0, reach=settings.get("reachDegrees", 30.0),
              t=t, work=1.0)


def many_threat(c, s, t):
    """Its threat (#10961): the bearer braces low and tips its back up toward the threat so its pods face it (they
    split as the product's pieces); the mender rears its front up with every arm spread wide."""
    settings = s["threat"]
    up = gait.rise_hold_fall(t, 0.25, 0.85)
    shake = gait.wave(t, 8)
    c.bone_offset("hips", (0, 0, -settings.get("sinkMetres", 0.1) * up))
    c.bone_rotation("hips", pitch(settings.get("rearDegrees", 10.0) * up) @ roll(0.6 * up * shake))
    c.bone_rotation("chest", pitch(settings.get("chestDegrees", 5.0) * up))
    many_arms(c, lift=settings.get("armLiftDegrees", 60.0) * up, spread=settings.get("armSpreadDegrees", 30.0) * up,
              reach=20.0 * up, t=t * 2.0, work=0.0)


def many_strike(c, s, t):
    """A shove: it draws back, then drives its front forward and down at what is ahead, the arms jabbing with it."""
    settings = s["bite"]
    back = gait.ease_in_out(t, 0.0, 0.32) * (1.0 - gait.ease_in_out(t, 0.36, 0.46))
    strike = gait.ease_in_out(t, 0.36, 0.48) * (1.0 - gait.ease_in_out(t, 0.72, 1.0))
    c.bone_offset("hips", (0, -settings.get("lungeMetres", 0.15) * strike + 0.06 * back, -0.04 * strike))
    c.bone_rotation("hips", pitch(6.0 * back - 8.0 * strike))
    c.bone_rotation("chest", pitch(4.0 * back - 6.0 * strike))
    many_arms(c, lift=40.0 * back + 50.0 * strike, reach=-20.0 * back + 40.0 * strike)


def many_hurt(c, s, t):
    hit = gait.rise_hold_fall(t, 0.12, 0.3)
    c.bone_offset("hips", (0, 0.05 * hit, -0.03 * hit))
    c.bone_rotation("hips", roll(6.0 * hit))
    c.bone_rotation("chest", pitch(6.0 * hit))
    many_arms(c, lift=-10.0 * hit, spread=15.0 * hit)


def many_die(c, s, t):
    """The feet stay where they stand while the body sinks between them (`dropShare` of its leg's reach), so the knees
    fold under the shell, and it tips a little onto one side as it settles; the arms curl in."""
    fall = gait.ease_in_out(t, 0.1, 0.7)
    height = c.leg_length(next(iter(c.targets)))
    c.bone_offset("hips", (0, 0, -s["die"].get("dropShare", 0.5) * height * fall))
    c.bone_rotation("hips", roll(s["die"].get("tipDegrees", 10.0) * fall) @ pitch(-4.0 * fall))
    many_arms(c, lift=-30.0 * fall, reach=-50.0 * fall)


MANY_CLIPS = {
    "rest": (clip_rest, False, True),
    "idle": (many_idle, True, True),
    "walk": (many_walk, True, True),
    "run": (many_run, True, True),
    "alert": (many_alert, True, True),
    "feed": (many_feed, True, True),
    "die": (many_die, False, False),
    "threat": (many_threat, False, True),
    "bite": (many_strike, False, True),
    "hurt": (many_hurt, False, True),
}


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
    # Whether the bones depend on the species' points (a tail as long as it is drawn): `bones(points)`.
    sized: bool = False
    # Whether the root bone carries skin. A snake's root lies inside its body (#10883), where it would hold the middle
    # still while the chain coils around it.
    root_deforms: bool = True
    # The sides its legs stand on: left and right, or with "C" a leg on the middle line too (#10960, the tripods).
    leg_sides: tuple = ("L", "R")


FAMILIES = {
    "plantigrade": Family(plantigrade_bones, PLANTIGRADE_LEGS, PLANTIGRADE_CLIPS,
                          (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                          ("thigh", "shin", "foot", "upperarm", "forearm", "hand"), ("claw",)),
    "ungulate": Family(ungulate_bones, UNGULATE_LEGS, UNGULATE_CLIPS,
                       (("shin", "cannon", "hoof"), ("forearm", "fcannon", "fhoof")), "fetlock", "ffetlock",
                       ("thigh", "shin", "cannon", "upperarm", "forearm", "fcannon"), ("hoof", "fhoof")),
    "ungulate-eared": Family(ungulate_eared_bones, UNGULATE_LEGS, UNGULATE_EARED_CLIPS,
                             (("shin", "cannon", "hoof"), ("forearm", "fcannon", "fhoof")), "fetlock", "ffetlock",
                             ("thigh", "shin", "cannon", "upperarm", "forearm", "fcannon"), ("hoof", "fhoof")),
    "graviportal": Family(graviportal_bones, PLANTIGRADE_LEGS, GRAVIPORTAL_CLIPS,
                          (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                          ("thigh", "shin", "upperarm", "forearm"), ("foot", "hand")),
    "bird": Family(bird_bones, BIRD_LEGS, BIRD_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                   ("thigh", "shin", "cannon"), ("hoof",)),
    "theropod": Family(theropod_bones, BIRD_LEGS, THEROPOD_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                       ("thigh", "shin", "cannon"), ("hoof", "hand")),
    # Three legs, two in front and one behind on the middle line (#10960): the channel sifter and the stilt pack.
    "tripod": Family(tripod_bones, TRIPOD_LEGS, TRIPOD_CLIPS, (("shin", "cannon", "hoof"),), "fetlock.C", "fetlock",
                     ("thigh", "shin", "cannon", "whip"), ("hoof",), sized=True, leg_sides=TRIPOD_SIDES),
    "many-limbed": Family(many_limbed_bones, MANY6_LEGS, MANY_CLIPS,
                          (("thigh1", "shin1", "foot1"), ("thigh2", "shin2", "foot2"), ("thigh3", "shin3", "foot3")),
                          "ankle3", "ankle1", MANY_LIMB_BONES, MANY_TIP_BONES, sized=True),
    "many-limbed-armed": Family(many_limbed_bones, MANY4_LEGS, MANY_CLIPS,
                                (("thigh1", "shin1", "foot1"), ("thigh3", "shin3", "foot3")),
                                "ankle3", "ankle1", MANY_LIMB_BONES, MANY_TIP_BONES, sized=True),
    "strider": Family(strider_bones, BIRD_LEGS, STRIDER_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                      ("thigh", "shin", "cannon", "upperarm", "forearm"), ("hoof", "hand", "claw")),
    # The strider with a pair of scythe limbs on its back (#10958).
    "strider-bladed": Family(strider_bladed_bones, BIRD_LEGS, STRIDER_BLADED_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                             ("thigh", "shin", "cannon", "upperarm", "forearm", "bladeupper", "bladefore"), ("hoof", "hand", "claw", "blade")),
    "percher": Family(bird_bones, BIRD_LEGS, PERCHER_CLIPS, (("shin", "cannon", "hoof"),), "fetlock", "fetlock",
                      ("thigh", "shin", "cannon"), ("hoof",)),
    "flyer": Family(flyer_bones, {}, FLYER_CLIPS, (), "body", "body", ("wing1", "wing2", "wing3"), ("streamer1", "streamer2")),
    "low-slung": Family(low_slung_bones, PLANTIGRADE_LEGS, LOW_SLUNG_CLIPS,
                        (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                        ("thigh", "shin", "upperarm", "forearm"), ("foot", "hand"), sized=True),
    # The low-slung legs and feet with a bending trunk and a tail as long as the species draws it (#10879).
    # No legs (#10883): body segments from the tail tip to the neck; the product pitches it between s2 and the head.
    "serpent": Family(serpent_bones, {}, SERPENT_CLIPS, (), "s2", "head", (), (), sized=True, root_deforms=False),
    "saurian": Family(saurian_bones, SAURIAN_LEGS, SAURIAN_CLIPS,
                      (("thigh", "shin", "foot"), ("upperarm", "forearm", "hand")), "ankle", "wrist",
                      ("thigh", "shin", "upperarm", "forearm"), ("foot", "hand"), sized=True),
}
