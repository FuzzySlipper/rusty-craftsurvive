"""Lays preview.py's frames out as one review sheet: a row per clip and view (#10568).

usage: python3 -I authoring/tools/creatures/montage.py <frames dir> <out.png>
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

from PIL import Image, ImageDraw

LABEL_WIDTH = 150


def main(frames_dir, out_path):
    rows = defaultdict(dict)
    for path in Path(frames_dir).glob("*.png"):
        match = re.fullmatch(r"(.+)-(side|quarter)-(\d+)", path.stem)
        if match:
            rows[(match[1], match[2])][int(match[3])] = path
    keys = sorted(rows)
    sample = Image.open(next(iter(rows[keys[0]].values())))
    width, height = sample.size
    columns = max(len(r) for r in rows.values())
    sheet = Image.new("RGB", (LABEL_WIDTH + columns * width, len(keys) * height), "white")
    draw = ImageDraw.Draw(sheet)
    for row, key in enumerate(keys):
        draw.text((8, row * height + height // 2), f"{key[0]}\n{key[1]}", fill="black")
        for column, path in sorted(rows[key].items()):
            sheet.paste(Image.open(path).convert("RGB"), (LABEL_WIDTH + column * width, row * height))
    sheet.save(out_path)
    print(f"wrote {out_path}: {len(keys)} rows x {columns}")


if __name__ == "__main__":
    main(*sys.argv[1:3])
