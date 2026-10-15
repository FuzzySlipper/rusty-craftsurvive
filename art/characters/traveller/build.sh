#!/bin/bash
# Rebuilds the traveller style-test content (#10014) from the retained Tripo model.
# usage: art/characters/traveller/build.sh <tripo rig-model.glb> <work dir> <python with xatlas>
set -euo pipefail
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
TRIPO=$1 WORK=$2 XPY=$3
OUT=$ROOT/content/game/characters/traveller
cd "$ROOT"
for v in chunky stylised bold; do
  python3 -I art/characters/make_clip_pack.py content/animations/UAL1_Standard.glb "$OUT/ual-clips-$v.glb" "traveller-$v"
done
python3 -I art/characters/face_set.py art/characters/traveller/face/faces-01.png "$OUT" face 32 64 128
for v in chunky stylised bold; do
  python3 -I art/characters/fit_to_ual.py "$TRIPO" art/characters/traveller/$v.json "$WORK/fit-$v" > /dev/null
  "$XPY" art/characters/reuv.py "$WORK/fit-$v/payload.npz" "$WORK/fit-$v/tripo-basecolor.png" "$WORK/fit-$v/payload-reuv.npz" "$OUT/body-$v.png" --texels-per-metre 32 --double "$OUT/body-$v-64.png"
  python3 -I art/characters/assemble_character.py "$WORK/fit-$v/payload-reuv.npz" art/characters/traveller/$v.slots.json "$OUT/traveller-$v.glb"
  python3 -I art/characters/assemble_character.py "$WORK/fit-$v/payload-reuv.npz" art/characters/traveller/$v.slots.json "$WORK/traveller-$v-clips.glb" --with-clips
done
