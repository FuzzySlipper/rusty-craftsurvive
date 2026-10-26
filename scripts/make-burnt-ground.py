"""Derive the burnt ground texture from the lush one (#10296): the same painted ground, charred by a grass fire.

The terrain blends its ground layers by weight, so burnt ground must tile and read at the same scale as sage-ground;
recolouring that texture keeps its brushwork, scale and seamless edges. Its colours fall to dark, brown-grey char,
keeping a share of their light so the brushwork still shows, and broad drifts of pale ash lie over it (a noise that
wraps at the tile's edges, so the texture stays seamless). Deterministic: run again to reproduce the checked-in image.

    python3 scripts/make-burnt-ground.py
"""
import colorsys
import hashlib
import json
import random
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
FOLDER = ROOT / "content/game/textures/terrain-studies"
SOURCE, TARGET, NAME = FOLDER / "sage-ground.png", FOLDER / "burnt-ground.png", "burnt-ground"

# Char: every colour takes this hue (a warm brown-black) and saturation, its value falls to FLOOR plus a share of
# what it was, so the painted strokes stay legible in the dark.
CHAR_HUE, CHAR_SATURATION = 0.07, 0.28
FLOOR, KEPT = 0.1, 0.38
# Ash: drifts where a wrapping noise (LATTICE cells across the tile) passes ASH_FROM, lifted toward ASH grey in two
# crisp steps (ASH_THIN, then ASH_THICK past ASH_DEEP), so they read as painted shapes like the strokes under them.
ASH = (0.47, 0.45, 0.42)
ASH_FROM, ASH_DEEP = 0.7, 0.8
ASH_THIN, ASH_THICK = 0.1, 0.22
LATTICE = 9
SEED = 10296


def wrapping_noise(size, lattice, seed):
    """Smooth value noise on a lattice that wraps at the tile's edges."""
    rng = random.Random(seed)
    grid = [[rng.random() for _ in range(lattice)] for _ in range(lattice)]
    cell = size / lattice
    values = []
    for y in range(size):
        gy, fy = divmod(y / cell, 1)
        gy = int(gy)
        fy = fy * fy * (3 - 2 * fy)
        row = []
        for x in range(size):
            gx, fx = divmod(x / cell, 1)
            gx = int(gx)
            fx = fx * fx * (3 - 2 * fx)
            a = grid[gy % lattice][gx % lattice] * (1 - fx) + grid[gy % lattice][(gx + 1) % lattice] * fx
            b = grid[(gy + 1) % lattice][gx % lattice] * (1 - fx) + grid[(gy + 1) % lattice][(gx + 1) % lattice] * fx
            row.append(a * (1 - fy) + b * fy)
        values.append(row)
    return values


def char(r, g, b, ash):
    _, _, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    rgb = colorsys.hsv_to_rgb(CHAR_HUE, CHAR_SATURATION, FLOOR + KEPT * v)
    rgb = [c + (a - c) * ash for c, a in zip(rgb, ASH)]
    return tuple(round(min(1.0, c) * 255) for c in rgb)


def main():
    image = Image.open(SOURCE).convert("RGBA")
    if image.width != image.height:
        raise SystemExit("sage-ground must be square to wrap the ash noise")
    noise = wrapping_noise(image.width, LATTICE, SEED)
    data = image.tobytes()
    pixels = bytearray(len(data))
    for i in range(0, len(data), 4):
        x, y = (i // 4) % image.width, (i // 4) // image.width
        n = noise[y][x]
        ash = ASH_THICK if n > ASH_DEEP else ASH_THIN if n > ASH_FROM else 0.0
        pixels[i:i + 3] = bytes(char(data[i], data[i + 1], data[i + 2], ash))
        pixels[i + 3] = data[i + 3]
    out = Image.frombytes("RGBA", image.size, bytes(pixels))
    out.save(TARGET)
    digest = hashlib.sha256(TARGET.read_bytes()).hexdigest()
    manifest_path = FOLDER / "materials.json"
    manifest = json.loads(manifest_path.read_text())
    sage = next(entry for entry in manifest if entry["id"] == "sage-ground")
    entry = {"id": NAME, "width": image.width, "height": image.height, "sha256": digest,
             "metresPerTile": sage["metresPerTile"], "triplanarSharpness": sage["triplanarSharpness"]}
    manifest = [e for e in manifest if e["id"] != NAME] + [entry]
    manifest.sort(key=lambda e: e["id"])
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    print(f"make-burnt-ground {TARGET.name}: {image.width}x{image.height} sha256 {digest}")


if __name__ == "__main__":
    main()
