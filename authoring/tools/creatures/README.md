# Creature sources

The editable source of the wildlife character workflow (Den #10568, campaign #10566). Design and
roster: the den-wiki page [Wildlife](https://wiki.dragonden.stream/wiki/wandering-years/wildlife).
Provenance is in [`content/creatures.sources.json`](../../../content/creatures.sources.json).

## Why the clips are generated

Humans borrow UAL's humanoid clips (`authoring/tools/characters/`). Nothing comparable exists for four legs,
and every animal's proportions differ. So each **family** in `families.py` (plantigrade: sloths and
bears; ungulate: horses, deer, goats, pigs; graviportal: mammoths; low-slung: glyptodons, beavers, the hummock and the seep-rat;
saurian: monitors, salamanders; bird and theropod: two legs, among them the stalk-bill and the ribbon-stalker; flyer: wings)
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
| `add_crest.py` (Blender) | Grows a crest of filaments with glowing beads onto a prepared body (#10878) |
| `keep_band.py` | Keeps one hue band of a master paint through pixel reduction, with its own palette (#10878) |
| `add_tongue.py` (Blender) | Grows a forked tongue inside a prepared body's mouth, on its own bone (#10879) |
| `split_dome.py` (Blender) | Cuts a domed back down the middle, lines the halves and builds ribbed plates under them (#10880) |
| `tone_band.py` | Scales the saturation and value of one hue band of a reduced paint, to match the ground an animal hides in (#10880) |

```sh
blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/build_creature.py -- \
    authoring/assets/creatures/sloth/species.json content/game/creatures/sloth/sloth.glb [--mesh body.glb --texture body-64.png] [--blend review.blend]
blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/preview.py -- \
    content/game/creatures/sloth/sloth.glb <frames dir> --frames 5
python3 -I authoring/tools/creatures/montage.py <frames dir> sheet.png
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

The look for animals is on the den-wiki [Wildlife](https://wiki.dragonden.stream/wiki/wandering-years/wildlife) page.
The route the sloth took:

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
- `preview.py --view <metres>` frames a small or a very large animal; `--top` adds a view from above (a lizard's
  side-to-side bend shows only there) and `--clips walk,bite` renders only those.

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

## Ragdolls

`python3 authoring/tools/creatures/ragdoll.py [species ...]` writes `<species>.ragdoll.json` beside the GLB, the
bones and links a killed animal falls with (#10621). Run it after every rebuild of a body. It needs
no Blender: one table of joint names serves every family, and sizes and masses come from the mesh, as
its header says. `mesh.ragdoll` in `kinds.json` turns it on for a kind. A new joint name has to go in
its tables, as a cone, a hinge or left to the clips. The `die` clip stays, for an animal that cannot
fall.

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

Keep foot planting budgeted to the nearest animals unless the Engine changes: planting every animal in a
crowd costs about ten times as much. The measurements are Den `technical/creature-animation-costs`.

## Birds in the air

The streamer (#10630) is the first, on the `flyer` family in `families.py`: a body, jointed wings and a
tail, with `glide` and `flap` clips and no legs. Tripo sees a body from its front, sides and back, and
level wings are a line from all of those, so a flyer is meshed standing on its tail:

```sh
python3 -I authoring/tools/creatures/cut_flyer_views.py authoring/assets/creatures/streamer/mesh/reference.png authoring/assets/creatures/streamer/mesh
blender -b --factory-startup --python-exit-code 1 --python authoring/tools/creatures/prepare_flyer.py -- <tripo.glb> <level.glb>
```

The sheet has the side across its top and the views from below and above beneath it (`reference-spec.txt`).
`cut_flyer_views.py` turns those into a belly-on front and a back, and `prepare_flyer.py` lays the body level for
`prepare_mesh.py --head +x`. In game a bird is a kind in `content/game/birds/birds.json`, not a wildlife
kind: `Modules/Birds` draws flights and keeps nothing. `craft.birds.overhead <kind>` sends one past the player.

The skein bird (#10631) is the second, on the same family: its `streamer` bones carry its trailing legs, and it
flies in the V under `flock.formation`. Its body was built from the sheet alone, which suits a bird that is
mostly a plan seen from far below and needs no generator:

```sh
python3 -I authoring/tools/creatures/sheet_flyer.py authoring/assets/creatures/skein/mesh/reference.png <level.glb>
```

The output goes straight to `prepare_mesh.py --head +x`, with no `prepare_flyer.py`. The image model draws the
near wing standing up in the side view however the spec asks for it to be edge-on, so paint that out first.

The line shag (#10642) is built the same way as the skein bird, with its `streamer` bones on the two halves of its tail.
Check the sheet's lower views: the image model drew the feet on the view from above, so they had to be swapped.

The glade parrot (#10639) has two bodies, because folded wings cannot open by skinning: `glade-parrot` on the
ground, on the `percher` family (the bird's bones with `idle`, `peck` and `hop` clips), and `glade-parrot-flying`, a
flyer built by `sheet_flyer.py` with its tail on the `streamer` bones. A bird swaps from one to the other as it goes
up. It is a party in `birds.json`, not a flight. Small birds take 300 texels/m, since at 64 one would be a few texels.

The ground body came from Tripo's website, whose credits are apart from the API's: multi-view in the Model panel,
then a separate 20-credit texture step, then Export as GLB. Its bird faces `-y`, so `prepare_mesh.py --head -y`.

## Hostile animals

The gaunt (#10615) is the first. It is an invented animal, so the faithful-proportions rule does not
bind it, and it goes through the same workflow with these differences:

- **A real mouth has to be on the sheet.** Tripo copies what it is shown, so `gaunt/mesh/reference.png`
  draws the jaws half open at rest, and the mesh arrives with separate jaws, modelled teeth and a red
  mouth. Its rest pose is that half-open mouth; clips open and shut from there.
- **`"mouth"` in a species file** parts the jaws by surface distance from a point on each, forward of
  `hingeForward` (`pin_mouth` in `build_creature.py`). Interlocking teeth defeat both heat weighting
  and a dividing line.
- **Hunter clips** in the ungulate family: `threat`, `bite` and `hurt`.
- **`restShare` under `clips.die`** sets how high the fallen body rests, as a share of the leg. Long
  legs leave a body in the air at the usual 0.5.

The axe-beak (#10622) is the second, and the first on two legs: the `bird` family in `families.py`.
Its legs are the ungulate's hind legs, under a three-bone neck, with `wing` stumps left to the clips,
and its gait swings the body over the foot that stands. A two-legged body has one place its feet stand,
so `hindMetres` equals `foreMetres` and the product does not pitch it. Its rest pose has the beak half
open, so its clips open and shut from there by less than the gaunt's.

The drift-runner (#10626) is the third: the `theropod` family, the bird's body and legs with two small
arms in place of wings and a six-bone tail. `pin_islands` in `build_creature.py` moves loose pieces such
as claws with the surface nearest them. `biteHeight` under `"mouth"` sets where upper and lower teeth
part, where the mean of the two points would not.

In game each is a kind in `content/game/wildlife/kinds.json` with `"pressed": "hunt"` and a `hunt` block.
Its capsule is its size class's, centred between its feet with the mesh drawn ahead of it, and
creatures are routed for the largest body hunting, on cells that fit it.
Wildlife seeds it, draws it and counts its losses; `CreatureModule` hunts with it (`HunterRules`).
`craft.creatures.hunter <kind> <east> <south>` places one.

### Cave hunters on the plantigrade rig (#10877)

The mouldback and the drop-sloth came through Tripo's web studio, whose GLBs face `-y`, so `prepare_mesh.py --head -y`.
Their reference sheets draw fine parts as solid masses (the mouldback's nose as thick nubs joined into one rosette,
the drop-sloth's coat as clumps hugging the limbs), and both came out with separate jaws and modelled teeth.

- **Hunter clips on the plantigrade family:** `threat` rears up on the hind legs with the forelimbs spread and the
  claws out (`pitchDegrees`, `armDegrees`, `spreadDegrees`); `bite` lunges and snaps, raking down with the right
  forelimb too under `"swipe": true`; `hurt`; and `lie`, the body sunk between feet that stay put, for a hunter
  that rests flat (the mouldback, as one more patch of fungus).
- **`hang` and `drop`** are for a hunter that rests hanging from a ceiling (the drop-sloth). `hang` turns the body
  over about the ground plane, so the feet's rest height becomes the grip at the origin, and the product draws the
  origin at the ceiling. `drop` turns it back over as the product lowers it to the floor. In `kinds.json`,
  `"hang": true` in a hunt's `rest` block turns this on: a ceiling between 2.8 m and 14 m over its floor is hung
  from, and the animal falls under gravity when it wakes (`HunterRules.Hang`).
- **Glow.** `emission_mask.py <body-64.png> <emission.png> [--hue lo hi]` keeps the texels of one hue band (the
  mouldback's blue-green glow caps) and blackens the rest. `build_creature.py --emission <emission.png>` exports that
  as the material's emissive texture at strength 2, so those parts show with no light on them, as the underground's
  light-has-a-source rule asks. The product's coat tint keeps a GLB's emission.
- **Reach while hanging.** A swing measures a hanging or falling hunter from the lowest of its body
  (`HunterRules.BodyOffFloor`: its lift less its height), and it lands no bite until it is down (R10877-2).
- **`aboveMetres` under `"mouth"`** keeps anything lower than that height out of the jaw parting. The drop-sloth's
  long forelimbs reach past its hinge, and without it the claw tips went with the jaw.

### Cave hunters on the ungulate rig (#10878)

The draft-hound and the glass-courser came through the Tripo web studio as the #10877 pair did.

- **Ear-fans: the `ungulate-eared` family.** The ungulate's bones with an `ear.L`/`ear.R` pair on the head (`ear` and
  `ear_tip` points). Every clip of the ungulate family gets an ear layer (`EAR_POSES` in `families.py`): held into the
  draft at rest and walking, swept back running, flattened back in `threat` and `bite`, drooping as it dies. The
  family's own clip is `search`: the fans flare wide and forward and each turns on its own while the head sweeps,
  nose up. Tripo made the fans thin, separate sheets from a sheet that draws them that way.
- **`"ears"` in a species file** (`pin_ears` in `build_creature.py`) pins every vertex out past `sideMetres`, above
  `aboveMetres` and between the `forward` pair to its side's ear bone, and gives those faces a second material,
  `membrane`: alpha blended at `membrane.alpha` (0.8 by default) and drawn from both sides. The Engine imports a GLB's
  alphaMode and doubleSided; it has no transmission or back-lighting term, so a fan lets the cave show through it
  but does not glow when the lantern is behind it.
- **`keep_band.py <master> <reduced> <out> --hue lo hi`** repaints one hue band of the master into the reduced paint
  with its own small palette. `pixel_reduce.py` spends its palette on what covers most of a body, so the hound's pink,
  red-veined fans came out tan without it.
- **Crests: `add_crest.py`.** Tripo fuses thin filaments into the body, so the courser's reference sheet is drawn
  without its crest and the crest is grown onto the prepared body from the species' `"crest"` block: three-sided
  shafts rooted on the back's midline, swept back and splayed, each with a bead. It adds a three-row strip to the
  paint for the shafts' and beads' colours (the body's UVs move up to keep their texels); `emission_mask.py --hue`
  on the bead's amber then makes the beads glow. The pieces move with the body surface nearest them (`pin_islands`).
- **New ungulate clips.** `lie` keys the legs folded (`hind` and `fore` give each segment's pitch from hanging straight
  down, top to toe; `fold_leg` turns each bone from its own rest direction): the courser lies with its hind legs
  under it and its forelegs out in front. `threat` with `"headLow": true` crouches, head low and jaws wide, instead
  of rearing.
- **`preview.py`** shows the base colour's image: Workbench draws a material's active image, which for a body with an
  emissive texture was the black emission map.
- **In game.** The hound searches with its `search` clip (`"search": { "clip": "search" }`, the `Search` clip role);
  the courser rests on `lie`. Near the player, the hunters from one home ring it, each at striking distance in its
  own place (`HunterRules.Ring`: a pack turns by its flank, others by 35 degrees), rather than piling onto one spot.
- **Resting into the draft (R10878-1).** The veil fans stand square to the cave's draft, so a cave's sites carry the
  draft beside them (`CaveSites.Drafts`, upwind from each fan) and a home of a kind whose `rest` block has
  `"draft": { "downwindShare": 0.3 }` takes the upwind heading there (`HerdRecord.Upwind`). The pack rests facing it,
  and wakes to a player coming down the draft at its whole wake range, falling to that share of it from behind
  (`HunterRules.WakeShare`), so the way past is from downwind. A pack-mate still lying at rest calls no alarm; one
  that wakes calls them all. The creature readout shows `upwind=`, `faces=` and `wake=`.
- **`craft.creatures.unseen <seconds>`** is a test aid for captures: no creature sees the player for that long, so
  hunters lose the player where they stand and search there, by the player's own light (0 ends it).

### Cave hunters on the saurian rig (#10879)

The salt-monitor and the drowner came through the Tripo web studio as the #10877 pair did. Both reference sheets were
redrawn until the front view stopped smiling: Tripo builds the face from it, and the first draws of both grinned. The
drowner's gill plumes and flank fringe are drawn as solid fronds and one wavy skirt, which Tripo kept.

- **The `saurian` family.** The low-slung legs (two segments and a foot, sprawled) with a second spine bone and a tail
  as long as the species places points for (`tail1` to `tailN`; the monitor has six, the drowner eight), so its bones
  depend on the species (`Family.sized`). Its walk is a sprawling diagonal gait: the trunk bends as a standing wave
  (`bend`, the girdles turning opposite ways so each reaching limb goes forward), the head swings against it and a wave
  runs down the tail. Clips:
  - `threat` raises the body high on the legs, bent side-on, jaws wide and tail curled;
  - `bite` draws the head back and to one side and sweeps it across as the jaws shut;
  - `lie` sinks the belly to the ground between sprawled feet;
  - `swim` (the drowner) lays the legs back along the body and sinks it, a slow wave holding it in place;
  - `lunge` (the drowner) throws the whole body forward a metre or more, forelegs off the ground, jaws gaping. The
    drowner's kind plays it as its bite.
- **Tongue: `add_tongue.py`.** A monitor flicks a forked tongue, which Tripo cannot make, so it is grown from the
  species' `"tongue"` block: a flat strip from `root` inside the closed mouth, `length` forward, forking for its last
  `forkMetres`. It adds a two-row strip of its `colour` to the paint. With `tongue` and `tongue_tip` in the points the
  family has a `tongue` bone on the jaw; `pin_tongue` in build_creature.py gives it every loose piece along that line,
  and `idle`, `alert`, `walk` and `lie` slide it out `tongue.reachMetres` and back (`flick`).
- **Paint.** `pixel_reduce.py`'s `--luminance` scales the whole atlas, padding included, to a mean: the salt-monitor's
  salt-white took 0.72 at saturation 0.7, and the drowner's mostly red atlas 0.4 at 0.75 (0.62 bleached its skin white).
- **In game.** The drowner's home is the middle of its pool, over a bed a few tenths of a metre under the surface, so
  it rests there on `swim` (the `Swim` clip role), sunk to its back and head.

### Cave hunters that hide or swarm (#10880)

The hummock and the seep-rat came through the Tripo web studio as the #10879 pair did, both on the `low-slung` family.
The hummock's sheet draws its dome closed, with one seam down the middle, and hardens its face to a snapping turtle's
hooked beak. The seep-rat's draws its mouth open on four incisors, since Tripo copies the sheet literally.

- **The low-slung family's additions.**
  - Its bones now depend on the species (`Family.sized`): the tail is as long as the species draws it.
  - A species that places `dome_hinge` and `dome_hinge_tip` has a dome cut in two, each half on its own bone (`dome.L`,
    `dome.R`) hinged along its side and parented to the spine.
  - Clips:
    - `threat` braces the body, lowers or raises the head and gapes; a split dome heaves open by `domeDegrees` and pulses;
    - `bite` draws back and lunges with the jaws wide; with `rearDegrees` (the hummock's charge and crush) the front
      rears off its forefeet and slams down;
    - `hurt` is a flinch;
    - `lie` is belly-down between sprawled feet (the seep-rat);
    - `hide` (the hummock) sinks the body until the dome's rim meets the ground, feet into it, head drawn in, the dome
      breathing. Its feet leave the ground, so it does not plant them; the kind maps its `lie` role to `hide`.
- **The split back: `split_dome.py`.** From the species' `"dome"` block:
  - **The cut.** It cuts the dome's top along the midline between the block's `forward` ends (faces above
    `cutAboveMetres`) and opens the cut, nudging each lip a millimetre to its own side.
  - **The lining.** It copies the dome's faces near the midline, insets them toward the long axis (`axisMetres`) and
    turns them inward, so an opened half shows flesh from inside. The generated body's vertex normals were not all
    usable: insetting along them left whole rows of the lining on the outer surface, where it showed through.
  - **The plates.** It builds the ribbed fins, following the dome's own curve just under it and widest where the
    cut is, with a floor beneath them.
  - **The weights.** `pin_dome` in build_creature.py gives each half its dome and lining by a weight that is whole in
    the cut's middle and fades toward its ends and down to the hinge, so the opening is a lens. The plates (told
    apart by their strip texels) stay on the body.
- **Paint.** The hummock's velvet came out a bright ochre and orange, which stood out among the cushion barrens' muted
  rust and khaki hummocks. `tone_band.py --hue 15 60 --above 0.62 --saturation 0.8 --value 0.68` brought it to their
  levels. The seep-rat's skin folds reduced to scattered dark texels until `pixel_reduce.py --line-contrast 0.22`.
- **In game.**
  - The hummock rests hidden at its home among the barrens' cushions and displays its split back before it charges.
  - A seep-rat colony is one band of four to six at 0.9 m spacing. The kind's `pack` block wakes all of them together.

### Two-legged cave hunters (#10881)

The ribbon-stalker (theropod) and the stalk-bill (bird) came through the Tripo web studio as the #10880 pair did.

- **The sheets.** The ribbon-stalker took three draws: the first grinned from the front and held its tail level, and the
  second drew an upright crocodile-man on human legs. The third spec names the forward lean and the bird's leg (short
  thigh, long bare tarsus), and asks for seven large solid flaps, so Tripo kept them as solid strips. The stalk-bill's
  spec asks for its bill parted and the house style's crisp texels, not sheet 21's soft paint.
- **New bird-family clips** (the theropod inherits `stand`):
  - `stand` is the hidden rest: neck in, one slow turn of the head a clip, the body swaying a degree, a theropod's
    arms folded and tail hanging still;
  - `stalk` (the stalk-bill) is the walk at its own slow pace with the neck low, its `speed` in the manifest as walk's
    and run's are;
  - `spear` (the stalk-bill) coils the neck into an S, then tips the body forward at the spine over planted feet and
    swings the neck out of its S with the head aimed down ahead, bill wide, and snaps it shut. Tipping it at the root
    instead lifted the feet off the ground.
- **In game.** Both kinds map their `lie` role to `stand`. The stalk-bill plays `stalk` as its walk and `spear` as its
  bite.

