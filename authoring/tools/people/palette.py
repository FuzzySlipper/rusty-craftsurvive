#!/usr/bin/env python3
"""A door figure's palette (#10027): ramps of shades per material, designed from the figure's own pages.

The Engine draws a door figure smooth and lit, cuts it into blocks of screen pixels, and gives each block the nearest
palette colour by sRGB value, after the lighting and before the view's finish (rusty-engine #10716). So the palette
must hold the figure's colours as lit, dark (night, before auto exposure lifts it) and bright (lamp and day), not only
as painted. For each material (its pixels clustered by colour in OKLab), a ramp runs from near black to near white,
each shade a fixed ratio lighter than the one below (so the night's darker light and its exposure, a scale, move a
colour along the ramp rather than crowding it into the darkest shades), in that material's hue, chroma falling toward both ends, shadows turned a little cool and
highlights a little warm, as a pixel artist ramps a material.
With --lit, each ramp is repeated under each of LIGHTS (its linear colours multiplied by the tint), so a lamp-lit or
moonlit block finds a shade of its own material in that light rather than the nearest unlit one.
    palette.py <door folder> <out.json> <materials> <shades> [--lit]
"""
import glob
import json
import sys

import numpy as np
from PIL import Image
from scipy.cluster.vq import kmeans2

SOLID = 128               # page alpha that counts as the figure
LIGHT_RANGE = (.12, .96)  # OKLab lightness of a ramp's darkest and lightest shade
LIGHTNESS_WEIGHT = .5     # materials are told apart more by hue and chroma than by lightness
SHADOW_HUE, LIGHT_HUE = 255.0, 75.0   # degrees: shadows turn toward blue, highlights toward yellow
SHADOW_TURN, LIGHT_TURN = 14.0, 10.0  # degrees at a ramp's darkest and lightest shade
LIGHTS = {"lamp": (1.0, .78, .52), "moon": (.75, .85, 1.0)}   # linear tints a ramp is also lit by, beside white
SAMPLES = 60000
SEED = 1


def srgb_to_linear(c):
    return np.where(c <= .04045, c / 12.92, ((c + .055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= .0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - .055)


M1 = np.array([[.4122214708, .5363325363, .0514459929], [.2119034982, .6806995451, .1073969566],
               [.0883024619, .2817188376, .6299787005]])
M2 = np.array([[.2104542553, .7936177850, -.0040720468], [1.9779984951, -2.4285922050, .4505937099],
               [.0259040371, .7827717662, -.8086757660]])


def oklab(rgb):
    lms = np.cbrt(srgb_to_linear(rgb) @ M1.T)
    return lms @ M2.T


def from_oklab(lab):
    lms = (lab @ np.linalg.inv(M2).T) ** 3
    return linear_to_srgb(lms @ np.linalg.inv(M1).T)


def turn_toward(hue, target, degrees):
    delta = (target - hue + 180) % 360 - 180
    return hue + np.clip(delta, -degrees, degrees)


def ramp(centre, shades):
    """One material's shades: even OKLab lightness, its hue turned cool in shadow and warm in light, chroma peaking at
    the material's own lightness and falling toward black and white."""
    l0, a, b = centre
    chroma = np.hypot(a, b)
    hue = np.degrees(np.arctan2(b, a)) % 360
    out = []
    for light in np.geomspace(*LIGHT_RANGE, shades):
        if light < l0:
            t = (l0 - light) / max(l0 - LIGHT_RANGE[0], 1e-6)
            h, c = turn_toward(hue, SHADOW_HUE, SHADOW_TURN * t), chroma * (1 - .6 * t)
        else:
            t = (light - l0) / max(LIGHT_RANGE[1] - l0, 1e-6)
            h, c = turn_toward(hue, LIGHT_HUE, LIGHT_TURN * t), chroma * (1 - .7 * t)
        lab = np.array([light, c * np.cos(np.radians(h)), c * np.sin(np.radians(h))])
        out.append(from_oklab(lab[None])[0])
    return out


def main(folder, out, materials, shades, lit=False):
    pixels = []
    for page in sorted(glob.glob(f"{folder}/*.png")):
        rgba = np.asarray(Image.open(page).convert("RGBA")).reshape(-1, 4)
        pixels.append(rgba[rgba[:, 3] >= SOLID, :3] / 255)
    rgb = np.concatenate(pixels)
    rng = np.random.default_rng(SEED)
    rgb = rgb[rng.choice(len(rgb), min(len(rgb), SAMPLES), replace=False)]
    lab = oklab(rgb)
    weighted = lab * [LIGHTNESS_WEIGHT, 1, 1]
    centres, labels = kmeans2(weighted, materials, seed=SEED, minit="++")
    colours = []
    for k in range(materials):
        members = lab[labels == k]
        if len(members) == 0:
            continue
        shades_of = ramp(members.mean(0), shades)
        colours += shades_of
        if lit:
            for tint in LIGHTS.values():
                colours += [linear_to_srgb(srgb_to_linear(np.array(c)) * tint) for c in shades_of]
    hexes = ["#%02x%02x%02x" % tuple(int(round(v * 255)) for v in c) for c in colours]
    hexes = list(dict.fromkeys(hexes))
    lights = f", each also under {' and '.join(LIGHTS)} light" if lit else ""
    json.dump({"about": f"A door figure's palette (#10027): {materials} materials of its own pages, {shades} shades each{lights}, "
                        "made by authoring/tools/people/palette.py. sRGB hex; each block of the figure's screen-pixel grid takes "
                        "the nearest once lit.", "colours": hexes}, open(out, "w"), indent=1)
    print(out, len(hexes), "colours")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), "--lit" in sys.argv[5:])
