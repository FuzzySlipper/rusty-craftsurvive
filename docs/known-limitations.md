# Known limitations

The current C# lane is a deliberately bounded continuation of the old
CraftSurvive experiment. These limits describe the landed product; they are
not invitations to recreate retired Rust, browser, or test infrastructure.

- Terrain generation is deterministic generation version 2 with a fixed
  product recipe. Residency requests a 3-by-3 horizontal window, retains a
  5-by-5 window up to 64 populated chunks, and admits at most 16 operations
  per product update. There is no background generation worker, generated
  chunk disk cache, biome framework, or general procgen framework.
- Terrain edits use one bounded spherical brush radius of 0, 1, or 2 and are
  admitted as one product revision. Placement is rejected if the complete edit
  overlaps the player or exceeds the Engine coordinate envelope. Inventory,
  crafting, construction permissions, networking, and multiplayer merge
  policy are not implemented.
- The terrain presentation uses three product-owned flat material colors with
  Engine-owned voxel scene projection. It does not yet consume an authored
  texture atlas, material variants, normal maps, animated tiles, or blending.
  The canonical texture files remain in `content/textures/` for a later
  deliberate content slice.
- Engine voxel collision remains the canonical collision authority. The
  product's moving platform is a translating axis-aligned box supplied as a
  call-local character obstacle; rotated or general rigid-body platform
  collision is outside this slice.
- The player owns a 120 Hz controller cadence, first-person look, sprint,
  crouch, jump, impulse, moving-platform schedule, camera composition, and
  world-position policy. Engine owns the character solver, collision casts,
  support/carry, camera resource, and origin mechanism. No general entity,
  rigid-body, animation, or scheduler framework is claimed.
- The product rebases at a named local threshold and retains signed global
  positions. It remains bounded by Engine's admitted coordinate envelope; no
  limitless precision, cross-origin multiplayer policy, or background-world
  streaming is certified.
- Persistence is one bounded, product-owned terrain overlay stored through
  Engine Persistence. There is no migration/merge policy for incompatible
  schemas or concurrent writers.
- `src/ui/main.ts` is a static DOM companion. Engine's generated
  product-browser-host owns the canvas, renderer, browser input delivery, and
  runtime transport. There is no TypeScript gameplay or non-UI renderer.
- The C# host is runnable and supplies the current terrain/player continuation,
  but this repository makes no broad browser, accessibility, hardware, or
  subjective interactive-certification claim. Use a focused direct exercise
  only when a task needs it.
- The generated C# API intentionally exposes named Engine service families,
  not every Rust source-level API. A future product slice that needs an absent
  mechanism must file or link the upstream capability request and stop its
  downstream substitute work.

Retired Rust/session/browser experiments, garden assets, and their proof
scripts are deliberately absent from the working tree. Git history is the
record for those experiments; no archive copy is maintained here.
