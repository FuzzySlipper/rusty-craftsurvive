#!/usr/bin/env bash
# Copies the ignored authoring/assets/ to or from its store on the den NAS: the backup of the raw and working asset
# files git does not hold (concepts, reference sheets, generated sources, downloaded packs), and the way a new
# checkout gets them. It keeps no versions. Their text (prompts, specs, provenance, READMEs) is in git as well.
# MapLab's output (authoring/assets/maplab-out/) is regenerable and not copied.
#
#   authoring/tools/assets-sync.sh push            # this checkout's assets to the store (adds and updates, deletes nothing)
#   authoring/tools/assets-sync.sh pull            # the store's assets into this checkout (adds and updates, deletes nothing)
#   authoring/tools/assets-sync.sh push --mirror   # also delete what the destination has and the source lacks
#   CRAFT_ASSET_STORE=/path authoring/tools/assets-sync.sh pull
set -euo pipefail
root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
assets=$root/authoring/assets
store=${CRAFT_ASSET_STORE:-/mnt/den-nas-backup/data/rusty-craftsurvive/assets}

direction=${1:-}
mirror=()
[[ ${2:-} == --mirror ]] && mirror=(--delete)
case $direction in
  push) from=$assets; to=$store ;;
  pull) from=$store; to=$assets ;;
  *) echo "usage: authoring/tools/assets-sync.sh push|pull [--mirror]" >&2; exit 2 ;;
esac
[[ -d $from ]] || { echo "assets-sync: $from does not exist" >&2; exit 1; }
mkdir -p "$to"
rsync -rt --info=stats1 --exclude=/maplab-out/ --exclude=__pycache__/ "${mirror[@]}" "$from/" "$to/"
