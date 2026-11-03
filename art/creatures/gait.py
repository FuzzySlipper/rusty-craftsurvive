"""Gait math for the creature clip generator (#10568): plain Python, no Blender.

A clip is in place: the root stays at the origin and planted feet slide backwards at the gait's
speed, so the product plays a clip at `speed / clip speed` and the feet stay put on the ground.

Phases are fractions of one cycle. A foot's own phase starts at touch-down: it is planted for the
first `duty` of its cycle and swings forward for the rest.
"""
import math

TAU = 2.0 * math.pi


def smoothstep(x):
    x = min(1.0, max(0.0, x))
    return x * x * (3.0 - 2.0 * x)


def foot(phase, duty, stride, lift):
    """A foot's offset from its rest position at a cycle phase: (forward metres, up metres).

    `stride` is how far the body travels in one cycle. Planted, the foot moves back linearly by
    stride x duty, centred on its rest position. Swinging, it eases forward again and rises on a
    sine arch `lift` high.
    """
    phase %= 1.0
    sweep = stride * duty
    if phase < duty:
        s = phase / duty
        return sweep * (0.5 - s), 0.0
    s = (phase - duty) / (1.0 - duty)
    return sweep * (-0.5 + smoothstep(s)), lift * math.sin(math.pi * s)


def planted(phase, duty):
    return (phase % 1.0) < duty


def speed(stride, cycle_seconds):
    """Ground speed, metres a second, at which a clip's planted feet do not slide."""
    return stride / cycle_seconds


def wave(t, cycles=1.0, lag=0.0):
    """A sine over clip time t in [0, 1), `cycles` times per clip, delayed by `lag` of a cycle."""
    return math.sin(TAU * (cycles * t - lag))


def ease_in_out(t, start, end):
    """0 before start, 1 after end, smooth between: for one-shot clips' phases."""
    if end <= start:
        return 1.0 if t >= end else 0.0
    return smoothstep((t - start) / (end - start))


def rise_hold_fall(t, rise_end, fall_start, fall_end=1.0):
    """0 to 1 by rise_end, held, back to 0 from fall_start to fall_end."""
    return ease_in_out(t, 0.0, rise_end) * (1.0 - ease_in_out(t, fall_start, fall_end))


if __name__ == "__main__":
    # A quick self-check: a planted foot moves back at the body's speed, so it does not slide.
    stride, duty, cycle = 1.4, 0.7, 1.8
    a, _ = foot(0.1, duty, stride, 0.2)
    b, _ = foot(0.5, duty, stride, 0.2)
    assert abs((a - b) / (0.4 * cycle) - speed(stride, cycle)) < 1e-9
    # The swing comes back to touch-down height and position.
    c, up = foot(1.0 - 1e-9, duty, stride, 0.2)
    assert abs(c - foot(0.0, duty, stride, 0.2)[0]) < 1e-6 and up < 1e-6
    print("gait ok")
