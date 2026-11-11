"""Animates preview.py --sequence frames: smooth playback beside a stepped rate, as one GIF (#10568).

usage: python3 -I authoring/tools/creatures/sequence.py <frames dir> <clip> <out.gif> [--fps 30] [--steps 12] [--loops 2]

The left half plays every frame; the right half holds each pose for whole steps of 1/steps
seconds, as the game's `craft.creature.step` does. An art study, not in-game evidence.
"""
import math
import sys
from pathlib import Path

from PIL import Image, ImageDraw


def main(argv):
    frames_dir, clip, out = Path(argv[0]), argv[1], Path(argv[2])
    option = lambda name, default: int(argv[argv.index(name) + 1]) if name in argv else default
    fps, steps, loops = option("--fps", 30), option("--steps", 12), option("--loops", 2)
    paths = sorted(frames_dir.glob(f"{clip}-seq-*.png"))
    if not paths:
        raise SystemExit(f"no {clip}-seq-*.png frames in {frames_dir}")
    images = [Image.open(p).convert("RGB") for p in paths]
    # A looping clip's last frame repeats its first; drop it so the loop doesn't stutter.
    if len(images) > 1 and images[-1].tobytes() == images[0].tobytes():
        images = images[:-1]
    width, height = images[0].size
    out_frames = []
    for loop in range(loops):
        for i in range(len(images)):
            stepped = min(len(images) - 1, round(math.floor(i / fps * steps) / steps * fps))
            frame = Image.new("RGB", (width * 2, height))
            frame.paste(images[i], (0, 0))
            frame.paste(images[stepped], (width, 0))
            draw = ImageDraw.Draw(frame)
            draw.text((8, 8), f"{clip}: smooth {fps} fps", fill="black")
            draw.text((width + 8, 8), f"{clip}: stepped {steps} fps", fill="black")
            out_frames.append(frame)
    out_frames[0].save(out, save_all=True, append_images=out_frames[1:], duration=round(1000 / fps), loop=0)
    print(f"wrote {out}: {len(out_frames)} frames")


if __name__ == "__main__":
    main(sys.argv[1:])
