"""Tones down one hue band of a reduced paint, so an animal matches the ground it hides in (#10880, the hummock).

usage: python3 -I authoring/tools/creatures/tone_band.py <paint.png> <out.png> --hue LO HI --above S \
           [--saturation 0.8] [--value 0.68]

Every texel whose hue lies between LO and HI degrees and whose saturation is at least S has its saturation and value
scaled by the factors given; every other texel is left as it is. The hummock's velvet came out of Tripo a bright
ochre and orange, where the cushion barrens' own hummocks (content/game/textures/dungeon-overgrowth/cushion-*.png) are a
muted rust, khaki and dark olive; scaling the velvet's band brings it to their levels without touching its skin.
"""
import argparse
import colorsys

from PIL import Image


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("paint")
    parser.add_argument("out")
    parser.add_argument("--hue", type=float, nargs=2, required=True)
    parser.add_argument("--above", type=float, required=True)
    parser.add_argument("--saturation", type=float, default=0.8)
    parser.add_argument("--value", type=float, default=0.68)
    args = parser.parse_args()
    low, high = args.hue
    image = Image.open(args.paint).convert("RGBA")
    pixels = image.load()
    toned = 0
    cache = {}
    for y in range(image.height):
        for x in range(image.width):
            r, g, b, a = pixels[x, y]
            if (r, g, b) not in cache:
                h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
                if low <= h * 360 <= high and s >= args.above:
                    rgb = colorsys.hsv_to_rgb(h, min(1.0, s * args.saturation), min(1.0, v * args.value))
                    cache[(r, g, b)] = tuple(round(c * 255) for c in rgb)
                else:
                    cache[(r, g, b)] = None
            new = cache[(r, g, b)]
            if new:
                pixels[x, y] = (*new, a)
                toned += 1
    image.save(args.out)
    print(f"tone_band: {toned} of {image.width * image.height} texels toned "
          f"(hue {low:g}-{high:g}, saturation from {args.above:g})")


if __name__ == "__main__":
    main()
