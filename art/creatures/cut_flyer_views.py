"""Cuts a flying creature's reference sheet into Tripo's four views (#10630).

usage: python3 -I art/creatures/cut_flyer_views.py <reference.png> <out dir> [--threshold 24]

Tripo is shown a body from its front, sides and back, and a bird's wing shape can't be seen from any
of them in level flight: edge-on, a wing is a line. So the bird is presented standing on its tail,
belly to the camera. The sheet has the left side across its top, beak left, and the views from below
and above beneath it, beak up (reference-spec.txt). Here the below view becomes the front, the above
view the back, and the side is turned so its beak is up and its belly faces the front's way. All are
set at one height, because the image model doesn't hold one scale. The body comes back standing on its
tail with its belly along +x, and `prepare_flyer.py` lays it level.
"""
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageOps

THRESHOLD = 24
CANVAS = 1024
# The bird's length on the canvas: the view from below is its widest, and must fit across.
HEIGHT = 700
# The side view lies in the sheet's top share; the two views below it split at its middle.
SIDE_SHARE = 0.32


def box(image, background, threshold):
    difference = ImageChops.difference(image, Image.new("RGB", image.size, background)).convert("L")
    return difference.point(lambda value: 255 if value > threshold else 0).getbbox()


def main():
    arguments = sys.argv[1:]
    threshold = int(arguments[arguments.index("--threshold") + 1]) if "--threshold" in arguments else THRESHOLD
    sheet = Image.open(arguments[0]).convert("RGB")
    out = Path(arguments[1])
    background = sheet.getpixel((4, 4))
    width, height = sheet.size
    split = int(height * SIDE_SHARE)
    regions = {
        "left": (0, 0, width, split),
        "front": (0, split, width // 2, height),
        "back": (width // 2, split, width, height),
    }
    views = {}
    for name, region in regions.items():
        part = sheet.crop(region)
        views[name] = part.crop(box(part, background, threshold))
    # Beak left and belly down, turned a quarter clockwise: beak up and belly left, as the front faces.
    views["left"] = views["left"].rotate(-90, expand=True)
    views["right"] = ImageOps.mirror(views["left"])
    for name, view in views.items():
        scale = HEIGHT / view.height
        scaled = view.resize((round(view.width * scale), HEIGHT), Image.LANCZOS)
        canvas = Image.new("RGB", (CANVAS, CANVAS), background)
        canvas.paste(scaled, ((CANVAS - scaled.width) // 2, (CANVAS - HEIGHT) // 2))
        canvas.save(out / f"{name}.png")
        print(f"{name}: {view.size} to {scaled.size}")


if __name__ == "__main__":
    main()
