# Live proofs and evidence lanes

Two lanes prove different things. The **managed lanes** (`tests/`) run in CI and cover product
rules and SDK values; the focused collision lanes use the Engine's packaged
headless test host. The **live lane** runs the real product against the
installed runtime pack and is the only lane that can prove host-bound mechanisms, rendering and
load paths. A build passing is not evidence about the live lane. CI's `product` job is the one
automated live check: it serves the product, checks it keeps updating with a matching generator
fingerprint, and walks the player on input with the client below.

The live substrate proof that established the Engine constraints this product builds on left
with the authoring lane; its record is Den `history/live-substrate-proof`.
The [product map](csharp-migration-map.md) and [limitations](known-limitations.md)
describe the implementation contracts that a new proof must exercise.

## Operational traps

- **An unwatched stream pauses the entire product.** The product reads Engine
  `Watching` once per update and holds Engine gameplay time when false. Clock,
  survival, creatures, streaming and saving all wait; an attached page resumes
  them. Debug commands and input claims are not watchers. Use `--headless` for
  unattended live checks so its browser page watches. A playtest session already
  supplies a page; do not add another watcher when testing detachment.
- **A product fault stops the world.** An exception that escapes a product callback leaves the
  runtime `state=Faulted` (`craft.runtime`) and prints one line to the host's stderr; start the
  host with `--diagnostics-log <file>` for the full exception and its stack.
- **A stale staged product produces misleading catalog errors.** After changing authored catalog
  code, a previously staged build under `src/CraftSurvive.Game/obj/Rusty.Engine/Product/` can
  still be what runs. If an error does not match the source, remove that directory to force a
  clean restage.
- **A killed run can leave staging half-done.** Killing `rusty dev` mid-flight occasionally makes
  the next start fail to stage its content. It is a race, not a code problem: start again.
- **A stale save is discarded, not fatal.** A save written for another seed or generator version
  is discarded, kept as that key's backup, and the readouts say so (`restore=discarded: ...`).
  `craft.save.manifest` lists every key and whether it and its backup are stored.
- **`rusty dev` restarts a crashed worker twice, then stops.** A product that throws during
  construction appears as `child-exited-unexpectedly` twice and then
  `paused-fault: unexpected-child-exit-budget-exhausted`. Read the first `CSHARP_PRODUCT_CALL`
  line, not the last.
- **Persistence lives in the working tree.** `rusty dev` keeps the product's store under
  `.runtime/persistence`; a live experiment that edits the world changes the saved world. Copy
  that directory aside first when the edits should not stay.

## The live-lane client

`scripts/live.mjs` is the one client for a running product. It needs a host started with
`--live-debug` (as `.den-serve.json` starts it) and takes the host's address from the
environment, never from the script:

```sh
rusty dev --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj --headless --live-debug --bind-host 127.0.0.1 --port <port>
export CRAFT_ORIGIN=http://127.0.0.1:<port>   # or CRAFT_PORT=<port>
```

```sh
node scripts/live.mjs walk --steps 60
```

Holds time (the Engine's manual mode) and walks the player with the forward control over that
many simulation steps (60 a second), then puts time back as it was. It prints the distance, the
steps and product updates it ran, the positions before and after, and how many key events the
product received. Because the steps are counted, not timed, a slow machine walks as far as a fast
one (about 7 m for 60 steps on level ground). It fails unless the player moved at least
`--minimum` metres (default 4) *and* the product received the key events, so the distance is
attributable to input. Other actions: `--action walk-back|strafe-left|strafe-right`.

```sh
node scripts/live.mjs discovery
```

Stands the player at the nearest unvisited standing stones and checks the journal counted a first
visit, stored itself at 32 + 51 bytes per place, and reports what its restore did (`--kind` picks
another kind of place).

```sh
node scripts/live.mjs exec <debug command>
```

Runs any debug command, including the Engine's playtest commands the product registers:
`playtest.observe` (the player's feet, look, vitals and received key events, as JSON),
`playtest.action <id>` (which physical control an action is) and `playtest.look <yaw> <pitch>`.

**The `craft.*` commands are developer tooling, never part of play.** Everything in the core
loop has a player path: moving, climbing and swimming on keys; fighting on the attack key;
crafting, eating, resting, building, blasting, choosing a difficulty and going in and out of
dungeons and the world map from the HUD's actions. A command that does one of these (`craft.player.attack`,
`craft.inventory.craft`/`use`, `craft.dungeon.enter`/`leave`, `craft.build.*`,
`craft.blast.fire`, `craft.world.difficulty`, `craft.world.view`) is a scripted stand-in for that path. The rest
are for the lane:
- reading an owner's state (each family's `readout`, `craft.terrain.*`, `craft.runtime`,
  `craft.save.manifest`, `craft.discovery.near`/`find`/`crossings`, `craft.feedback.readout`);
- setting up a live check (`craft.player.teleport`/`strike`, `craft.survival.set`,
  `craft.world.hour`, `craft.inventory.grant`, `craft.creatures.restart`,
  `craft.dungeon.visit`, `craft.feedback.cue`, `craft.build.dig`);
- comparing generators and measuring costs (`craft.dungeon.approach`/`surface`/`seed`/
  `validate`/`blast`, `craft.creatures.navigation`, `craft.terrain.timing`).

A command earns its place by serving one of these; one whose experiment has ended is retired
with it.

**Vegetation reach and forests.**

Ground cover grows on the voxels to 40 m, then on two Engine vegetation tile fields out to the
Ground cover option's reach, thinning with distance; trees are Engine vegetation records with a far
mesh and an impostor level, out to a reach that follows the view distance. `craft.vegetation.readout`
reports both, with each field's tiles, pending tiles, copies and over-budget layers; before judging a
view, wait for `pendingAdmissions=0` and `settled=True` on the tiles. `craft.terrain.groundcover <m>`
and `craft.terrain.fartrees <m>` set the reaches for the session. `engine.renderer` reports the
renderer's per-copy selection (`gpu.scatterSelection`), impostors and pass timings; a stream's frame
rate includes capture and delivery, so it is not a gameplay FPS measurement.

`engine.renderer.camera` takes scene-local coordinates: a long teleport that rebases the world origin
moves every pinned camera, so take fixed-pose captures in a session that has not travelled far.

Forests come from the flora catalog (`content/game/flora/catalog.json`): each species' climate range
sets where the canopy closes, with forest floor under it, stands of mostly one species, sparse
open-country trees. On the ground beside them (`plantTiles` and `floorTiles` in the readout), every plant
species grows on its own, so ferns, woodland flowers, shrubs, saplings and deadwood overlap. These are
Engine vegetation records: shrubs, deadwood and saplings to 160 m, ground plants to 112 m, and shade
grass on the forest floor to 72 m. Fallen logs and stumps are solid near the player:
`craft.vegetation.deadwood <log|stump|fallen-trunk|any> <x> <z>` lists the nearest, and `craft.vegetation.solid off|on` compares
walking the same path over a log without and with them (the player's height rises over the log only
when solid). Grass is blades (#10038): seeded clumps of tapered blades near (to 40 m) and a lighter clump in the mid tiles (to
112 m), with painted cards beyond. `craft.terrain.grass cards|blades` switches it everywhere it grows to compare at a
held pose. A fallen trunk blocks: walk straight at one (`craft.vegetation.deadwood fallen-trunk <x> <z>`)
and the player slides along it, then jump it. `craft.vegetation.sites <x> <z>` finds closed forest near a point with its trees per 128 m
square, and `craft.terrain.trees <species> <x> <z>` the nearest trees of one species. The focused check
is `dotnet run --project tests/TerrainResidency -c Release -- forest`. Compare a forest interior, its
edge, open country and a raised vista; a stand should read as one species with others mixed in.

**Water appearance comparison.**

At a river or lake, `craft.water.visual waves` (the default) draws the sea state's waves (#9902);
`flow` draws the same flowing surface without them, and `baseline` the Engine's water alone, at the
same camera pose and held time. The waves follow the weather's wind; `craft.water.wind <m/s>` holds a
wind (2 calm, 9 a fresh breeze, 20 a gale; a negative speed follows the weather again), and
`craft.water.waves.at <x> <z>` reads a column's fetch, swell share, shore coverage, significant
height in the wind now, and how far the drawn surface stands over the rest level. Compare a calm
pond, a great lake in a gale (whitecaps on the downwind faces), and a river in the same gale (only a
chop). `craft.water.lakes <x> <z> <count>` lists the nearest lakes with the waves a gale raises on each.

Boats and swimmers ride what is drawn: `craft.boat.readout` gives the boat's pitch, roll, heave and the
waves' lift under it. Board in deep water in a gale and paddle: the player stays aboard, rising and
falling with the hull. Shore coverage fades both opacity and displacement into the bed, and motion
fades across the last 24 m of the near-water square to meet the flat distant water. Ice stays still.

The waves are shader displacement on the product's clock, separate from the rest level used for
depth, fords and wading. The installed shader hook returns positions only, so lighting normals come
from the displaced 1 m triangles: look for faceting in highlights and in whitecaps close to the
camera. Culling still uses the undisplaced mesh bounds, so the waves are capped to stay within a metre.

Floods (#9880): `craft.water.floods <x> <z>` gives the area under water now and the flooded floodplain
nodes nearest a point (level, depth over the lattice's ground, reach). Stand on a bank beside one and look
at the water in the river's valley; on the map, a walk across it stops at Floodwater.
`MapLab --floods SEED --size METRES [--from-day D --days N]` draws the worst flood of a stretch of the year
from before the storm to the plain draining.

`craft.water.visual velocity` displays the mesh's current: red increases toward +X, green
toward +Z, and blue means slow flow. Resume time to judge motion as well as the still image.

`craft.water.flow <world-x> <world-z>` starts a 128 m Engine WaterField over the generated bed,
with fixed stages on wet boundary cells. Its sampled current drives the appearance, blending
back to regional current near the edge. This study does not change water levels, swimming,
boats or terrain; it is a visual consumer of the field. `craft.water.flow.at <x> <z>` compares
field depth/current with the regional current, and `craft.water.readout` includes snapshot age
and worker cost. Check movement both inside the field and across its edge.
`craft.water.flow.stop` restores regional flow. The study's bed and boundary stages are captured
when started; restart it after terrain or river-stage changes. Compare in the overworld:
separate landscape studies and distant map water use their own materials.

**How input reaches the product.** `walk` does not use a page. It claims input through the
host's harness lane (`control/claim`, `runtime/input`, `control/release`), which delivers key facts to
the product's ordinary mappings exactly as a page would; an attached page shows the input as held
by `craft-live` meanwhile.

**Keyboard input from a page reaches the product only once the Engine canvas has gameplay
focus.** Measured with a headless page: with the page just loaded (focus on the document body),
or with a product UI button focused, holding W for 1.5 s delivered no key events and moved the
player 0 m; after one click on the canvas the same hold delivered 2 key events and moved the
player about 11 m. A page-driven lane must click the canvas (not the UI panel) before sending
keys. This is why earlier scripted walks recorded no key events.

**A body placed inside the world stops the product.** The character controller refuses to step a
body that starts inside a solid cell, and that refusal faults the product. `craft.player.teleport`
therefore moves the player only where a standing body fits - the point asked for, else the ground
of that column - and building refuses cells the player's body occupies.

## Browser captures through the playtest lane

Screenshots come from the crew-services playtest lane, which serves the product from
`.den-serve.json` and drives a page from `.den-playwright.json`. Do not start a competing host on a
broker-owned session's port merely to look at it.

```sh
playtest start rusty-craftsurvive     # returns SESSION and a first screenshot
playtest observe SESSION              # the current frame
playtest capture SESSION --json '{}'  # a retained capture
playtest stop SESSION
```

A page-driven check must click the world before sending keys (see above). Retained captures
belong under [evidence](evidence/); each names the pair and machine it was taken on in Den.
