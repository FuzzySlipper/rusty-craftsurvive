# Known limitations

The current C# lane is a deliberately bounded continuation. These limits
describe the landed product; they are not invitations to recreate a parallel
runtime or test infrastructure.

- The default courtyard is an explicit runtime construction/whole-scene regeneration experiment, not an editable voxel world. `CourtyardScene` owns its bounded dimensions and three treatment presets. Sampling may miss sub-cell detail; triangle-centroid materials and planar UV charts can show seams. Generated meshes supply copied Spatial collision. The visible 45° stair-nose bevels are that same collision and only mitigate the hard vertical-riser solver limitation in Engine #7831; they do not resolve it. The former dense 0.16m setting is not current: soft-to-dense regeneration crossed the host's five-second operation timeout, tracked as the Engine #7833 long-operation limit and unrelated to the #7831 solver constraint. Aesthetic acceptance and practical cost are tracked in Den #7823.
- Courtyard replacement is bounded by the Engine's committed baseline of 64 MiB for the assembled replacement; existing per-mesh limits remain unchanged. The product publishes the replacement snapshot before retiring its predecessor, and clears the snapshot before disposal. This is a lifecycle rule, not a second retained-scene owner.
- Whole-scene treatment switches can briefly trigger browser baseline recovery and `DEV_HOST_WORKER_TELEMETRY_DROPPED` when timing samples cannot enter the shell queue. Fresh bounded treatment roundtrips recover with the same product state and zero Engine errors, but this is not an instantaneous or zero-warning refresh claim. Engine #7833 owns this publication/telemetry pressure; timing samples can be stale during recovery.
- Current browser evidence uses software SwiftShader at 320×180 within a 1280×720 viewport, limiting conclusions about pacing, hardware behavior, and final aesthetics.
- In `TraversalShowcase`, terrain generation is deterministic generation version 2 with a fixed
  product recipe. Residency requests a 3-by-3 horizontal window, retains a
  5-by-5 window up to 64 populated chunks, and admits at most 16 operations
  per product update. There is no background generation worker, generated
  chunk disk cache, biome framework, or general procgen framework.
- In `TraversalShowcase`, terrain edits use one bounded spherical brush radius of 0, 1, or 2 and are
  admitted as one product revision. Placement is rejected if the complete edit
  overlaps the player or exceeds the Engine coordinate envelope. Inventory,
  crafting, construction permissions, networking, and multiplayer merge
  policy are not implemented.
- The retained voxel terrain presentation admits the canonical authored 128 by 128 atlas through
  Engine AuthoredContent and Appearance, then projects source slots 1 grass,
  2 dirt, and 3 stone through Engine's directional voxel scene presentation.
  Grass uses the grass-side base with a +Y grass-top override; normal maps,
  animated tiles, blending, and any retired source slot 4 are not implemented.
  The authored sky panorama is active through Engine CameraView; the Engine
  retains both resource and renderer lifecycles.
- Engine Spatial remains the collision authority for generated meshes and voxels. The
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
- `src/ui/main.ts` is a DOM companion with Ghost Settings and live diagnostics. Its compact initial chrome keeps metrics and Courtyard controls collapsed; **Show metrics** calls the Engine renderer show/hide commands. Courtyard treatment buttons report a command as queued until a normal product update applies it, while Refresh reads C# state. The packaged Engine host owns the canvas, renderer, physical input delivery, and runtime integration. There is no UI-owned gameplay or non-UI renderer.
- The CoreCLR lane is runnable and supplies the current terrain/player
  continuation, but this repository makes no broad accessibility, hardware, or
  subjective interactive-certification claim. Use a focused direct exercise
  only when a task needs it.
- The generated C# API intentionally exposes named Engine service families,
  not every Rust source-level API. A future product slice that needs an absent
  mechanism must file or link the upstream capability request and stop its
  downstream substitute work.

Retired experiments and their proof scripts are deliberately absent from the
working tree. They are semantic evidence only; no archive copy is maintained
here.
