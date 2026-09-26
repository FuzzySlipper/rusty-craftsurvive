# S0 decision record

Slice S0 of campaign #8595 exists to settle four decisions and to test the
mechanisms the campaign is priced on before Slices 2, 4, and 7 commit to them.
The mechanism verdicts are in [live-proofs](live-proofs.md); this record carries
the decisions. Each one states a recommendation, the evidence behind it, and who
owns the call.

## 1. Content floor for V1

**Settled 2026-09-26: a deliberately small floor — 12–16 block types, 6–10 item
types, 2 creatures, one biome family plus a cave and a dungeon tileset.**

Rationale: every block type costs an atlas region (see §5 below), a material
entry, an item definition, and a face-material decision; every mob costs a
projection, navigation, and an FSM. The floor should be the smallest set that
makes an adventurer loop work — walk, cross water, enter a dungeon, fight, carry
loot, eat, sleep — and nothing more.

Confirmed by the product owner. Consequence: S2 budgets 12–16 atlas tiles and
authors them deliberately, because each block type costs a region in one atlas
image (§5). This is the cheapest decision to revisit later, so it is set low
rather than negotiated upward.

## 2. Manipulation model

**Settled 2026-09-26: adventurer manipulation, not mining.** The player places
blocks and detonates charges; per-block break-and-collect is not the core verb.
Block-breaking exists only where a slice names it (a door, a weak wall, a
resource node), and every world-changing action arrives as one bounded revisioned
edit with presentation covering the remesh.

Evidence: `AGENTS.md` and the direction doc already record that manipulation is
deliberately slow and infrequent so a sluggish voxel remesh is never on the
critical path. The live proof adds that a single voxel edit advances the mesh and
source revisions and rebuilds chunks, so *cheap* edits are not free either — the
budget argument for infrequent, FX-covered manipulation holds.

Confirmed by the product owner. Consequence: S6 keeps charges, blasts, and
placement, and drops any general break-time table; block-breaking survives only
where a slice names a specific target.

## 3. World extent and border

**Settled 2026-09-26: a finite bounded world of roughly 100 km², generated on
demand inside a declared extent, with an authored hard border the player reads as
terrain rather than as an invisible plane.**

Rationale: the direction doc already commits to a finite world of roughly 100 km².
The live proof shows residency is a product-owned plan with bounded operations per
tick, and that navigation must be published for a world box — both fit a declared
extent naturally and both get harder with unbounded extents. The border should be
authored terrain (cliffs, deep water, or a wall of the world's own material)
rather than an invisible plane, so it never reads as a bug.

Confirmed by the product owner. Arithmetic for S2: 10 km × 10 km at one-metre
voxels and 16-voxel chunk edges is 625 × 625 chunks per layer — large in total,
but residency is on demand, so only the declared extent and border need defining
up front. The border should be authored terrain (cliffs, deep water, or the
world's own material), and navigation must be published per box rather than for
the whole extent.

## 4. Save and version policy

**Settled 2026-09-26: worlds are disposable. The save envelope carries an explicit
world identity and generation version, a mismatch is discarded rather than
migrated, and the previous blob is retained as one backup.**

Evidence, from the live lane rather than from documentation: changing the authored
terrain catalog invalidated the persisted overlay blob, and `Persistence.Load`
**failed the entire product** instead of reporting an unreadable or stale blob.
The product therefore has no migration story today — it has a hard failure. Given
generated worlds and a pre-V1 product, "discard and regenerate" is both cheaper and
honest, provided the failure is turned into a deliberate, reported reset.

Confirmed by the product owner. S2 requirements: version the envelope, detect the
mismatch explicitly instead of letting it reach a load call, keep one previous
generation, and report a reset to the player rather than failing the session.

## 5. Constraints inherited from S0's live proofs

These are not open questions; they are findings that bound the decisions above.

- **One atlas per voxel scene.** Every block material needs its own region in the
  scene's atlas image, and a material whose surface resolves through a second atlas
  fails the directional projection (filed upstream as a robustness request). The
  content floor is therefore also an atlas-packing decision, and new block types
  should be budgeted in tiles.
- **Water is separable from water rendering.** Swim mode, immersion, and
  `HeadSubmerged` work from a product-supplied volume with no water material at
  all. "Static water" costs nothing mechanically; only its appearance needs
  authored tiles.
- **Navigation must be published.** A world without `ReplaceCollisionNavigation`
  answers every path query with `ProjectionUnavailable`. Each dimension needs its
  own published projection.
- **A dimension is a second session.** It can be created, filled, read, and
  disposed inside the running product without disturbing the loaded world, so a
  load screen is a product concern rather than an Engine request.
- **Background residency preparation is the supported overlap path**, and a
  rejected candidate is a normal outcome rather than an error.
- **A refused appearance snapshot stops the update loop.** Anything that must run
  after a snapshot publish belongs before it, and a rejection should be treated as
  fatal to the session.

## 6. Status

| Decision | Recommendation | Owner | State |
| --- | --- | --- | --- |
| Content floor | 12–16 blocks, 6–10 items, 2 creatures, one biome family | product | **settled 2026-09-26** |
| Manipulation | Adventurer manipulation with charges; no general break table | product | **settled 2026-09-26** |
| World extent | Finite ~100 km², authored hard border | product | **settled 2026-09-26** |
| Save policy | Disposable worlds, explicit version, discard on mismatch, one backup | product | **settled 2026-09-26** |

All four decisions S0 owed are settled, so S0 has no open product questions left.
What remains in the slice is evidence work, not decisions.
