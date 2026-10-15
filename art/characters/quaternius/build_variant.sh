#!/bin/bash
# build_variant.sh <name> <recipe.json> <body-material> <paint-prompt-file> : one Quaternius character,
# assembled, repainted and given the shared painted face (#10029).
#
# Steps: assemble.py (head + parts + hair) -> export.py -> asset-pipeline view_paint.py render (four
# views) -> Qwen repaint (front first with the style reference, then the side and back views matched to
# it) -> paint_bake.py (per material) -> patch_images.py (painted textures, matte, the innkeeper's
# tone-matched head skin) -> face_slot.py build (capped head, front-projected face) -> order_slots.py
# (body slot 0, face slot 1) -> retarget.py (UAL1 idle/walk/talk clip pack).
# Every variant shares the base female head, so it shares the face box and the painted face set.
set -euo pipefail
NAME=$1 RECIPE=$2 BODY=$3 PROMPT=$4
ROOT=/home/agent/dev/rusty-craftsurvive
AP=/home/agent/dev/asset-pipeline
Q=${SCRATCH:-/tmp/quat-build}
EV=$ROOT/docs/evidence/quaternius-10029/paint
W=$EV/$NAME
C=$ROOT/content/game/characters/quaternius
QS=$ROOT/art/characters/quaternius
STYLE_REF=$EV/ref-innkeeper.png
mkdir -p $Q $W/views
cd $ROOT
blender -b --python $QS/assemble.py -- $RECIPE $Q/$NAME.blend > $Q/$NAME-assemble.log 2>&1
blender -b $Q/$NAME.blend --python $QS/export.py -- $Q/$NAME-raw.glb 1024 > $Q/$NAME-export.log 2>&1
(cd $AP && blender -b --factory-startup --python-exit-code 1 --python tools/texture-edit/view_paint.py -- \
  render $Q/$NAME-raw.glb $W/views --views 4 --size 2048 --elevation 8 > $Q/$NAME-views.log 2>&1)
P="$(cat $PROMPT)"
(cd $AP && python3 tools/texture-edit/qwen_edit.py --image $W/views/view_0.png --image $STYLE_REF \
  --prompt "$P Use the second image only as a style reference for the painting. Keep exactly the same silhouette, pose, position, size and garment boundaries as the first image; keep the black background; do not add or remove anything." \
  --out $W/views/edit_0.png --seed 7 --resolution 1536 | tail -1)
for i in 1 3; do
  (cd $AP && python3 tools/texture-edit/qwen_edit.py --image $W/views/view_$i.png --image $W/views/edit_0.png \
    --prompt "The first image shows the character from the SIDE. $P The second image is the same character already repainted from the front: match its colours, brushwork and soft contrast exactly. Keep exactly the same silhouette, pose, position and size as the first image; keep the black background; do not add or remove anything." \
    --out $W/views/edit_$i.png --seed 7 --resolution 1536 | tail -1)
done
(cd $AP && python3 tools/texture-edit/qwen_edit.py --image $W/views/view_2.png --image $W/views/edit_1.png \
  --prompt "The first image shows the character from BEHIND: we see her back, the back of her head and the backs of her legs. There is NO face in this view. $P The second image is the same character already repainted, seen from the side: match its colours, brushwork and soft contrast exactly. Keep exactly the same silhouette, pose, position and size as the first image; keep the black background; do not add or remove anything." \
  --out $W/views/edit_2.png --seed 11 --resolution 1536 | tail -1)
blender -b --factory-startup --python-exit-code 1 --python $QS/paint_bake.py -- $Q/$NAME-raw.glb $W/views $W/textures > $Q/$NAME-bake.log 2>&1
rm -f $W/textures/T_Superhero_Female_Dark_BaseColor.png
python3 -I $QS/patch_images.py $Q/$NAME-raw.glb $Q/$NAME-painted.glb $W/textures $EV/head-matched
blender -b --factory-startup --python-exit-code 1 --python $QS/face_slot.py -- build $Q/$NAME-painted.glb $Q/$NAME-face.glb \
  $C/innkeeper-face-neutral.png > $Q/$NAME-face.log 2>&1
python3 -I $QS/order_slots.py $Q/$NAME-face.glb $Q/$NAME-ordered.glb "$BODY" face
mkdir -p $Q/empty
python3 -I $QS/patch_images.py $Q/$NAME-ordered.glb $C/$NAME.glb $Q/empty
python3 -I $QS/retarget.py content/animations/UAL1_Standard.glb $C/$NAME.glb $C/$NAME-clips.glb
echo "BUILT $NAME"
