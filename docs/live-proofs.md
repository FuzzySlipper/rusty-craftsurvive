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

```sh
CRAFTSURVIVE_SCENE=traversal CRAFTSURVIVE_PROOF=substrate \
  ./.runtime/pair-afbe891e1d34/runtime-pack/bin/rusty dev \
  --runtime ./.runtime/pair-afbe891e1d34/runtime-pack \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --bind-host 127.0.0.1 --port 37321
```

It runs once on the first update and prints an evidence block. Expected output:

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
[proof] residency preparation attempt 1: started for chunk (5, 0, 0), status Pending, resident chunks 18, source revision 5
[proof] residency preparation: committed on attempt 1, resident chunks 18 -> 19
[proof] residency preparation: a second preparation for chunk (6, 0, 0) cancelled cleanly
[proof] dimension: second session built with its own residency, resident chunks 1, solid voxels 512, authority 6183767077223527439
[proof] dimension: the first world still reports 19 resident chunks after the second session was disposed
[proof] entity projection validation: an entity without Transform was refused: Appearance entity 1 must be active with a Transform component.
[proof] entity projection: publishing a standalone snapshot is refused while the product retains a ghost plate (...); projections require whole-snapshot ownership
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
- **Background residency preparation works and is the streaming path.** A preparation
  started for a chunk outside the product's own plan reported `Pending` without
  blocking the frame, reached `Ready`, committed to `Committed`, and raised the
  resident chunk count from 18 to 19; a second preparation cancelled cleanly. The
  product composes the payload and the Engine builds the projection off the
  admitted update path.
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
- **A stale persistence blob fails the whole product.** Changing the authored
  catalog invalidates `.runtime/persistence/craftsurvive/terrain/overlay`, and
  `Persistence.Load` then fails the run instead of discarding an unreadable
  overlay. Move the blob aside rather than deleting it; the save/version policy
  is still an open decision.
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

## Browser evidence

Screenshots and input-driven evidence go through the crew-services playtest lane
against a broker-owned session, using the scripts under `tests/` and the
`.den-playwright.json` serve entry. See
[rope-playground](rope-playground.md) for the submission form
(`playtest run SESSION --file <absolute-path> --budget-ms <ms>`) and
[evidence](evidence/) for retained captures. Do not start a competing host on the
broker-owned port merely to look at one; run the live proof on an ephemeral port
as shown above.

The lane itself is verified up to session allocation. `playtest games` lists
`rusty-craftsurvive` at `http://192.168.1.22:37300/`, and the product serves
`/product-ui/main.js` there. Capturing a sample screenshot needs a free pool slot:

```sh
# 1. serve the world on the lane's declared port
CRAFTSURVIVE_SCENE=traversal ./.runtime/pair-afbe891e1d34/runtime-pack/bin/rusty dev \
  --runtime ./.runtime/pair-afbe891e1d34/runtime-pack \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --bind-host 0.0.0.0 --port 37300

# 2. allocate a slot, then capture and stop
playtest start rusty-craftsurvive     # returns SESSION and a screenshot
playtest observe SESSION              # returns the current frame
playtest capture SESSION --json '{}'  # retained capture
playtest stop SESSION
```

Two caveats recorded on 2026-09-26: the configured pool was fully occupied by other
projects, so no sample capture is retained yet; and the registered game entry still
describes "First-person courtyard" while the campaign's world is the traversal
showcase, so the lane's description and default scene should follow the S2 boot
switch rather than staying on the retired courtyard.
