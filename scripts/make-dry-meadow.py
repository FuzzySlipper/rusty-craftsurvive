"""Derive the dry meadow ground texture from the lush one (#10041): the same painted ground, sun-dried.

The terrain blends its ground layers by weight, so the dry meadow must tile and read at the same scale as
sage-ground; recolouring that texture keeps its brushwork, scale and seamless edges, and only moves its
greens toward straw and dry earth. Deterministic: run again to reproduce the checked-in image.

    python3 scripts/make-dry-meadow.py
"""
import colorsys
import hashlib
import json
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
FOLDER = ROOT / "content/game/textures/terrain-studies"
SOURCE, TARGET, NAME = FOLDER / "sage-ground.png", FOLDER / "dry-meadow.png", "dry-meadow"

# Greens move toward this hue (straw), keep this share of their saturation, and lift by this much.
STRAW_HUE = 0.115
HUE_PULL = 0.8
SATURATION_KEPT = 0.82
LIGHTEN = 1.14
# Already-brown soil patches keep their own hue.
GREEN_FROM, GREEN_TO = 0.16, 0.45


def dry(r, g, b):
    h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
    if GREEN_FROM <= h <= GREEN_TO:
        h = h + (STRAW_HUE - h) * HUE_PULL
        s *= SATURATION_KEPT
    v = min(1.0, v * LIGHTEN)
    return tuple(round(c * 255) for c in colorsys.hsv_to_rgb(h, s, v))


def main():
    image = Image.open(SOURCE).convert("RGBA")
    data = image.tobytes()
    pixels = bytearray(len(data))
    for i in range(0, len(data), 4):
        pixels[i:i + 3] = bytes(dry(data[i], data[i + 1], data[i + 2]))
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
    print(f"make-dry-meadow {TARGET.name}: {image.width}x{image.height} sha256 {digest}")


if __name__ == "__main__":
    main()
