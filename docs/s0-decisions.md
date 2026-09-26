# S0 decision record

Slice S0 of campaign #8595 exists to settle four decisions and to test the
mechanisms the campaign is priced on before Slices 2, 4, and 7 commit to them.
The mechanism verdicts are in [live-proofs](live-proofs.md); this record carries
the decisions. Each one states a recommendation, the evidence behind it, and who
owns the call.

## 1. Content floor for V1

**Recommendation: a deliberately small floor — 12–16 block types, 6–10 item
types, 2 creatures, one biome family plus a cave and a dungeon tileset.**

Rationale: every block type costs an atlas region (see §5 below), a material
entry, an item definition, and a face-material decision; every mob costs a
projection, navigation, and an FSM. The floor should be the smallest set that
makes an adventurer loop work — walk, cross water, enter a dungeon, fight, carry
loot, eat, sleep — and nothing more.

Owner: **product** (needs a decision). This is the cheapest decision to revisit
later, so it should be set low rather than negotiated upward.

## 2. Manipulation model

**Recommendation: adventurer manipulation, not mining.** The player places
blocks and detonates charges; per-block break-and-collect is not the core verb.
Block-breaking exists only where a slice names it (a door, a weak wall, a
resource node), and every world-changing action arrives as one bounded revisioned
edit with presentation covering the remesh.

Evidence: `AGENTS.md` and the direction doc already record that manipulation is
deliberately slow and infrequent so a sluggish voxel remesh is never on the
critical path. The live proof adds that a single voxel edit advances the mesh and
source revisions and rebuilds chunks, so *cheap* edits are not free either — the
budget argument for infrequent, FX-covered manipulation holds.

Owner: **product** (confirm). If confirmed, S6 keeps blasts and charges and drops
any general break-time table.

## 3. World extent and border

**Recommendation: a finite bounded world, generated on demand inside a declared
extent, with a hard border the player cannot cross — not an infinite stream, and
not a void fall.**

Rationale: the direction doc already commits to a finite world of roughly 100 km².
The live proof shows residency is a product-owned plan with bounded operations per
tick, and that navigation must be published for a world box — both fit a declared
extent naturally and both get harder with unbounded extents. The border should be
authored terrain (cliffs, deep water, or a wall of the world's own material)
rather than an invisible plane, so it never reads as a bug.

Owner: **product** (needs a decision: extent in chunks, and which border).

## 4. Save and version policy

**Recommendation: worlds are disposable; the save envelope carries an explicit
world identity and generation version, and a mismatch is discarded rather than
migrated. Back up the previous blob before overwriting.**

Evidence, from the live lane rather than from documentation: changing the authored
terrain catalog invalidated the persisted overlay blob, and `Persistence.Load`
**failed the entire product** instead of reporting an unreadable or stale blob.
The product therefore has no migration story today — it has a hard failure. Given
generated worlds and a pre-V1 product, "discard and regenerate" is both cheaper and
honest, provided the failure is turned into a deliberate, reported reset.

Owner: **product** (confirm), and it becomes an S2 requirement: version the
envelope, detect the mismatch explicitly, keep one previous generation, and never
let a stale blob reach a load call.

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
| Content floor | 12–16 blocks, 6–10 items, 2 creatures, one biome family | product | open |
| Manipulation | Adventurer manipulation with charges; no general break table | product | confirm |
| World extent | Finite declared extent, authored hard border | product | open |
| Save policy | Disposable worlds, explicit version, discard on mismatch, one backup | product | confirm |
