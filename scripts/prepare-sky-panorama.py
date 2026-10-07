"""Prepare a generated sky image as CraftSurvive's equirectangular sky panorama (#9667).

Text-to-image panoramas are close to, but not exactly, equirectangular. This makes one usable:

1. find the horizon row (the strongest mean vertical change in the middle band, or --horizon as a
   fraction of the height) and remap rows piecewise so it lands exactly on the middle row;
2. make the 0/360 wrap seamless: the last --seam pixels cross-fade into the first ones and are
   dropped, then the image is resized back to its width;
3. calm the zenith: the top --zenith fraction of rows blends toward each row's mean colour, so the
   pole does not pinch the painted detail into a star;
4. write an RGBA PNG and print the horizon colour in linear light (the mean of the rows just above
   the horizon), which DayNightSky uses as the open fog colour.

    python3 scripts/prepare-sky-panorama.py SOURCE.png content/game/textures/sky-panorama.png --width 2048
"""
import argparse

import numpy as np
from PIL import Image

HORIZON_BAND = (0.3, 0.75)
FOG_ROWS = 0.012


def linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--width", type=int, default=2048)
    parser.add_argument("--horizon", type=float, default=None, help="horizon row as a fraction of the height")
    parser.add_argument("--seam", type=int, default=128, help="cross-fade width at the wrap, in source pixels")
    parser.add_argument("--zenith", type=float, default=0.08)
    args = parser.parse_args()

    image = np.asarray(Image.open(args.source).convert("RGB"), dtype=np.float64) / 255
    height, width, _ = image.shape
    if args.horizon is None:
        rows = image.mean(1).mean(1)
        change = np.abs(np.diff(rows))
        low, high = (int(height * f) for f in HORIZON_BAND)
        horizon = low + int(np.argmax(change[low:high]))
    else:
        horizon = int(args.horizon * height)

    # Rows above the horizon stretch into the top half, rows below into the bottom half.
    target = np.arange(height) + 0.5
    middle = height / 2
    source_rows = np.where(target < middle, target / middle * horizon, horizon + (target - middle) / middle * (height - horizon))
    source_rows = np.clip(source_rows.astype(int), 0, height - 1)
    image = image[source_rows]

    seam = args.seam
    blend = np.linspace(0, 1, seam)[None, :, None]
    head = image[:, :seam] * blend + image[:, width - seam:] * (1 - blend)
    image = np.concatenate([head, image[:, seam:width - seam]], 1)

    zenith_rows = int(height * args.zenith)
    for row in range(zenith_rows):
        weight = 1 - row / zenith_rows
        image[row] = image[row] * (1 - weight) + image[row].mean(0) * weight

    out = Image.fromarray((np.clip(image, 0, 1) * 255 + 0.5).astype(np.uint8), "RGB")
    out = out.resize((args.width, args.width // 2), Image.Resampling.LANCZOS).convert("RGBA")
    out.save(args.output)
    final = np.asarray(out.convert("RGB"), dtype=np.float64) / 255
    rows = max(1, int(final.shape[0] * FOG_ROWS))
    middle_row = final.shape[0] // 2
    fog = linear(final[middle_row - rows:middle_row]).reshape(-1, 3).mean(0)
    print(f"horizon source row {horizon}/{height}; horizon colour (linear) {fog[0]:.3f}, {fog[1]:.3f}, {fog[2]:.3f}")


if __name__ == "__main__":
    main()
