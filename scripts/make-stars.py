"""Write CraftSurvive's star catalogue and Milky Way band (#9807).

The catalogue's frame is the sky's: +Z toward the celestial pole (north, WorldClock.LatitudeDegrees
above the horizon), +X toward the stars on the meridian at the first midnight, +Y completing it.
WorldClock.StarOrientation turns it into the world.

- About 4,500 stars from magnitude -1 to 6.2, counted as the real sky's (about three times as many
  for each magnitude fainter), the faint ones gathered toward the Milky Way's plane. Mostly white,
  some blue, some orange and red.
- A pole star of magnitude 1.5 at the pole, so a player can find north.
- Authored asterisms of bright stars, compass-true and stable in every world. The Sled's two pointer
  stars line up on the pole star.
- The Milky Way: a 2:1 equirectangular band in the same frame, laid out as the Engine samples it
  (as a sky panorama: u along atan2(z, x), v from the frame's +Y down), clumped, with a dark rift
  along its middle and a brighter core.

    python3 scripts/make-stars.py content/game/sky

It writes the catalogue in the Engine's format (sky.stars.json, rusty.stars.v1: the pole star first),
the asterisms by catalogue index (asterisms.json, the product's own) and milky-way.png, and prints
the count by magnitude.
"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image

SEED = 9807
FAINTEST = 6.2
BRIGHTEST = -1.0
COUNT_TO_SIX = 4200
# The Milky Way's plane: its pole's declination and right ascension, degrees (tilted well off the equator).
BAND_POLE = (27.0, 192.0)
BAND_WIDTH_DEGREES = 7.0
# The band's brightest, in linear light: a faint glow, never brighter than the sky's horizon.
BAND_BRIGHTNESS = 0.18
# The share of faint stars drawn toward the band, by how faint they are.
BAND_GATHERING = 0.55
POLE_STAR_MAGNITUDE = 1.5

# Asterisms: name, centre (declination, right ascension in degrees), and stars as offsets (degrees
# east, north of the centre) with magnitudes; lines join stars by index.
ASTERISMS = [
    ("The Sled", (62.0, 165.0), [(-9, 3, 1.8), (-9, -3, 2.3), (-2, -4, 2.4), (-1, 3, 3.3), (6, 4, 1.8), (12, 2, 2.2), (17, 0, 1.9)],
     [(0, 1), (1, 2), (2, 3), (3, 0), (3, 4), (4, 5), (5, 6)]),
    ("The Lantern", (50.0, 15.0), [(0, 0, 2.2), (4, 2, 2.7), (8, 0, 2.5), (8, -5, 3.0), (0, -5, 2.9), (4, -9, 3.4)],
     [(0, 1), (1, 2), (2, 3), (3, 4), (4, 0), (3, 5), (4, 5)]),
    ("The Hunter's Bow", (2.0, 85.0), [(0, 9, 0.4), (-2, 3, 2.1), (-3, 0, 1.7), (-2, -3, 2.0), (0, -9, 0.2), (5, 0, 2.6), (9, 0, 3.1)],
     [(0, 1), (1, 2), (2, 3), (3, 4), (2, 5), (5, 6)]),
    ("The Crown", (28.0, 235.0), [(-7, 0, 2.8), (-5, 3, 3.2), (-2, 4, 2.2), (2, 4, 3.0), (5, 3, 3.3), (7, 0, 3.6)],
     [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5)]),
    ("The Serpent", (-12.0, 270.0), [(-14, -4, 2.9), (-10, -1, 3.1), (-6, 1, 2.6), (-2, 0, 3.4), (2, -2, 3.0), (6, -1, 2.5), (10, 2, 3.2), (13, 5, 2.8)],
     [(0, 1), (1, 2), (2, 3), (3, 4), (4, 5), (5, 6), (6, 7)]),
    ("The Anvil", (-30.0, 120.0), [(-6, 2, 1.6), (6, 2, 2.4), (3, -2, 2.9), (-3, -2, 2.7), (0, -6, 3.3)],
     [(0, 1), (1, 2), (2, 3), (3, 0), (2, 4), (3, 4)]),
    ("The Twins", (25.0, 110.0), [(-3, 4, 1.2), (3, 4, 1.6), (-3, -3, 3.0), (3, -3, 2.9), (-4, -8, 3.4), (4, -8, 3.5)],
     [(0, 2), (2, 4), (1, 3), (3, 5)]),
    ("The Kite", (40.0, 300.0), [(0, 8, 0.0), (-4, 0, 2.2), (0, -3, 2.6), (4, 0, 2.5), (0, -11, 3.1)],
     [(0, 1), (1, 2), (2, 3), (3, 0), (2, 4)]),
    ("The Heron", (-45.0, 330.0), [(0, 6, 1.7), (-2, 1, 2.8), (0, -2, 2.4), (3, -6, 3.0), (-4, -7, 3.2)],
     [(0, 1), (1, 2), (2, 3), (2, 4)]),
    ("The Hearth", (5.0, 30.0), [(-3, 0, 2.0), (0, 3, 2.9), (3, 0, 2.6), (0, -3, 3.0)],
     [(0, 1), (1, 2), (2, 3), (3, 0)]),
]


def direction(declination, right_ascension):
    d, r = np.radians(declination), np.radians(right_ascension)
    return np.stack([np.cos(d) * np.cos(r), np.cos(d) * np.sin(r), np.sin(d)], -1)


def colour(rng, count):
    """Star colours in linear light: mostly white, some blue, some orange and red."""
    palette = np.array([[0.75, 0.85, 1.0], [0.9, 0.95, 1.0], [1.0, 1.0, 1.0], [1.0, 0.95, 0.85], [1.0, 0.82, 0.6], [1.0, 0.65, 0.45]])
    weights = np.array([0.08, 0.2, 0.32, 0.22, 0.13, 0.05])
    return palette[rng.choice(len(palette), count, p=weights)]


def field(rng):
    """Background stars: magnitudes counted as the sky's, the faint gathered toward the band."""
    # N(< m) grows as 10^(0.48 m): draw magnitudes by inverting that count.
    slope = 0.48
    top = COUNT_TO_SIX * 10 ** (slope * (FAINTEST - 6.0))
    count = int(top)
    u = rng.uniform(10 ** (slope * BRIGHTEST) / 10 ** (slope * FAINTEST), 1.0, count)
    magnitudes = FAINTEST + np.log10(u) / slope
    directions = rng.normal(size=(count, 3))
    directions /= np.linalg.norm(directions, axis=1, keepdims=True)
    # Pull a share of the faint stars toward the band's plane.
    pole = direction(*BAND_POLE)
    faintness = np.clip((magnitudes - 3.0) / (FAINTEST - 3.0), 0, 1)
    gathered = rng.uniform(size=count) < BAND_GATHERING * faintness
    height = directions @ pole
    squash = np.where(gathered, rng.uniform(0.05, 0.3, count), 1.0)
    directions = directions - pole * (height * (1 - squash))[:, None]
    directions /= np.linalg.norm(directions, axis=1, keepdims=True)
    return directions, magnitudes


def asterisms():
    stars, figures = [], []
    for name, (dec, ra), points, lines in ASTERISMS:
        first = len(stars)
        for east, north, magnitude in points:
            # East offsets scale by the centre's declination, so stars at one east offset share a right ascension.
            stars.append((direction(dec + north, ra + east / max(np.cos(np.radians(dec)), 0.2)), magnitude))
        figures.append({"name": name, "stars": [first + i for i in range(len(points))], "lines": [[first + a, first + b] for a, b in lines]})
    return stars, figures


def check_sled_points_at_pole(stars, figures):
    """The Sled's two pointer stars (its first two) share a right ascension, so their line runs to the pole."""
    sled = figures[0]["stars"]
    a, b = stars[sled[0]][0], stars[sled[1]][0]
    assert abs(np.cross(a, b) @ np.array([0.0, 0.0, 1.0])) < 1e-9


def band(width=2048):
    """The Milky Way in the catalogue frame, 2:1 equirectangular, sRGB."""
    height = width // 2
    rng = np.random.default_rng(SEED + 1)
    u = (np.arange(width) + 0.5) / width
    v = (np.arange(height) + 0.5) / height
    # The Engine reads the band as a sky panorama in the catalogue's frame: u = atan2(z, x), v from +Y.
    around = 2 * np.pi * (u - 0.5)
    up = np.pi / 2 - np.pi * v
    d = np.stack([np.cos(up)[:, None] * np.cos(around)[None, :], np.repeat(np.sin(up)[:, None], width, 1), np.cos(up)[:, None] * np.sin(around)[None, :]], -1)
    pole = direction(*BAND_POLE)
    latitude = np.degrees(np.arcsin(np.clip(d @ pole, -1, 1)))
    # Longitude along the band, for the brighter core and the clumps.
    seed_axis = np.cross(pole, [0.0, 0.0, 1.0])
    seed_axis /= np.linalg.norm(seed_axis)
    across = np.cross(pole, seed_axis)
    longitude = np.arctan2(d @ across, d @ seed_axis)
    glow = np.exp(-((latitude / BAND_WIDTH_DEGREES) ** 2))
    core = 0.55 + 0.45 * np.exp(-(((longitude - 0.6 + np.pi) % (2 * np.pi) - np.pi) / 0.9) ** 2)
    # Clumps: a few octaves of smooth 3D value noise over the direction itself, so the texture's poles
    # (where the panorama layout pinches) show no seam or streaks.
    clumps = np.zeros_like(glow)
    for cells, weight in ((6, 0.5), (12, 0.3), (24, 0.2)):
        grid = rng.uniform(0, 1, (cells + 2, cells + 2, cells + 2))
        p = (d + 1) * 0.5 * cells
        i = np.floor(p).astype(int)
        f = p - i
        f = f * f * (3 - 2 * f)
        value = np.zeros_like(glow)
        for dx in (0, 1):
            for dy in (0, 1):
                for dz in (0, 1):
                    w = (f[..., 0] if dx else 1 - f[..., 0]) * (f[..., 1] if dy else 1 - f[..., 1]) * (f[..., 2] if dz else 1 - f[..., 2])
                    value += w * grid[i[..., 0] + dx, i[..., 1] + dy, i[..., 2] + dz]
        clumps += weight * value
    mottle = np.clip((clumps - 0.3) / 0.5, 0, 1) ** 1.5
    rift = 1 - 0.6 * np.exp(-(((latitude + 1.2) / 1.8) ** 2)) * np.clip((clumps - 0.35) / 0.3, 0, 1)
    light = glow * core * (0.2 + 0.8 * mottle) * rift
    tint = np.array([0.85, 0.88, 1.0])
    image = np.clip(light[..., None] * tint * BAND_BRIGHTNESS, 0, 1)
    srgb = np.where(image <= 0.0031308, image * 12.92, 1.055 * np.power(image, 1 / 2.4) - 0.055)
    return (srgb * 255 + 0.5).astype(np.uint8)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    args.directory.mkdir(parents=True, exist_ok=True)
    rng = np.random.default_rng(SEED)
    named, figures = asterisms()
    check_sled_points_at_pole(named, figures)
    directions, magnitudes = field(rng)
    all_directions = np.concatenate([np.array([[0.0, 0.0, 1.0]]), np.array([d for d, _ in named]), directions])
    all_magnitudes = np.concatenate([[POLE_STAR_MAGNITUDE], [m for _, m in named], magnitudes])
    colours = colour(rng, len(all_magnitudes))
    colours[0] = [1.0, 0.97, 0.9]
    for figure in figures:
        figure["stars"] = [i + 1 for i in figure["stars"]]
        figure["lines"] = [[a + 1, b + 1] for a, b in figure["lines"]]
    stars = [[round(float(x), 6) for x in d] + [round(float(m), 2)] + [round(float(c), 3) for c in col]
             for d, m, col in zip(all_directions, all_magnitudes, colours)]
    (args.directory / "sky.stars.json").write_text(json.dumps({"format": "rusty.stars.v1", "stars": stars}, separators=(",", ":")))
    asterisms_file = {
        "frame": "+Z toward the celestial pole, +X toward the meridian at the first midnight (WorldClock.StarOrientation)",
        "poleStar": 0,
        "asterisms": figures,
    }
    (args.directory / "asterisms.json").write_text(json.dumps(asterisms_file, indent=1))
    Image.fromarray(band(), "RGB").convert("RGBA").save(args.directory / "milky-way.png")
    counts = np.histogram(all_magnitudes, bins=np.arange(-1.5, 7.0, 1.0))[0]
    print("stars:", len(stars), "by magnitude from -1.5:", " ".join(str(c) for c in counts))


if __name__ == "__main__":
    main()
