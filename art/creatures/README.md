# Creature sources

The editable source of the wildlife character workflow (Den #10568, campaign #10566). Design and
roster: the atlas page [Wildlife & the Old Beasts](https://github.com/FuzzySlipper/docs/blob/main/wandering-year/pages/wildlife.md).
Provenance is in [`content/creatures.sources.json`](../../content/creatures.sources.json).

## Why the clips are generated

Humans borrow UAL's humanoid clips (`art/characters/`). Nothing comparable exists for four legs,
and every animal's proportions differ. So each **family** in `families.py` (plantigrade: sloths and
bears; ungulate: horses, deer, goats, pigs; graviportal: mammoths; low-slung: glyptodons, beavers)
has a skeleton and a clip generator in code. A species bakes the family clips it has settings for:

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
| `families.py` (Blender) | Each family's skeleton, legs and clips |
| `gait.py` | The gait math, plain Python; `python3 -I gait.py` self-checks it |
| `cut_views.py` | Cuts a reference sheet into Tripo's four views at one scale |
| `fit.py` (Blender) | Shows a species' joints through its prepared body, for fitting them |
| `drop_limbs.py` (Blender) | Cuts limbs a generated body should not have out of it and closes the holes |
| `preview.py` (Blender) | Renders an exported GLB's clips: still frames, or every frame with `--sequence` |
| `montage.py` | Lays `preview.py` stills out as one sheet |
| `sequence.py` | Animates `--sequence` frames: smooth beside a stepped rate, as one GIF |

```sh
blender -b --factory-startup --python-exit-code 1 --python art/creatures/build_creature.py -- \
    art/creatures/sloth/species.json content/game/creatures/sloth/sloth.glb [--mesh body.glb --texture body-64.png] [--blend review.blend]
blender -b --factory-startup --python-exit-code 1 --python art/creatures/preview.py -- \
    content/game/creatures/sloth/sloth.glb <frames dir> --frames 5
python3 -I art/creatures/montage.py <frames dir> sheet.png
```

- **Body.** Without `--mesh`, the body is a stand-in built from metaball capsules along the
  bones (`proxy` in the species file), with a palette texture: enough to prove a new family's rig
  and clips before any art exists. A real mesh comes in with `--mesh` (and `--texture` for its
  reduced paint), already in the species' rest pose and facing +Z. Either way it is skinned by
  automatic weights. Vertices that heat weighting misses, such as loose fur flaps, take their
  nearest bone. The build stops if more than 5% are missed, because then the rig doesn't fit the
  body.
- **IK poles.** Each leg's pole angle is chosen by search, so the rest pose stays put (the build
  prints the residual error, under 1 cm for the sloth).
- **Engine rules met by the export.** Clip ids are lowercase. A `rest` clip is always present.
  Samplers are nearest for both filters (rusty-engine #10011).
- **When heat weighting fails outright.** Loose pieces no bone can see, such as a horse's mane
  flaps, stop the whole solve. The build then solves on a watertight voxel copy, keeping only its
  largest island, and carries the weights to the body's own vertices.
- **Manifest.** It lists each clip's seconds, whether it loops, and whether it keeps all feet on
  the ground (`plantsFeet`), plus the legs as IK chains and where the hind and fore feet stand.
  The product reads it (`CreatureStyleTest`).

## A real body: the sloth (#10568)

Patch's look for animals (2026-11-03, atlas wildlife page): faithful proportions, smooth clips,
and a painted coat drawn as 3D pixel art. The route the sloth took:

1. **Reference sheet** (`sloth/mesh/reference.png`, its spec beside it), a GPT image of the left
   side, front and back on one sheet:
   - in concept A's shape and concept C's paint;
   - in a neutral stance on all fours, under flat light.
   It is cut into four views at one pixel scale; the right view is the left mirrored.
2. **Shape:** Tripo P2 multiview (`../asset-pipeline/tools/tripo/tripo.py generate --views ...`).
3. **`prepare_mesh.py`** (Blender, with the xatlas package built for its Python 3.13). It:
   - welds the generator's split vertices;
   - faces the body forward;
   - scales it to length and stands it on the ground;
   - decimates it to about 6,000 triangles;
   - charts it with xatlas at 128 texels/m;
   - bakes the generated paint into `master.png`.

   Blender's own projection cut the shaggy body into thousands of islands a few texels across.
4. **`pixel_reduce.py`** (`../asset-pipeline/legacy/experiments/stele-place-study/`): `--factor 2`
   gives 64 texels/m, the density animals use (Patch, 2026-11-03; props stay at 32, `--factor 4`). The run used `--colors 14 --luminance 0.44
   --saturation 0.85`: the Engine's tone mapping turned the master's own levels a deep red-brown
   and hid the fawn face.
5. **Fit the species points to the body.** Measure slices along it (back and belly heights, leg
   columns, feet, tail), then check an overlay of the joints on side, front and top views.
6. **`build_creature.py --mesh body.glb --texture body-64.png`.**

The Tripo output, the prepared body and the master are kept at
`/data/evidence/rusty-craftsurvive/sloth-10568/`.

## What a body can get wrong

Count the feet before fitting: a reference sheet that draws near and far legs apart in its side view
gives Tripo a body with twice the legs (the glyptodon had eight). `drop_limbs.py` cuts the extras out.

A species file can also carry what a body needs:

- `"shell": { "bone", "aboveMetres", "forward", "legs" }` holds all but the legs below the rim to one
  bone, so a carapace does not flex;
- limb points named `"elbow.R"` and so on give a body standing with staggered legs its own right limb,
  in left-side metres;
- `preview.py --view <metres>` frames a small or a very large animal.

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

## The next families

What the sloth proved and what each new family needs (#10570).

- **Carries over unchanged:**
  - the gait math and its IK bake (`gait.py`, `Controls`, `bake_clips`);
  - the manifest;
  - `prepare_mesh.py` and the `pixel_reduce` step at 64 texels/m;
  - weld-then-skin with the nearest-bone fallback;
  - the style test and its foot IK.
- **Written per family:**
  - **A bone list.** Today `plantigrade_bones` is the only one.
  - **Its `LEGS` table.**
  - **Clips for its own behaviour hooks.**
- **Ungulate** (horse, deer, cave goat, entelodont): legs have a third long segment (the cannon)
  and stand on hooves. The Engine has two-bone IK only, so the shin and cannon take the IK and the
  thigh is keyed from the stride; the product's foot IK moves the same two segments.
- **Graviportal** (mammoth): columnar two-bone legs, as here. It adds a trunk chain of five or
  six bones, keyed directly like the tail, plus ears.
- **Low-slung** (glyptodon, giant beaver): short, slightly sprawled legs. The glyptodon's shell is
  one rigid bone over the spine, and its hunker clip draws the legs and head in under it. The
  beaver's flat tail is a short chain.
- **Reference sheets:**
  - **Pose:** keep the neutral stance on all fours, with all four legs separate.
  - **Scale:** cut every view at one pixel scale. A long body leaves the front and back views
    small, and that is correct.
  - **Order:** Tripo's front view is the face; its left view is the head-left profile.

Measured on the sloth in the live lane (RX 9070 XT, headless, pair ddbedd6365d6):
- **Driving cost:** about 0.2 ms per animal per update to play, place and plant (nine sloths: mean
  1.8 ms, worst 5.4 ms). Turning foot planting off barely changes it, so the cost is in playback
  and the two ground casts per placement.
- **Drawing cost:** within the stream's ±3 ms noise for one sloth, and roughly 2–3 ms for nine.

Herds (#10569) play clips through direct playback rather than a graph, so one-shot clips hold
and rates follow ground speed. To give a kind a body in game, add a `mesh` block to its entry in
`content/game/wildlife/kinds.json`, naming the build, its manifest and the clip for each role.
A role left out falls back to `idle`. The tiers, the planting budget and the pitch limit are in
that file's `animation` section.

A kind's young (#10600) need no second build. A `young` block in `kinds.json` scales the adult's
mesh, shares every clip, and names a paint made by `young_paint.py` from the adult's 64 texels/m
texture: the same charts, recoloured. The head joint is scaled up per animal, as young have
larger heads for their size.

Animals differ a little by themselves (#10601): `variety` in `kinds.json` sets how far size, coat
brightness and warmth stray either way. The coat is a tint over the shared paint, through the
appearance's material factors, so it costs no texture and each animal keeps its look when its herd
wakes again. If a kind needs real pattern differences, the next step is a coat shader reading that
same tint as data, as `kit-stone.wgsl` does; product shaders on skinned meshes are untested.

Measured with planting budgeted to the nearest eight: about 2.5 ms an update for 24 sloths
awake near the player. Planting every animal in a crowd cost about ten times that and tripled
render time, so keep the budget unless the Engine changes.

