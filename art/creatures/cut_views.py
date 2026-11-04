"""Cuts a creature's reference sheet into Tripo's four views, at one pixel scale (#10570).

usage: python3 -I art/creatures/cut_views.py <reference.png> <out dir> [--size 1024]

The sheet has the left side across its top, and the front and back below (reference-spec.txt). The
views are found by the gaps of background between them and set on square canvases of the sheet's
own background, unscaled, so all four agree on how big the animal is, with its feet on one line. A
long body leaves the front and back small, and that is correct. The right view is the left mirrored.
"""
import sys
from pathlib import Path

from PIL import Image, ImageChops, ImageOps

# How far a pixel may stray from the background's colour and still be background.
THRESHOLD = 24
# A gap between views is at least this many pixels across.
GAP = 12
# The feet stand this share of the way down each canvas.
FEET = 0.86


def runs(flags):
    """The (start, end) of each stretch of True at least GAP apart from the next."""
    found, start, last = [], None, None
    for i, on in enumerate(flags):
        if not on:
            continue
        if start is None:
            start = i
        elif i - last > GAP:
            found.append((start, last + 1))
            start = i
        last = i
    if start is not None:
        found.append((start, last + 1))
    return found


def main(argv):
    sheet = Image.open(argv[0]).convert("RGB")
    out = Path(argv[1])
    size = int(argv[argv.index("--size") + 1]) if "--size" in argv else 1024
    out.mkdir(parents=True, exist_ok=True)
    background = sheet.getpixel((4, 4))
    mask = ImageChops.difference(sheet, Image.new("RGB", sheet.size, background)).convert("L").point(lambda v: 255 if v > THRESHOLD else 0)
    width, height = sheet.size
    pixels = mask.load()
    rows = runs([any(pixels[x, y] for x in range(width)) for y in range(height)])
    if len(rows) != 2:
        raise SystemExit(f"expected two rows of views, found {rows}")

    def columns(top, bottom):
        return runs([any(pixels[x, y] for y in range(top, bottom)) for x in range(width)])

    upper, lower = columns(*rows[0]), columns(*rows[1])
    # Parts of one view can be split by a gap (a tusk tip): the outermost edges bound the left view.
    boxes = {"left": (upper[0][0], rows[0][0], upper[-1][1], rows[0][1])}
    if len(lower) != 2:
        raise SystemExit(f"expected a front and a back view, found {lower}")
    boxes["front"] = (lower[0][0], rows[1][0], lower[0][1], rows[1][1])
    boxes["back"] = (lower[1][0], rows[1][0], lower[1][1], rows[1][1])
    for name, box in boxes.items():
        view = sheet.crop(box)
        if view.width > size or view.height > size:
            raise SystemExit(f"{name} view {view.size} is larger than the {size} canvas")
        canvas = Image.new("RGB", (size, size), background)
        canvas.paste(view, ((size - view.width) // 2, int(size * FEET) - view.height))
        canvas.save(out / f"{name}.png")
        print(f"{name}: {view.size}")
        if name == "left":
            ImageOps.mirror(canvas).save(out / "right.png")


if __name__ == "__main__":
    main(sys.argv[1:])
