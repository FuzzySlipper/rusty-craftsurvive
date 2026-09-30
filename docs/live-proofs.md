# Live proofs and evidence lanes

Two lanes prove different things. The **managed lane** runs in CI and covers
product rules and SDK values that need no host (see `tests/`). The **live lane**
runs the real product against the installed runtime pack and is the only lane
that can prove host-bound mechanisms, rendering behaviour, and load paths. A
build passing is not evidence about the live lane; several failures recorded
below were invisible to `dotnet build` and to every managed check.

## The live substrate proof

Answers, against a real session, whether the Engine mechanisms campaign #8595 is
priced on actually work here:

`rusty dev` runs the pair pinned in `Directory.Build.props`; the revision is deliberately not
repeated here, because a pair named in a document is a pair that goes stale in it.

```sh
CRAFTSURVIVE_SCENE=traversal CRAFTSURVIVE_PROOF=substrate \
  rusty dev \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --bind-host 127.0.0.1 --port 37321
```

It runs once on the first update and prints an evidence block. The shape below is
the durable list of what the proof establishes; the exact figures move with the
machine and the generation version, and the run that produced the current ones is
recorded in the task campaign rather than here. Expected output:

```
[proof] live substrate proof beginning (player at (8.00, 6.32, 12.00))
[proof] site (8, 9, 12)
[proof] state cell: wrote 13, read 13 as quarterTurns=1 variant=3; mesh revision 2 -> 3
[proof] state-only edit: material kept at 3, state 13 -> 3, accepted revision 3 -> 4
[proof] direct light: lit luminance=301.1256 from 1 light(s); unlit luminance=0.0000 from 0 light(s)
[proof] swim step inside the volume: mode=Swimming immersion=0.499 headSubmerged=True
[proof] swim step outside the volume: mode=Walking immersion=0.000 headSubmerged=False
[proof] navigation replace: walkable cells=926 revision=1 hash=10117220357098119954 over world box (-8.00, 2.32, -4.00)..(24.00, 14.32, 28.00)
[proof] navigation query: outcome=Reached kind=CollisionDerived cells=5 visited=23 revision=1 from cell (16, 4, 16)
[proof] cleanup: cleared the proof cell, status Accepted
[proof] saved world overlay: none
[proof] world edit through the product path: outcome=accepted;target=8,12,12;changed=1;...
[proof] world overlay saved by that edit: 110 bytes
[proof] persistence round trip: wrote and read back 8192 bytes through the Engine store
[proof] generation determinism: snapshot 1D8E7D5D... across the live Engine keyed RNG
[proof] generation budget: 64 chunks in 87.6 ms (...); resident chunks 48, mesh revision 6
[proof] resident payload: 384 KiB of product chunk payload for 48 chunks at 8 KiB each
[proof] save/load latency: 5.56 ms save, 0.03 ms load for one 8 KiB chunk payload
[proof] player movement: mode=Walking immersion=0.000 headSubmerged=False climbAttached=False
[proof] chunk cache: 4096 voxels stored and read back in 2.85 ms write, 0.41 ms read, identical to fresh generation
[proof] product tick cost over 20 updates with 54 resident chunks: mean 3.549 ms, worst 58.496 ms in the product's residency synchronisation (Engine render and frame time are not included)
[proof] dimension load: session created and one chunk admitted in 1.22 ms
[proof] residency admission: chunk TerrainChunkAddress { X = 5, Y = 0, Z = -2 } admitted in place (1 admitted), resident chunks 54 -> 55
[proof] dimension: second session built with its own residency, resident chunks 1, solid voxels 512, authority 6183767077223527439
[proof] dimension: the first world still reports 19 resident chunks after the second session was disposed
[proof] entity projection validation: an entity without Transform was refused: Appearance entity 1 must be active with a Transform component.
[proof] entity projection: published 1 fact(s); the product's own snapshot publication would be replaced
[proof] live substrate proof PASSED
```

What that establishes:

- **Per-cell state round-trips in a live session.** A cell written as
  `VoxelCellState.Encode(1, 3)` reads back as state 13 and decodes to one quarter
  turn and variant 3; the edit advances the mesh revision; and a state-only edit
  keeps the material while advancing the accepted revision. Oriented blocks,
  rotation, and growth stage therefore need no entity.
- **Direct-light sampling is real and goes dark.** A lit cell reports luminance
  301.13 from one contributing light; an address beyond every light's range
  reports 0.0 from zero. The dark-cave mechanism is "disable the default rig,
  then place lights", not a propagating light lattice.
- **Swim mode works from a product-supplied volume.** Inside the water AABB the
  receipt reports `Swimming`, immersion 0.499, and `HeadSubmerged=True`; outside
  it the same command falls back to `Walking` with zero immersion. **Swimming and
  submersion need no water material at all** — only a volume and a mode. The water
  *material* is a separate, purely visual concern.
- **Navigation works, but only after the product publishes a walkable
  projection.** `RequestNavigationPath` alone answers `ProjectionUnavailable`
  with `kind=None`; calling `ReplaceCollisionNavigation` over a world box first
  derives 926 walkable cells from the same voxel authority collision uses, after
  which the same query answers `Reached` with a five-cell path. Query cells are
  relative to the published box, not world voxel coordinates.
- **Residency admission is applied in place.** One `ApplyResidency` call for a
  chunk outside the product's own plan raised the resident chunk count by one.
  The product composes the payload; the Engine removed background preparation
  (Engine #8739), so there is nothing to poll, commit or cancel.
- **Dimensions are product-owned and need no runtime restart.** A second
  `SpatialSession` was created inside the running product with its own
  configuration and residency (one chunk, 512 solid voxels, its own authority
  hash), read back through the ordinary voxel API, and disposed — after which the
  first world still reported its 19 resident chunks. S8's authored dungeons
  therefore need a load boundary and authored content, not a new Engine mechanism.
- **Entity projection requires whole-snapshot ownership.** `EntityGraphicsProjection`
  replaces the complete appearance snapshot, and the Engine refuses a snapshot that
  drops a projected animation target or a ghost plate's source object. A product
  that publishes its own snapshot each frame — as this one does — cannot also
  publish a standalone projection; adopting projections means moving all appearance
  publication onto that path, and the projecting store must register
  `EngineComponentTypes.Transform` with every projected entity carrying it.

The proof edits real voxels and clears its cell afterwards, so a passing run
leaves the world as it found it.

## Operational traps paid for in real time

- **A stale staged product produces misleading catalog errors.** After changing
  authored catalog code, a previously staged build under
  `src/CraftSurvive.Game/obj/Rusty.Engine/Product/` can still be what runs, so the
  Engine reports validation failures for code that no longer exists. If an error
  does not match the source, remove that directory to force a clean restage.
- **A killed run can leave staging half-done.** Killing `rusty dev` mid-flight
  occasionally makes the next start fail with
  `Rusty Engine did not stage ProductContent at .../Product.next/content`. It is a
  race, not a code problem: start the run again.
- **A stale persistence blob is reported and discarded, not fatal.** Changing the
  authored catalog invalidates a stored overlay or journal, and the load path
  detects the mismatch, discards it, keeps the previous bytes as a backup, and says
  so - the journal's readout reports `restore=discarded: Stored journal was written
  for generation 7, not 8`, and has done so five times against real saves. The
  version policy is decided, not open: **there is no migration**, so a change to the
  world's generation deliberately throws the stored state away rather than
  converting it. Move the blob aside only if the *discard* is what you want to keep
  for inspection; the product no longer needs the manual workaround this trap
  described.
- **`rusty dev` restarts a crashed worker twice, then stops.** A product that
  throws during construction appears as `child-exited-unexpectedly` twice and
  `paused-fault: unexpected-child-exit-budget-exhausted`. Read the first
  `CSHARP_PRODUCT_CALL` line, not the last.
- **Cartesian origin note:** voxel addresses print as integers; a cell written at
  the player's feet plus three voxels is a different cell from the one the player
  stands in, and a character step that starts inside a solid cell is rejected with
  `EngineCallException ... ProposeCharacterStep returned status 0`.
- **A failed appearance-snapshot publish stops later updates.** When the Engine
  refuses a snapshot, the runtime records a callback error and the product's update
  loop does not continue: the proof's later stages never ran, with nothing printed
  and no error line. Anything that must run after a snapshot publish belongs
  *before* it, and a rejected snapshot should be treated as fatal to the session
  rather than as a recoverable per-frame failure.
- **One atlas per voxel scene.** A scene material whose surface resolves through a
  second atlas fails the directional projection (filed upstream as a robustness
  request). Every block material therefore needs a region in the scene's own
  atlas image.

## The live-lane client

`scripts/live.mjs` is the one client for a running product. It needs a host started with
`--live-debug` (as `.den-serve.json` starts it) and takes the host's address from the
environment, never from the script:

```sh
rusty dev --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj --live-debug --bind-host 127.0.0.1 --port <port>
export CRAFT_ORIGIN=http://127.0.0.1:<port>   # or CRAFT_PORT=<port>
```

```sh
node scripts/live.mjs walk --seconds 2
```

Walks the player with the forward control for two seconds and prints the distance, the
positions before and after, and how many key events the product received. It fails unless the
player moved at least `--minimum` metres (default 1) *and* the product received the key events,
so the distance is attributable to input. Other actions: `--action walk-back|strafe-left|strafe-right`.

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
