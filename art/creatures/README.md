# Creature sources

The editable source of the wildlife character workflow (Den #10568, campaign #10566). Design and
roster: the atlas page [Wildlife & the Old Beasts](https://github.com/FuzzySlipper/docs/blob/main/wandering-year/pages/wildlife.md).
Provenance is in [`content/creatures.sources.json`](../../content/creatures.sources.json).

## Why the clips are generated

Humans borrow UAL's humanoid clips (`art/characters/`). Nothing comparable exists for four legs,
and every animal's proportions differ. So each **family** (plantigrade: sloths and bears; later
ungulate, graviportal, low-slung) has a skeleton and a clip generator in code:

- A species file (`<kind>/species.json`) places the family's joints as metres
  (`[side, forward, up]`, left side mirrored) and gives its gait figures: stride, duty factor,
  footfall phases, foot lift, body bob and sway.
- Legs follow foot trajectories (`gait.py`) through Blender IK; the spine, neck, head, jaw and
  tail are keyed directly. Each clip is baked to plain joint keys before export.
- Clips are in place. A gait's `speed` in the manifest is the ground speed at which its planted
  feet don't slide, so the product plays it at `speed / clip speed`.
- Each species' GLB carries its own clips, so there is no shared clip pack (and rusty-engine
  #10018 doesn't apply).

## Pipeline

| Script | Does |
| --- | --- |
| `build_creature.py` (Blender) | Rig, body, skin, clips and export: `<out>.glb` and `<out>.manifest.json` |
| `gait.py` | The gait math, plain Python; `python3 -I gait.py` self-checks it |
| `preview.py` (Blender) | Renders an exported GLB's clips: still frames, or every frame with `--sequence` |
| `montage.py` | Lays `preview.py` stills out as one sheet |
| `sequence.py` | Animates `--sequence` frames: smooth beside a stepped rate, as one GIF |

```sh
blender -b --factory-startup --python-exit-code 1 --python art/creatures/build_creature.py -- \
    art/creatures/sloth/species.json content/game/creatures/sloth/sloth-proxy.glb [--mesh body.glb] [--blend review.blend]
blender -b --factory-startup --python-exit-code 1 --python art/creatures/preview.py -- \
    content/game/creatures/sloth/sloth-proxy.glb <frames dir> --frames 5
python3 -I art/creatures/montage.py <frames dir> sheet.png
```

- **Body.** Without `--mesh`, the body is a stand-in built from metaball capsules along the
  bones (`proxy` in the species file), with a palette texture. A real mesh comes in with `--mesh`,
  already in the species' rest pose and facing +Z. Either way it is skinned by automatic weights;
  the build stops if any vertex is left unweighted.
- **IK poles.** Each leg's pole angle is chosen by search, so the rest pose stays put (the build
  prints the residual error, under 1 cm for the sloth).
- **Engine rules met by the export.** Clip ids are lowercase. A `rest` clip is always present.
  Samplers are nearest for both filters (rusty-engine #10011).
- **Manifest.** It lists each clip's seconds, whether it loops, and whether it keeps all feet on
  the ground (`plantsFeet`), plus the legs as IK chains and where the hind and fore feet stand.
  The product reads it (`CreatureStyleTest`).

## In game

`content/game/creatures/style-kinds.json` lists the kinds the creature style test places. The
`craft.creature.*` debug commands drive it:

| Command | Does |
| --- | --- |
| `near` | Places a kind near the player |
| `clip` | Plays a clip; walk and run circle at their own speed |
| `driver` | Picks the driver: graph (a trigger per clip) or direct (sampled) |
| `step` | Advances the pose in whole steps at a given frames-per-second |
| `plant` | Turns two-bone foot IK on uneven ground on or off |
| `read`, `clips`, `clear` | Report the placed creatures, list a kind's clips, remove them all |

## Concepts

`sloth/concept/` holds the three GPT-image turnaround options (A faithful, B storybook bulk,
C storybook with 3D-pixel-art paint) with their exact specs. They are concept images, not Engine
captures.
