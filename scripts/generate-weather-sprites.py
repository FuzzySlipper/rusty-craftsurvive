"""Draw the weather's particle sprites (#9740).

White on transparent, so each emitter's colour curve tints them:

- weather-rain.png: a thin, soft vertical streak (a falling drop seen moving);
- weather-flake.png: a soft round flake;
- weather-glint.png: a four-pointed glint, for the arcane glass storm;
- weather-grain.png: a small soft blob of blown sand or dust.

Writes content/game/textures/weather-*.png (RGBA, the Engine admits only RGBA PNGs).

    python3 scripts/generate-weather-sprites.py
"""
from pathlib import Path

import numpy as np
from PIL import Image

FOLDER = Path(__file__).resolve().parent.parent / "content" / "game" / "textures"
SIZE = 32


def save(name, alpha):
    rgba = np.zeros((SIZE, SIZE, 4), dtype=np.uint8)
    rgba[..., :3] = 255
    rgba[..., 3] = np.clip(alpha * 255, 0, 255).astype(np.uint8)
    Image.fromarray(rgba, "RGBA").save(FOLDER / name)


y, x = np.mgrid[0:SIZE, 0:SIZE].astype(float)
u = (x + 0.5) / SIZE * 2 - 1
v = (y + 0.5) / SIZE * 2 - 1
r = np.sqrt(u * u + v * v)

# A streak: narrow across, long down the sprite, brighter toward its leading (lower) end.
across = np.exp(-(u / 0.07) ** 2)
along = np.clip(1 - np.abs(v) / 0.95, 0, 1) * (0.55 + 0.45 * (v + 1) / 2)
save("weather-rain.png", across * along)

save("weather-flake.png", np.clip(1 - r / 0.55, 0, 1) ** 1.5)

rays = np.exp(-(u / 0.06) ** 2) * np.clip(1 - np.abs(v), 0, 1) + np.exp(-(v / 0.06) ** 2) * np.clip(1 - np.abs(u), 0, 1)
save("weather-glint.png", np.clip(rays * 0.8 + np.clip(1 - r / 0.25, 0, 1), 0, 1))

save("weather-grain.png", np.clip(1 - r / 0.8, 0, 1) ** 2 * 0.8)
