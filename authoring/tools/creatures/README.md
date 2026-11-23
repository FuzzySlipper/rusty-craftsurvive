# Creature sources

The editable source of the wildlife character workflow (Den #10568, campaign #10566). Design and
roster: the den-wiki page [Wildlife](https://wiki.dragonden.stream/wiki/wandering-years/wildlife).
Provenance is in [`content/creatures.sources.json`](../../../content/creatures.sources.json).

## Why the clips are generated

Humans borrow UAL's humanoid clips (`authoring/tools/characters/`). Nothing comparable exists for four legs,
and every animal's proportions differ. So each **family** in `families.py` (plantigrade: sloths and
bears; ungulate: horses, deer, goats, pigs; graviportal: mammoths; low-slung: glyptodons, beavers, the hummock and the seep-rat;
saurian: monitors, salamanders, the plume-neck; bird and theropod: two legs, among them the stalk-bill and the ribbon-stalker; flyer: wings; serpent: no legs, the root-adder)
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
| `preview.py` (Blender) | Renders an exported GLB's clips: still frames, or every frame with `--sequence`; `--top` and `--front` add views from above and head on |
| `montage.py` | Lays `preview.py` stills out as one sheet |
| `sequence.py` | Animates `--sequence` frames: smooth beside a stepped rate, as one GIF |
| `add_crest.py` (Blender) | Grows a crest of filaments with glowing beads onto a prepared body (#10878) |
| `keep_band.py` | Keeps one hue band of a master paint through pixel reduction, with its own palette (#10878) |
| `add_tongue.py` (Blender) | Grows a forked tongue inside a prepared body's mouth, on its own bone (#10879) |
| `open_mouth.py` (Blender) | Cuts a body's closed mouth open along its bite line and builds the roof, floor and teeth inside (#10882) |
| `mirror_paint.py` (Blender) | Repaints a master's right side from its left, where Tripo painted the two sides differently (#10882) |
| `add_ruff.py` (Blender) | Grows a ring of large frills round a prepared body's neck, each on its own bone, painted from the body's own mane (#10882) |
| `split_dome.py` (Blender) | Cuts a domed back down the middle, lines the halves and builds ribbed plates under them (#10880) |
| `tone_band.py` | Scales the saturation and value of one hue band of a reduced paint, to match the ground an animal hides in (#10880) |
| `margin_paint.py` | Paints a ribbon body's back and belly margins into its master by the way each texel faces, keeping the head's teeth and marks (#10985) |

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

### The plume-neck (#10882)

An upright lizard that runs on all fours, on the `saurian` family rather than a theropod with arms that turn into
forelegs: its anatomy is a lizard's on four legs, and standing is a clip.

- **The sheet.** It draws the animal on all fours with a lizard's bent legs (sheet 26's standing legs looked human) and
  its mane closed. It took three draws: the first spread the ruff half open in the front and back views, and the second
  lost the long neck. The third's front view smiled, so that mouth alone was edited to a scowl.
- **The saurian family's additions.**
  - A species that places `neck2` gets a neck that bends in two.
  - `upright` rears the body up about the hind feet, the forelegs folded and the tail down along the ground. It stands
    still, breathing and turning its head once a clip; it does not plant its forefeet.
  - A threat's or bite's `ruffDegrees` flares the ruff (`flare_ruff`), with a ripple round it in the threat.
- **The ruff: `add_ruff.py`.** Tripo's mane lies along the neck and cannot open. Splitting it down the dorsal line and
  rolling the halves out (as `split_dome.py` does for the hummock) showed only a torn mane. So the ruff is grown from the
  species' `"ruff"` block instead: `count` solid serrated leaves in a ring round the neck behind the head, each lying
  back along the neck and splayed a little out. The script writes each frill's points into the species (`ruff1` to
  `ruffN`), so the family gives each its own bone on the head.
  - `flare_ruff` swings each frill out along its own radius, its splay away from the ruff's mean line, so the ring opens
    into a fan round the head.
  - The frills' faces map onto the largest square of the body's paint that is all mane crimson, so they carry its texels.
  - `pin_ruff` in build_creature.py gives each loose frill to its nearest ruff bone.
- **The mouth: `open_mouth.py`.** Where a sheet draws the jaws barely parted, Tripo closes them, and the mouth is a
  painted line: dropping the jaw stretched that surface into a smear (Patch, 2026-11-20: it looked like a drawn-open
  texture). The tool cuts the head along the species' `"mouthCut"` bite line (from `hinge` to `tip`, joined only at
  the corners), closes each jaw's edge with a fan into a dark roof or floor, and stands small pointed teeth along both
  edges, each wholly on its own jaw's side of the cut. Run it before `add_ruff.py`.
  - **The lips are parted a millimetre** (`LIP_PART`). A glTF export welds vertices that share a place and a texel,
    and the first cut's two lips lay on the same line, so the export joined the jaws together again: the jaw dropped
    and stretched the face as before (Patch saw it still closed).
  - **`pin_cut_mouth`** gives each vertex forward of the hinge to the side of the cut plane it lies on, rather than
    searching the surface: the GLB splits the surface at every UV seam, so a jaw arrives as many loose pieces.
- **`pin_mouth` seeds from both sides.** For a mouth not cut open, it used to seed each jaw from the one vertex nearest
  the species' `upper` and `lower` points, whichever side that lay on, so the other side's cheek could reach the jaw
  first through the mouth's corner. Each jaw is now seeded on both sides.
- **The paint: `mirror_paint.py`.** Tripo painted the head's right side with a smeared brow and mouth. Every texel of
  a face right of the midline is now repainted from the mirrored point on the left, on the master before
  `pixel_reduce.py`.

### The root-adder: a legless family (#10883)

- **The sheet** lays the snake out dead straight, so one chain runs through it; its jaws are parted on its fangs.
- **The body.** Tripo made it a metre wide over seven, too fat to coil. `prepare_mesh.py --girth 0.5 --depth 0.62`
  slims it across and down, its length kept, before it is charted. Tripo also closed the jaws, so `open_mouth.py`
  cuts them open. Its fangs hang outside the lower lip, below any level bite line, so the mouth block's
  `upperLuminanceAbove` keeps faces painted that bright (the fangs) uncut and on the head.
- **The `serpent` family.**
  - Bones: one chain of body segments from the tail tip forward (`body1` on `s0..s1` to `bodyN`), then the neck, head
    and jaw. No legs, so `CreatureFeet` plants nothing; the product pitches it between `s2` and the head.
  - Its root lies inside the body, so it does not deform (`Family.root_deforms`): skinned, it held the middle still.
  - `serpent_lay` turns each segment; with `centre` it turns the root so the head points forward and moves it so the
    flat shape is centred on the origin, where the capsule is.
  - Clips:
    - `lie` coils it flat in a spiral (`loops`), tail innermost;
    - `threat` lifts the last `riseSegments` into an S with the neck curving back to level and the jaws wide;
    - `bite` draws the S back and shoots it out and down;
    - `walk`/`run` run a wave of turns down the body (lateral undulation, in place);
    - `hang` lies belly up along the ceiling (the origin, as for the drop-sloth) in loose loops, the front bending
      down over `bendSegments` with the bend over the origin;
    - `drop` turns it right way up as the product lowers it and lands it coiled;
    - `idle`, `alert`, `hurt` and `die`, which goes limp.
- **Ragdoll.** `body` segments are cones. A body without a chest takes the blow on its middle segment; its ragdoll
  starts from the tail tip, which a fallen snake's record follows.


### Ambient cave creatures (#10884)

The blindfish, drift-grazer and mound-shrew are furtive ambience (Patch: no colliders and no combat). They get bodies
but no ragdoll.
- **Families.**
  - The blindfish (5 segments) and the drift-grazer (4) are on the `serpent` family: its `walk`/`run` wave reads as
    a fish's swim and as a sea pig's slow roll.
  - The mound-shrew is on the `low-slung` family.
  - Species strides are set so each gait's rate matches the ambient speeds in `kinds.json` (walk at `walkSpeed`,
    run at `fleeSpeed`).
  - The mound-shrew's mesh was fitted 0.5 m long; `kinds.json` draws it at `scale` 0.806, the design's 0.4 m. Its
    clips play about 1.24x faster to keep its feet on the ground. `tests/Wildlife` checks each small creature's
    drawn length (mesh bounds x scale) against its `lengthMetres`.
- **Small bodies.** At 0.3-0.5 m, heat weighting fails on the mesh as it is: Blender's heat solver loses a body under a
  few metres. So `build_creature.py`'s `auto_weights` scales the rig and mesh up to `HEAT_SMALLEST_METRES` for the
  weighting, then sets them back.
- **Triangles.** 1,200-1,400 triangles each. At 700 the fish looked faceted at lantern range.
- **The product's side.** A `caveAmbient` kind with a `mesh` block (the same `file`/`manifest`/`scale`/`clips` as a
  hunter's, with `idle`, `walk` and `run` required) is drawn on its own body by `WildlifeMeshBodies.PoseAmbient`:
  - the clip comes from its speed: running at 0.6 x `fleeSpeed`, walking above 3 cm/s;
  - no shadow;
  - it pauses when held by distance, as hunters do.
  - A kind without one keeps the placeholder.


### The main ruin inhabitant: the strider family and worn shells (#10956)

The collector is the ruins' common foe (den-wiki [Ruins wildlife](https://wiki.dragonden.stream/wiki/wandering-years/ruins-wildlife)).
Its body came through the Tripo web studio from `collector/mesh/reference.png`: the bare body from the settled shell sheet, in side,
front and back views with the arms and legs held apart.
- **The `strider` family.** The bird's legs (thigh, shin, cannon, toes; two-bone IK on the lower pair) under a spine with
  no tail, two long arms that hang to the ground (`upperarm`, `forearm`, `hand`, `claw`) and a short neck to a hanging
  head. The skitterer, the implement-carrier and the blade-limbed special build on it (#10957, #10958).
  - Clips: `rest`, `idle`, `walk` and `run` (the arms swing against the legs, `armSwingDegrees`), `alert`, `crouch`
    (its rest, sunk low with its hands down), `threat` (low and wide: knees deep, head thrust out, both arms reaching,
    the blade arm raised; Patch: it never stands up like a person), `bite` (the blade stroke: it coils, lunges and sweeps
    across), `hurt` and `die`.
  - The spine flexes little, so a rigid shell on it does not part from the body.
- **Worn pieces.** Shells and the blade are not skinned. Each is a rigid prop mesh in the frame of the joint it hangs
  from, so one body wears any of the four looks, as equipment (Patch, 2026-11-21).
  - `make_shells.py <species.json> <built.glb> <out dir>` reads the species' `shells` block and the built GLB's bind
    matrices, and writes each piece as `<name>.prop-mesh.json` and `<name>.png`, with `pieces.json` listing each look's
    pieces and the held ones by joint.
  - A shell with `mesh` is a generated one, stretched to its `box` and raised until the body's trunk clears its top
    (`clearance`); one without is a cap drawn in code (`centre`, `half`, `rim`, `top`, `fullness`).
  - Species metres to glTF swaps two axes, which mirrors the space, so the writer turns every triangle over.
  - `paint_from_views.py <body.glb> <views dir> <master.png>` paints a prepared mesh from its sheet's views by the way
    each surface faces. The shells needed it: Tripo's texture step stalled on the first and never finished, so the shells
    were generated untextured (30 credits each) and painted from their own sheets
    (`collector/shells/<name>-reference.png`).
  - Reduce shell paint with `--luminance 0.67`: the reducer scales to a mean over the whole image, and a shell's charts
    leave most of it empty, so the body's 0.45 turned them grey-brown.
- **In game.** `mesh.worn` in `kinds.json` names the pieces file and how often each look turns up. `WildlifeWornPieces`
  opens each piece once; each body's pieces are children of its object, hung from their joints in one `PublishChanges`
  with the body just before the first snapshot that draws it, and again after any snapshot that left it out (the map
  lets go of the bindings, R10956-1), under ids of their own (`ProductIds.WildlifeWornBase`, three a slot). The Engine
  carries them with the pose and the ragdoll; the product follows nothing. `craft.creature.look <look|any>` makes bodies
  from then on wear one look, for captures.
- **Skin.** Tripo painted the tissue a deep wine red that read punchy in game; Patch asked for paler and fleshier. Its
  plum-red band (hue 300-15 degrees) is warmed 12 degrees, halved in saturation and raised 1.4x in value after reduction.
- **Engine rules met.** Piece textures are RGBA PNGs; a point no triangle faces (a ring closing at a crown) takes the
  normal of the points at the same place, as the Engine refuses a mesh it cannot normalise.


### The skitterer and the implement-carrier (#10957)

Both are on the `strider` family, from Tripo web-studio bodies made from their picked sheets' side views
(`<kind>/mesh/reference.png`, hands empty). Their shells are their bodies' own, so their `shells` block holds only what they hold.
- **Threat and strike styles.** `threat.style` picks the pose: the collector's `wide` (the default), the skitterer's
  `duck` (the hood pitched down like a shield, the knife arm drawn back, the free hand forward) or the carrier's `thrust`
  (the implement held out at arm's length, the free arm back). `bite.style: thrust` is a straight punch of the held
  arm, low by `lowDegrees` (the skitterer's knife), in place of the collector's `slash`.
- **The crescent.** `make_shells.py` draws a held `"shape": "crescent"`: a ring open on one side and its core as a
  separate `glow` part, which `WildlifeWornPieces` gives an emissive material. `light` puts a point light at the core,
  parented to the piece's object so the Engine carries it with the hand; it burns `threatShare` times brighter while
  the bearer threatens or strikes. At most four held lights burn at once, on bodies drawn in full
  (`WildlifeMeshBodies.Light`).
- **Size.** The carrier is the `large` size class (a 2.4 m capsule). The karst ruin it was tested in has a breach too
  low for that capsule, so it could not route in; its fight and rest were captured on the surface.


### The blade-limbed special (#10958)

`reaper` (the wiki's blade-limbed special) is on `strider-bladed`: the strider's bones with a pair of blade limbs rising
from the spine (`bladeupper`, `bladefore`, `blade`; points `blade_root`, `blade_elbow`, `blade_wrist`, `blade_tip`). Every
strider clip gets a blade layer (`bladed` in `families.py`): held over the back and swaying, folded down in its crouch,
swept up and out in the threat (`bladeRaiseDegrees`, `bladeSpreadDegrees`), raised back then brought down together
through the target in the blow (`bladeStrikeDegrees`), loose as it dies. Its arms keep the strider's threat and thrust.
- **`pins` in a species file** hold a region to one bone: vertices within an `up` band of heights and a `radius` of a
  plan `centre`, less `except` circles mirrored to both sides. Heat weighting gave the top of its dome to the blade
  limbs rising from it, so a blade's swing tore the dome; the dome now follows the spine and only the roots move.
- **Ragdoll:** `bladeupper` is a cone, `bladefore` a hinge, and the scythe is left to the clips.
- It is the `large` size class, and it fits the karst ruin's pillared hall that the carrier could not reach.


### Small ruin skitterers on the low-slung family (#10959)

The threshold lurker, the shard dweller and the wall runner each got a wildlife sheet by the settled recipe (Patch saw
them first), then a mesh sheet and a Tripo body, all on `low-slung`.
- **Rearing threat.** `threat.rearDegrees` rears the front off its forefeet about the hind feet, the forelimbs raised by
  `armDegrees`/`forearmDegrees` and spread by `armSpreadDegrees`; `tailCurlDegrees` curls the tail up over the back
  (the wall runner). The lurker's threat is its burst out of the floor; its rest is the hummock's `hide`, sunk until only
  its back and shield show.
- **The lurker's ambush, staged live (R10959-1).** Spawn it resting on a doorway's threshold, stand the player a
  few metres off on the far side, and walk through on held input (`scripts/live.mjs walk --steps 6 --minimum 0`,
  re-aimed each hold): at its `wakeMetres` (4 m) it goes from its sunk `hide` rest to its rearing threat on the
  threshold, then pursues. The karst ruin's breach (`craft.dungeon.visit breach`) is an arched doorway open 7 m to
  its south. Placing it on doorways in generated ruins is #10955.
- **The shard dweller's shard** is held rigid on the spine by a species `pins` region. Its Tripo paint has no amber, so it
  has no emission.
- **The wall runner runs on floors only.** The Engine's character movement and navigation are planar and upright;
  running on walls and ceilings is asked upstream as rusty-engine #10967.
- **Mesh sheets that fought back:** the lurker's first sheet gave it human hands and stood it up, and the shard
  dweller's lost its long arms; naming what the limbs end in ("digging blades ... NOT hands", "arms twice as long as the
  legs") and passing the creature's full sheet as a third reference fixed both.

### Three-legged ruin creatures: the tripod family (#10960)

The channel sifter and the stilt runner (kind `stilt-pack`) are on `tripod`, a new family. Each got a wildlife sheet
by the settled recipe, which Patch saw first, then a mesh sheet and a Tripo body.
- **Three legs.** Two front legs are mirrored and a back leg runs on the middle line, placed by `.C` points. Each leg
  is the bird's leg: thigh, shin, cannon and toe. `Family.leg_sides` names the legs a family has (default `("L", "R")`;
  the tripod has `("L", "R", "C")`). The leg controls, the manifest's legs and foot placement all follow it, and a
  `.C` foot sits on the middle line.
- **The whip arm and the fan.** A species that places `whip0` to `whipN` hangs a whip of N segments under its chest
  (the stilt runner). `whip()` curls it up over the body in the threat and lashes it out ahead in the strike. A
  species that places `fan` and `fan_tip` gets a spreading fan on its head (the sifter's combs).
- **Rest.** The rest is `feed`: the head and fan dip and sift, with the body sunk a little. The sifter sifts the
  channel's water; the stilt runners peck at the relic lights.
- **The sifter's spores** are WildlifeBreath puffs in `kinds.json` (amber, released from the chest during the
  threat). They are a look only: the rules of the mark they leave are still being settled on the crew board.
- **The sifter's amber** was lost in the colour reduction, even with `--accent-colors`. The master's amber texels were
  copied back over `body-64.png` before `emission_mask.py` (recorded in `content/creatures.sources.json`).
- **A pack's lethality comes mostly from its size.** A landed blow does `power` plus a tenth of the roll, about 6 even
  at power 1. Five stilt runners kill an idle, unarmoured 30-health player in about 4 s, even at accuracy 14 and a
  2 s cooldown. That is still gentler than a seep-rat colony of the same size. Tune a pack by `groupMaximum` and
  `cooldownTicks`; `power` barely changes it.

### Many-limbed ruin tenders: the brood bearer and the mender (#10961)

Each got a wildlife sheet by the settled recipe, which Patch saw first, then a mesh sheet and a Tripo body. Their gameplay
was settled with Patch on the wiki's Ruins wildlife page ("Tenders' indirect threats"). Neither strikes a blow of its own.
- **The many-limbed family.** The family hangs up to three leg pairs (`hip1` to `hip3`, front to back) from whichever of
  the hips, spine and chest is nearest. It can also carry up to three pairs of handling arms (`shoulder1` to
  `shoulder3`: upper arm, forearm, pad) under the chest. `many-limbed` is the bearer's: six legs, no arms.
  `many-limbed-armed` is the mender's: legs 1 and 3 and all its arms.
  - Its stems are numbered, so `MANY_LIMB_BONES` and `MANY_TIP_BONES` name each for the stand-in body.
  - `ragdoll.py` knows `arm` (a cone) and `pad` (unsimulated).
- **Its clips.**
  - **Rest (`feed`):** the arms press forward in turn. This is the mender's smoothing, and its sealing.
  - **Threat:** the bearer braces low; the mender rears with its arms spread.
  - **Death:** the feet stay planted while the body sinks between them (`dropShare` of a leg's reach). A death that
    splayed the legs about the hip lifted the feet off the floor, and a deep sink crumpled the bearer's flank.
- **The bearer's mesh sheet** asked for its six cups empty, because the pods are worn pieces. The model generator still
  drew a leg on the middle line in the front and back views, which was painted out before Tripo. Its body also has four
  shallow openings along its flanks, which read as already-split pods.
- **Its pods** are one worn piece hung from the spine. `make_shells.py` makes it from the species' `shells` block:
  - `shape` `pods` is six glowing pods, in its `glow` part, sitting in the cups;
  - `husks` is the same cups split, with petals curling back from each cup's rim over a lens of glow;
  - the cup centres were read off a top-down heightmap of the prepared mesh.
  The kind wears `sealed`. The product's rules switch the member's `WornLook` to `split`, and the mesh body re-dresses
  under the same piece ids.
- **The seal** is `content/game/creatures/mender/seal.prop-mesh.json`, made by `make_seal.py`: a bowed sheet 2 by 3 m,
  drawn from both sides. The product stands it in the doorway and grows it up from the floor.

### The chime grazer: a folding crest (#10962)

The chime grazer's body came through the Tripo web studio from `chime-grazer/mesh/reference.png`. That reference is the
settled sheet's side view with the crest asked away, so the neck runs from the shoulder hump straight down to the
scoop head.
- **`ungulate-crested`.** The ungulate rig plus one `crest` bone on the chest.
  - The family's `rigid_bones` keep the crest out of the skin, both in heat weighting and in the nearest-bone pass, so
    the hump does not bend when the crest moves. `ragdoll.py` leaves it to the clips.
- **The crest folds like a wing** (Patch, 2026-11-22). It is a rigid held piece on the `crest` bone. The clips turn
  the bone between `clips.crest.foldDegrees` (lying back along the plates; a positive pitch lays it towards the tail)
  and `raiseDegrees` (upright). `alertLift` half raises it.
  - The ring is the `threat` clip: the crest unfolds over `ring.riseShare`, rings upright with a quick tremor while
    the head comes up, and folds back down from `ring.foldShare`. The clip ends folded, so the walk that follows
    starts from the crest's rest.
- **The crest piece: `"shape": "silhouette"`** in `make_shells.py`.
  - The crest sheet's front and back views disagreed with its side view, so the piece is cut from the side view alone
    (`crest/crest-side.png`). Its silhouette becomes a grid of `cell` metres, swelling to `thickness` within `round` of
    the edge and closing to a knife edge there.
  - Both faces are painted from the view, box-reduced to 64 texels/m after the backdrop takes the nearest inside
    colour. The view's jade texels form a `glow` part a hair proud of each face.
  - Each face keeps its own edge points, since shared ones would have no normal. Cells one wide are trimmed, and
    convex outline corners stay put while the outline is eased, as both would fold flat.

### Fog foes: a body faded into the mist (#10984)

Fog foes are seen only when they attack. At rest their bodies are faded into the mist, and they turn solid head first as
they strike (den-wiki `wandering-years/fog-wildlife`, "Roster and rules"). The fade is product-side and needs no
Engine work:

- `fade_paint.py <body.glb> <fade.png>` writes the kind's paint with the fade in its alpha.
  - Each texel's place along the body comes from the skin: the joints its triangles are weighted to, ranked from the head
    (1) to the tail (0) along `--chain`. The default chain is the serpent family's: head, neck, body14…body1, root. So a
    coiled rest pose reads the same as a straight one.
  - Its alpha is the cutoff plus that rank. It stays just over the cutoff at the tail (`TAIL_MARGIN`, matched by
    `WildlifeFade.TailMargin`), so a whole body draws whole.
  - Noise of ±`--crumble` per texel makes the edge crumble in texels.
- The kind's mesh gets a `fade` block: `{ "paint", "cutoff", "rest", "sweepSeconds" }`.
  - `WildlifeMeshBodies` binds the paint as a masked material at the cutoff.
  - Each body's base-colour alpha (`MeshMaterialFactors`) shows a share of it from the head back
    (`WildlifeFade.AlphaFor`). Its alpha is sent only when it changes.
  - A body shows `rest` of its length, and all of it while its clip is threat, bite, hurt or die. It sweeps between
    the two over `sweepSeconds`.
- **Test aid:** `craft.creature.fade <share>|auto` forces every faded body's share. The body readout gives `fade=` and
  `alpha=`.
- **Look:** the Engine resolves a mask's edge by coverage (alpha-to-coverage, sharpened by `fwidth`). The crumble
  noise therefore leaves the edge partly covered, blending with what lies behind it: dark ground darkens it, and mist
  shows through it. A paint with `--crumble 0` cuts clean.
- **Cost:** none measurable. With eight bodies sweeping without a break, the median update took 2.16 ms, against 2.2 ms
  held.

### The needle-mouth: a floating family (#10985)

The first fog foe, from round22 option 6 on den-wiki `wandering-years/fog-wildlife`. It is a 4 m ribbon-eel that swims
a mist sump's air.

- **The sheet.** The first pass gave it a gar's narrow snout, so the second drew the head again from a crop of the
  reference (`concept/sheet-b.png`, which Patch approved). The straight reference anchors its side view on a crop of
  sheet B. Its first try drew the end views as upright spikes, so the spec says what an end view of a long body shows.
- **The body.** Tripo made it round, so `prepare_mesh.py --girth 0.55 --depth 1.25` narrows it into a ribbon 0.19 m
  wide and 0.54 m deep. Tripo painted it one lilac, so `margin_paint.py` paints violet along the back and belly by how
  each texel faces, and keeps the head's teeth, eye and slits in their own paint. `open_mouth.py` opens the jaws.
- **The `floater` family.** It uses the serpent's bones. Its clips swim in place, and the product holds the body up
  (`Lift`):
  - `idle` hovers;
  - `walk` and `run` swim and dash;
  - `circle` bows the body into its turn;
  - `threat` gapes, the front drawn back into an S and the rear rising;
  - `bite` is the lunge: the rear arches up and the tail curls over at its tip, then the front shoots out;
  - `hurt` flinches and `die` rolls belly up.

  The arch is lifted from the tail tip, so `floater_pin` moves the root to keep the neck where it rests.
  `hurt` and `die` stay centred, where the serpent's swing about the tail tip.
- **In game.** The kind's `hunt.fog` block holds its float, hearing, feel, circling and clicks (`WildlifeFog`).
  `FogRules` and `CreatureModule.Fog` apply them:
  - It hears, and does not see. A noise is heard as far as the hunt's sight times its loudness. A run and a hard
    landing are loudest, a swing nearly so, and a walking step carries only a few metres.
  - It senses the player only while they are in the mist with it. It bounds itself by the sump's mist (its fog regions
    since #10993, or its laid volumes), not the mouths' glow. A player out of the mist draws it only to the edge, where it
    stops.
  - After each bite it circles off on an errand, and its teeth click (`Cue.NeedleClick`) faster as it nears.
- **Test aid:** `craft.dungeon.mistmap <metres>` maps where a fog foe may swim at that height. The sump's zone cells
  cover only its bottom metre, so they do not bound it.

| | Triangles | Paint | GLB |
| --- | --- | --- | --- |
| Needle-mouth | 7,485 (5,998 body, the rest the mouth's inside and teeth) | 357 x 361 at 64 texels/m | 783 KB |

### The mist that fog foes part (#10993)

A sump's passage is drawn as the Engine's fog regions (rusty-engine #10988), not added volumes, so what swims through it
can part it:
- `SumpMistRegions` fits a box over each straight run of the passage's mist volumes, at their mean thickness and
  current, within the Engine's limits (8 regions, 262,144 cells). The mouths' glowing walls stay volumes.
- `MistStirrers` puts fog stirrers in the mist, moved every step at their speed:
  - three along each creature's body (head, middle and tail, at its swim height), each 1.1 m across and clearing the
    mist wholly, so a fog foe carves a lane its whole length (Patch: "strengthen it");
  - one at the player's chest, 0.6 m across and clearing 0.4, so their lantern keeps its glow in the mist.
- The mist refills over 7 s.
- The add-only eddies of #10989 are off by default (`craft.dungeon.mist eddies on` brings them back).
- Tuning: `craft.dungeon.mist regions|stir on|off`, `settle`, `push`, `clear`, `radius`, and `you <radius,clear>`.

