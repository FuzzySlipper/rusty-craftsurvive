# Known limitations

What the product does not do today, and the limits a change has to respect. Each limit names
the constant that sets it or the task that owns it; the value lives there, not here. Study-era
records and superseded limits are in Den, project `rusty-craftsurvive`, under `history/` slugs.

## World and generation

- **The generator is versioned, and the version is the save contract.**
  `TerrainGeneratorContract.CurrentVersion` identifies the world a seed produces. Changing any
  generation rule or tuning moves the generator's fingerprint; the managed goldens in
  `tests/TerrainResidency` and `TerrainGenerationGoldens.Live` then require a version bump, and a
  bump discards every save written for the old version (see Persistence).
- **Generated chunks are cached on disk, keyed on the generator.** `TerrainChunkCache` keeps at
  most `TerrainChunkCacheIndex.MaximumChunks`, oldest first, keyed on
  `TerrainGenerationFingerprint.CacheIdentity`: the live output fingerprint mixed with a
  build-time stamp of the generator's sources (`TerrainGeneratorSource.targets`). Any source
  change to generation therefore empties the cache on the next start.
- **Residency follows the player only.** The request window is
  `TerrainConstants.RequestedChunkRadius`, the retained ring `RetainedChunkRadius`, capped at
  `MaximumResidentChunks`, admitting at most `MaximumResidencyOperationsPerTick` per update
  (chosen by measurement, #8895). Nothing is resident, and nothing has collision, outside that
  window.
- There is no biome framework, general procgen framework or product generation worker.

## Edits

- A view-aimed brush edit has radius at most `TerrainConstants.MaximumBrushRadius` within
  `TerrainConstants.EditReach`; a decided edit (a charge, a stamp) carries at most
  `TerrainBrushPolicy.MaximumTransactionCells` cells, a charge at most `BlastPolicy.MaximumCells`,
  and a stamp at most `BuildStamp.MaximumStampCells`.
- The player's edits are an overlay of at most `TerrainConstants.MaximumOverlayEntries` cells.
  An edit that would exceed it is refused at admission (`OverlayFull`) before the Engine is asked.
- Nothing is placed into the player's body: brush placement, stamps and block entities are
  refused on cells the player occupies. A body placed inside a solid cell faults the product
  (the character controller refuses to step it), which is why `craft.player.teleport` and
  respawn only move the player where a standing body fits.
- **Building is cubic and separate from the terrain's materials.** The UI builds floors and walls
  from `BuildPalette` blocks, laid relative to where the player faces, and only over replaceable
  cells (air, water), so a floor across a slope fills the gaps and leaves the hill. Undo takes back
  the last floor or wall only, and leaves empty what it clears rather than restoring it.
- **A charge resolves on the update after it is fired**, as one transaction, with its dust
  (`BlastDust`, smoke on the authored `dust-puff` sprite) emitted first. A charge past
  `BlastPolicy.MaximumCells` is refused, not truncated; the debris cubes are untextured.
- Inventory, crafting, construction permissions, networking and multiplayer merge policy are
  not implemented.

## Presentation

- **One atlas per voxel scene.** Every block material resolves through the scene's one atlas
  (`content/game/textures/terrain-atlas.*`, validated against `BlockRegistry` by
  `TerrainAtlasLayout`); a material from a second atlas fails the directional projection, as S0
  (#8596) recorded upstream. Normal maps, animated tiles and blending are not implemented.
- The C# runtime draws no shadow maps; the product has no shadow control.

## Creatures and navigation

- **Creatures sleep outside the resident world.** A creature whose ground is not resident is
  dormant - not drawn, sensing or moving - and wakes as the player comes near; creatures spawn
  beyond hostile sight (`CreatureSpawnPlan`), so a fresh session's creatures start dormant.
- **Pursuit follows Engine navigation, published around the player.** `CreatureNavigation`
  publishes collision-derived navigation over a box around the player (its extents and cell
  budget are its constants) only while something pursues, and again after the player moves
  `RepublishDistanceMetres`, a rebase, or an edit. The Engine re-derives only the columns new to
  the box or near changed collision, as long as the box's vertical range is unchanged, so the
  range holds while the player's feet stay within `VerticalSlackMetres`; leaving that band, or
  the first publication, rebuilds the whole box, which is a visible hitch. A pursuer outside
  the box, or with no route, waits; creature movement is planar and follows the ground, so the
  grid's step height is what keeps pursuers out of pits.

## Persistence

- Every saved key is listed in `SaveManifest`, written through `ProductSaveSlot` over one store,
  and guarded by the revision this session last read or wrote: a key changed outside the session
  is reported, not overwritten.
- **Worlds are disposable.** A save written for another seed or generator version is discarded,
  kept as the key's one backup, and the world regenerates; there is no migration.
- Player continuation restores position, look, vitals and progress, not controller motion: the
  player starts at rest. A position where a standing body no longer fits restores at home.
- The discovery journal holds at most `PoiConstants.MaximumDiscoveryEntries` places and refuses
  new ones when full.

## Player and input

- The character controller steps at `PlayerConstants.ControllerStepSeconds`; the world origin is
  rebased under the player past `PlayerConstants.RebaseThreshold`. Cross-origin multiplayer policy
  is not certified.
- **Keyboard input from a page reaches the product only after the Engine canvas has gameplay
  focus.** Keys pressed with the page body or a product UI control focused are not delivered (see
  [live-proofs](live-proofs.md)).
- **Climbing is pushing into a climbable face.** `PlayerClimb` decides it: a face whose block is
  `Climbable` in `BlockRegistry` (earth, rock, masonry, timber) and rises past a single step,
  within `HoldReachMetres`; forward climbs, back climbs down, a jump or crouch lets go, and the
  top lets go where the feet clear the ledge. There are no ladders, mantling animation or
  climbing stamina.
- Controller mapping is product policy in `PlayerInputState` and the project's input intents.

## UI

- `src/ui` is a DOM companion: a HUD over the product's UI projection and a bar that claims the
  product's `craftsurvive.ui` action intent. It holds no game state. The projection keeps the
  stream name `craftsurvive.terrain` and contract `craftsurvive.terrain.v1`, whose fate S9 (#8605)
  owns; kind and stage enums travel as numbers and may be appended to but never renumbered.
- Developer tools in the panel (renderer metrics, the live-debug panel) work only on a host
  started with `--live-debug`.

## Lane, process and Engine gaps

- CI's managed lanes do not launch the runtime; the `product` job serves it, checks it keeps
  updating with a matching generator fingerprint, and walks the player on input.
- A product fault, or an exception from product `Dispose`, prints one line to the host's stderr;
  the full record is in a `--diagnostics-log` file, and `craft.runtime` reports `state=Faulted`.
- The generated C# API exposes named Engine service families, not every Rust source-level API. A
  slice that needs an absent mechanism files or links the upstream capability request and stops
  its downstream substitute work.
