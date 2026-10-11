"""Write CraftSurvive's twilight panoramas for dawn and dusk (#9805).

The sun keeps one bearing all year (WorldClock has no seasons): it rises toward +x and sets toward -x,
both tilted toward +z. So the twilight can be painted onto world-fixed panoramas, as the sky's base
already is (#9804).

DayNightSky steps from the day through two twilight panoramas to the night, blending each
neighbouring pair, so every step meets the next without a jump:
- glow: the sun on the horizon. A wide warm band on the sun's side, gold above orange. Opposite it,
  the earth's shadow is just rising, with the pink anti-twilight band above it.
- afterglow: the sun a few degrees under. The glow has narrowed and reddened, with a faint purple
  light above it. The earth's shadow stands higher and the pink band has faded.

Each comes vivid or muted, so some evenings are spectacular and some are not. The base under the
twilight is the day and night gradients of make-sky-gradient.py, partly darkened toward night. The
panoramas are smooth, so they are half the day panorama's width.

    python3 scripts/make-twilight-sky.py content/game/textures

It prints each panorama's horizon colour away from the sun, in linear light. DayNightSky carries it
as the open fog colour (TwilightHorizons), because the haze toward the sun supplies that side.
"""
import argparse
import importlib.util
from pathlib import Path

import numpy as np
from PIL import Image

_spec = importlib.util.spec_from_file_location("sky_gradient", Path(__file__).with_name("make-sky-gradient.py"))
sky = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(sky)

WIDTH = 1024
SEED = 9805
DITHER = 0.5 / 255

# Unit directions toward the rising and the setting sun on the horizon, as WorldClock.TowardSun has them.
ARC_TILT = 0.35
BEARINGS = {"dawn": (1.0, ARC_TILT), "dusk": (-1.0, ARC_TILT)}

# Colours in linear light.
ORANGE = np.array([1.0, 0.36, 0.09])
GOLD = np.array([1.0, 0.70, 0.32])
PALE = np.array([0.95, 0.85, 0.62])
RED = np.array([0.85, 0.20, 0.05])
EMBER = np.array([1.0, 0.48, 0.16])
PURPLE = np.array([0.42, 0.24, 0.46])
PINK = np.array([0.80, 0.46, 0.52])
SHADOW = np.array([0.20, 0.25, 0.40])

# Each stage: how far its base has darkened toward the night gradient (in linear light), the glow
# (its spread in bearing and height, degrees, and its strength), the earth's shadow (its top, in
# degrees, and how much of the base it takes), the pink band (its centre and half-width in
# degrees, and its strength), and the purple light high over the glow (its strength).
STAGES = {
    "glow": dict(night=0.4, spread=62.0, height=22.0, glow=0.92, shadow_top=3.0, shadow=0.5,
                 pink_at=9.0, pink_width=7.0, pink=0.4, purple=0.0),
    "afterglow": dict(night=0.7, spread=45.0, height=11.0, glow=0.8, shadow_top=8.0, shadow=0.6,
                      pink_at=15.0, pink_width=7.0, pink=0.22, purple=0.3),
}

# A muted evening: the twilight's colours at this share, and greyed by this much.
MOODS = {"vivid": (1.0, 0.0), "muted": (0.45, 0.45)}

# The rows averaged for the fog's colour: the horizon (0 to this many degrees) away from the sun.
FOG_DEGREES = 1.5


def smooth(x, a, b):
    t = np.clip((x - a) / (b - a), 0.0, 1.0)
    return t * t * (3.0 - 2.0 * t)


def grey(colour, share):
    luminance = colour @ np.array([0.2126, 0.7152, 0.0722])
    return colour + (luminance[..., None] - colour) * share


def base_rows(night, height):
    """The day gradient darkened toward the night's, in linear light, one row per elevation."""
    day = sky.linear(np.clip(sky.gradient(sky.DAY, height * 2)[:, 0], 0, 1))
    dark = sky.linear(np.clip(sky.gradient(sky.NIGHT, height * 2)[:, 0], 0, 1))
    return day + (dark - day) * night


def twilight(stage, mood, bearing, width=WIDTH):
    height = width // 2
    s = STAGES[stage]
    share, greyed = MOODS[mood]
    elevation = (90 - (np.arange(height) + 0.5) / height * 180)[:, None]
    # The panorama's column u looks along atan2(z, x) = 2 pi (u - 1/2) (the Engine's panorama_uv).
    azimuth = np.degrees(2 * np.pi * ((np.arange(width) + 0.5) / width - 0.5))[None, :]
    toward = np.degrees(np.arctan2(bearing[1], bearing[0]))
    offset = (azimuth - toward + 180) % 360 - 180
    away = 180 - np.abs(offset)
    up = np.maximum(elevation, 0)

    colour = np.repeat(base_rows(s["night"], height)[:, None, :], width, 1)

    # The earth's shadow: a blue-grey band rising opposite the sun, soft at its top.
    shadow = s["shadow"] * share * np.exp(-((away / 85) ** 2)) * (1 - smooth(elevation, s["shadow_top"] - 2, s["shadow_top"] + 2)) * smooth(elevation, -8, -2)
    colour = colour + (SHADOW * colour.mean(-1, keepdims=True) * 1.6 - colour) * shadow[..., None]

    # The anti-twilight: a pink band over the earth's shadow, widest opposite the sun.
    pink = s["pink"] * share * np.exp(-((away / 95) ** 2)) * np.exp(-(((elevation - s["pink_at"]) / s["pink_width"]) ** 2))
    colour = colour + (grey(PINK, greyed) * 0.6 - colour) * pink[..., None]

    # The glow on the sun's side: hottest on the horizon under the sun, spreading along it, layered by
    # height from the horizon's colour through gold toward a pale yellow, fading upward.
    along = np.exp(-((offset / s["spread"]) ** 2)) * 0.8 + np.exp(-((offset / (s["spread"] * 2.2)) ** 2)) * 0.2
    rise = np.exp(-up / s["height"]) * smooth(elevation, -4, 0)
    if stage == "glow":
        hue = np.where(up[..., None] < 8, ORANGE + (GOLD - ORANGE) * smooth(up, 0, 8)[..., None],
                       GOLD + (PALE - GOLD) * smooth(up, 8, 26)[..., None])
    else:
        hue = RED + (EMBER - RED) * smooth(up, 0, 9)[..., None]
    glow = s["glow"] * share * along * rise
    colour = colour + (grey(hue, greyed) - colour) * np.clip(glow, 0, 1)[..., None]

    # The purple light: a faint rose-violet high over the afterglow.
    if s["purple"] > 0:
        purple = s["purple"] * share * np.exp(-((offset / 50) ** 2)) * np.exp(-(((elevation - 18) / 9) ** 2))
        colour = colour + grey(PURPLE, greyed) * purple[..., None] * 0.35

    image = sky.srgb(np.clip(colour, 0, 1))
    image += np.random.default_rng(SEED).uniform(-DITHER, DITHER, image.shape)
    rows = max(1, int(height * FOG_DEGREES / 180))
    middle = height // 2
    fog = colour[middle - rows:middle][:, np.abs(offset[0]) > 90].reshape(-1, 3).mean(0)
    return image, fog


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    for side, bearing in BEARINGS.items():
        for stage in STAGES:
            for mood in MOODS:
                image, fog = twilight(stage, mood, bearing)
                name = f"sky-{side}-{stage}-{mood}.png"
                rgb = (np.clip(image, 0, 1) * 255 + 0.5).astype(np.uint8)
                Image.fromarray(rgb, "RGB").convert("RGBA").save(args.directory / name)
                print(f"{name}: horizon away from the sun (linear) {fog[0]:.4f}, {fog[1]:.4f}, {fog[2]:.4f}")


if __name__ == "__main__":
    main()
