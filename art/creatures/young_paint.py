"""Repaints an animal's texture for its young (#10600): the same charts and texels, recoloured.

usage: python3 art/creatures/young_paint.py <adult.png> <young.png> [--value 1.3] [--saturation 0.7] [--hue 0.02]

Every texel keeps its place, so the marks and square crunch of the adult's paint stay and the mesh
needs no second set of UVs. Value is lifted, saturation drawn down and the hue turned a little
toward yellow: young coats read paler and softer beside the adult at a distance.
"""
import colorsys
import sys

from PIL import Image


def option(argv, name, default):
    return float(argv[argv.index(name) + 1]) if name in argv else default


def main(argv):
    value = option(argv, "--value", 1.3)
    saturation = option(argv, "--saturation", 0.7)
    hue = option(argv, "--hue", 0.02)
    image = Image.open(argv[0]).convert("RGB")
    # The paint has few colours after pixel_reduce, so each is worked out once.
    table = {}
    pixels = image.load()
    for y in range(image.height):
        for x in range(image.width):
            colour = pixels[x, y]
            if colour not in table:
                h, s, v = colorsys.rgb_to_hsv(*(c / 255 for c in colour))
                r, g, b = colorsys.hsv_to_rgb((h + hue) % 1, s * saturation, min(1, v * value))
                table[colour] = (round(r * 255), round(g * 255), round(b * 255))
            pixels[x, y] = table[colour]
    # The Engine admits RGBA PNGs only.
    image.convert("RGBA").save(argv[1])
    print(f"{argv[1]}: {image.width}x{image.height}, {len(table)} colours")


if __name__ == "__main__":
    main(sys.argv[1:])
