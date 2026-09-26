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
  ./.runtime/pair-c30c1ef18861/runtime-pack/bin/rusty dev \
  --runtime ./.runtime/pair-c30c1ef18861/runtime-pack \
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
[proof] cleanup: cleared the proof cell, status Accepted
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

The proof edits real voxels and clears its cell afterwards, so a passing run
leaves the world as it found it.

## Operational traps paid for in real time

- **A stale staged product produces misleading catalog errors.** After changing
  authored catalog code, a previously staged build under
  `src/CraftSurvive.Game/obj/Rusty.Engine/Product/` can still be what runs, so the
  Engine reports validation failures for code that no longer exists. If an error
  does not match the source, remove that directory to force a clean restage.
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
