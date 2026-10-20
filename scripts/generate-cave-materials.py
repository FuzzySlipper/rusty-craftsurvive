"""Draw the cave's ground materials (#10095).

Underground is not one material called rock: each of the cave's materials gets its own map, and the
Engine blends them where they meet on the smooth rock (CaveMaterials, TerrainPresentation). The maps
are calm and near flat, one material's colour with only a soft, broad variation and at most one
quiet feature (stains, flowstone banding, silt ripples, fracture lines), since the cave shader adds
the strata, the 3D pixels and the grain over all of them alike (props look preference: a calm
surface with few strong details, not busy wrapped tiles).

Alpha carries how dry a material is (255 dry, lower wetter); the cave shader reads it as gloss and a
little darkening.

Writes content/game/textures/cave-materials/NAME.png (RGBA, the Engine admits only RGBA PNGs),
materials.json (with each map's hash, metresPerTile and triplanar sharpness) and blending.json, as
TerrainGroundMaterials reads a ground texture set. The names' order is CaveMaterials.Names.

    python3 scripts/generate-cave-materials.py
"""

import hashlib
import json
import math
import random
import zlib
from pathlib import Path

from PIL import Image

OUT = Path(__file__).resolve().parent.parent / "content/game/textures/cave-materials"
SIZE = 128

# name: (sRGB colour, broad variation, dryness 0..1, metres per tile, feature)
MATERIALS = {
    "limestone": ((112, 104, 92), 0.10, 1.00, 6, None),
    "washed": ((104, 112, 116), 0.06, 0.45, 5, None),
    "patinated": ((92, 74, 56), 0.16, 1.00, 4, "stain"),
    "mud": ((62, 49, 37), 0.08, 0.25, 4, None),
    "silt": ((122, 108, 84), 0.07, 0.85, 5, "ripple"),
    "rubble": ((128, 124, 116), 0.12, 1.00, 3, "fracture"),
    "flowstone": ((196, 182, 158), 0.08, 0.70, 3, "band"),
    "turf": ((70, 84, 44), 0.14, 0.90, 4, None),
}


def value_noise(seed, cells):
    """A tiling value noise over the map, `cells` lattice cells across."""
    rnd = random.Random(seed)
    lattice = [[rnd.random() for _ in range(cells)] for _ in range(cells)]

    def at(x, y):
        fx, fy = x * cells / SIZE, y * cells / SIZE
        x0, y0 = int(fx) % cells, int(fy) % cells
        x1, y1 = (x0 + 1) % cells, (y0 + 1) % cells
        tx, ty = fx - int(fx), fy - int(fy)
        tx, ty = tx * tx * (3 - 2 * tx), ty * ty * (3 - 2 * ty)
        top = lattice[y0][x0] + (lattice[y0][x1] - lattice[y0][x0]) * tx
        bottom = lattice[y1][x0] + (lattice[y1][x1] - lattice[y1][x0]) * tx
        return top + (bottom - top) * ty

    return at


def draw(name, rgb, variation, dryness, feature):
    seed = zlib.crc32(name.encode())
    broad, finer = value_noise(seed, 4), value_noise(seed + 1, 8)
    rnd = random.Random(seed)
    cracks = [(rnd.uniform(0, SIZE), rnd.uniform(0, SIZE), rnd.uniform(0, math.pi)) for _ in range(3)]
    image = Image.new("RGBA", (SIZE, SIZE))
    pixels = image.load()
    for y in range(SIZE):
        for x in range(SIZE):
            v = 1 + variation * ((broad(x, y) - 0.5) * 1.4 + (finer(x, y) - 0.5) * 0.6)
            if feature == "stain":
                v *= 1 - 0.25 * max(0.0, finer(x // 2, y) - 0.6) / 0.4
            elif feature == "band":
                v *= 1 + 0.07 * math.sin(2 * math.pi * (y / SIZE * 6 + 0.3 * broad(x, y)))
            elif feature == "ripple":
                v *= 1 + 0.05 * math.sin(2 * math.pi * (x / SIZE * 5 + 0.5 * broad(x, y)))
            elif feature == "fracture":
                for cx, cy, angle in cracks:
                    d = abs((x - cx) * math.sin(angle) - (y - cy) * math.cos(angle))
                    if min(d, SIZE - d) < 0.8:
                        v *= 0.7
            colour = tuple(max(0, min(255, int(channel * v))) for channel in rgb)
            pixels[x, y] = colour + (int(255 * dryness),)
    return image


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    manifest = []
    for name, (rgb, variation, dryness, metres, feature) in MATERIALS.items():
        path = OUT / f"{name}.png"
        draw(name, rgb, variation, dryness, feature).save(path)
        manifest.append({
            "id": name,
            "width": SIZE,
            "height": SIZE,
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "metresPerTile": metres,
            "triplanarSharpness": 4,
        })
    (OUT / "materials.json").write_text(json.dumps(manifest, indent=2) + "\n")
    blending = {"transitionCells": 2, "weightContrast": 2, "textureScale": 1, "projectionSharpnessScale": 1}
    (OUT / "blending.json").write_text(json.dumps(blending, indent=2) + "\n")


if __name__ == "__main__":
    main()
