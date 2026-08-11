#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_dir="$repo_root/content/textures/source"
atlas="$repo_root/content/textures/terrain-atlas.png"
served="$repo_root/web/public/assets/terrain-atlas.png"
work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

for tile in grass-top grass-side dirt stone; do
  magick "$source_dir/${tile}-gpt.png" -resize 64x64! -filter box -alpha on "$work_dir/${tile}.png"
done

magick montage \
  "$work_dir/grass-top.png" "$work_dir/grass-side.png" \
  "$work_dir/dirt.png" "$work_dir/stone.png" \
  -tile 2x2 -geometry 64x64+0+0 -background none "$atlas"
mkdir -p "$(dirname "$served")"
cp "$atlas" "$served"
sha256sum "$atlas" "$served"
