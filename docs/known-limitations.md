# Known limitations

What the product does not do today, and the limits a change has to respect. This is
the live list. The study-era records it used to carry - the procgen workbench motif
and repair limits, the courtyard and stoneworks art studies, the cave-level and
large-complex slices, and the LAN startup regression - are in Den, project
`rusty-craftsurvive`, under `history/` slugs. The repository keeps no archive copy.

## Voxel world (`CRAFTSURVIVE_SCENE=traversal`)

- Generation is deterministic version 2 over a fixed product recipe. Residency asks
  for a 3x3 horizontal window, retains 5x5 up to 64 populated chunks, and admits at
  most 16 operations per update. There is no biome framework, general procgen
  framework, generated-chunk disk cache, or product generation worker; streaming and
  the world spine are S2 of campaign #8595, and Engine-owned residency preparation is
  the supported overlap path.
- Edits use one bounded spherical brush of radius 0, 1 or 2, admitted as one product
  revision. Placement is rejected if the edit overlaps the player or exceeds the
  Engine coordinate envelope. Inventory, crafting, construction permissions,
  networking and multiplayer merge policy are not implemented.
- Presentation admits the canonical 128x128 authored atlas through Engine
  AuthoredContent and Appearance, then projects source slots 1 grass, 2 dirt and
  3 stone through directional voxel scene presentation; grass uses the grass-side
  base with a +Y grass-top override. Normal maps, animated tiles, blending, and the
  retired source slot 4 are not implemented.
- **One atlas per scene.** A material whose surface resolves through a second atlas
  fails the directional projection, so a block's appearance must come from the
  scene's own atlas image.
- **Navigation must be published.** A world without a collision-derived navigation
  projection answers every path query with `ProjectionUnavailable`, and query cells
  are relative to the published box.
- Persistence is bounded, product-owned state through Engine Persistence: a terrain
  overlay and, since S5, a discovery journal. **A blob written for a different generation
  is now detected and reported rather than failing the load** - the journal discards the
  stale save, preserves the previous bytes as a backup, and says so in its own readout
  (`restore=discarded: Stored journal was written for generation 7, not 8`), which has
  happened five times against real saves. The overlay behaves the same way. What remains
  missing is **migration**: a version change regenerates rather than converting, so a
  player's journal is deliberately thrown away when the world changes, and there is still
  no policy for concurrent writers.

## Platform limits confirmed by measurement

- **Passable voxel material: this was real, and has since been honoured.**
  Collision used to stop at every non-empty voxel whatever the material declared, so
  water was solid, a player stood on a lake instead of swimming in it, and a ladder,
  door or any other non-blocking block could not be authored. Measured with a downward
  raycast through a three-layer lake that stopped at the top face of the water voxel
  rather than at the lake bed, while the water material is declared non-solid,
  non-collidable and non-occluding in `BlockRegistry` and those flags reach its
  `AuthoredMaterialInput`. Filed upstream with that evidence, and the report was acted
  on: the collision rule now honours the material, the Engine provides swimming and
  submersion, and the swim policy's positive case runs against generated terrain -
  walkers are still refused in water while swimmers are allowed from a shore. Recorded
  rather than deleted because that measurement is what got it fixed, and because it is
  the shape of limitation this document exists to track: real, evidenced, filed, closed.
- **A multi-cell edit transaction stalls the update loop.** One
  `VoxelEditTransaction` carrying nine or more edits reports `Accepted` and then
  no further product update runs: no failing status, no worker EOF, no
  crash-budget message, no exception. One and two-edit transactions are
  unaffected. This matters to this product specifically because manipulation is
  place-blocks plus *detonate charges*, where a multi-cell edit is the intended
  operation rather than a test artefact. Filed upstream with the table of
  transaction sizes and the diagnostics that do not appear.

Both are live limits of the installed pair, not bugs in this repository. The
passable-material limit is `rusty-engine` #8685 and the edit-transaction stall is
`rusty-engine` #8684; this repository keeps no copy of their state.

## Player and input

- The product owns a 120 Hz controller cadence, first-person look, sprint, crouch,
  jump, impulse, moving-platform schedule, camera composition and world-position
  policy. Engine owns the character solver, collision casts, support/carry, the
  camera resource and the origin mechanism. No general entity, rigid-body, animation
  or scheduler framework is claimed.
- The moving platform is a translating axis-aligned box supplied as a call-local
  obstacle. Rotated or general rigid-body platform collision is outside that.
- Controller input is product policy: the left stick has a radial deadzone that keeps
  its analog magnitude, the right stick integrates with the admitted simulation
  delta, and A, B, left-stick click, X, RT and LT map to jump, crouch, sprint,
  impulse, clear terrain and set terrain. Trigger edits use digital button edges.
- Rebasing happens at a named local threshold and signed global positions are kept,
  bounded by the Engine coordinate envelope. Limitless precision and cross-origin
  multiplayer policy are not certified.

## Authoring lane (courtyard and studies)

- The Stoneworks environment and Reference courtyard are runtime
  construction/whole-scene regeneration scenes, not editable voxel worlds. Reusable
  layout and field composition come from `Rusty.Engine.Implicit`, and `CourtyardScene`
  retains product mesh/collision publication ownership. No editor, erosion,
  asynchronous regeneration or world streaming is claimed there.
- Courtyard replacement is bounded by the Engine's committed 64 MiB baseline for the
  assembled replacement. The product publishes the replacement snapshot before
  retiring its predecessor and clears it before disposal; that is a lifecycle rule,
  not a second retained-scene owner.
- Free-form debug camera placement does not validate a safe character pose. Use the
  standing-eye inspection presets for art evaluation.
- Whole-scene treatment switches can briefly trigger browser baseline recovery and
  `DEV_HOST_WORKER_TELEMETRY_DROPPED` while timing samples cannot enter the shell
  queue. Engine #7833 owns that publication and telemetry pressure.

## UI

- `src/ui/main.ts` is a DOM companion with Ghost Settings and live diagnostics. Its
  compact chrome keeps metrics and courtyard controls collapsed, **Show metrics**
  calls the Engine renderer show/hide commands, and courtyard treatment buttons
  report a command as queued until a normal product update applies it. The Engine
  host owns the canvas, renderer, physical input delivery and runtime integration;
  there is no UI-owned gameplay and no non-UI renderer.
- **The `craftsurvive.terrain` UI projection has no consumer.** `TerrainWorld` opens
  the stream and publishes terrain facts to it, and no DOM or host code reads it
  back: `src/ui/main.ts` contains no reference to the stream name or its contract. It
  is a vestigial publication rather than a broken one, and removing or consuming it
  is deliberately not decided here. The decision belongs to S9 of campaign #8595
  (#8605), which owns HUD and screens and would have to live with either choice.

## Lane and process

- The CoreCLR lane supplies the current terrain and player continuation, but no broad
  accessibility, hardware or subjective interactive-certification claim is made. Use
  a focused direct exercise when a task needs one.
- The generated C# API exposes named Engine service families, not every Rust
  source-level API. A slice that needs an absent mechanism must file or link the
  upstream capability request and stop its downstream substitute work.
- Retired experiments and their proof scripts are deliberately absent from the working
  tree. They are semantic evidence only, kept in Den, and no archive copy is
  maintained here.
