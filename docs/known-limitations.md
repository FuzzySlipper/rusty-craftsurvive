# Known limitations

The current C# lane is a deliberately bounded continuation. These limits
describe the landed product; they are not invitations to recreate a parallel
runtime or test infrastructure.

- The default Stoneworks environment and selectable Reference courtyard are explicit runtime construction/whole-scene regeneration experiments, not editable voxel worlds. Reusable layout/field composition now comes from `Rusty.Engine.Implicit`; `CourtyardScene` retains product mesh/collision publication ownership. The Sampled volume study uses Engine-owned retained density data. No editor, erosion, asynchronous regeneration, or world streaming is claimed. See voxel-foundation.md for sampling and topology limits.
- The earlier folded-face holes and directional band sawteeth were corrected in Engine #7843/#7845. Interpolated materials can still miss nonlinear or thin regions hidden between sampled vertices; smaller requested cells do not force dense tessellation on a flat surface. Engine #7854 now provides opt-in MaterialSampleSpacing independent of DC flatness, with explicit mesh budgets; arbitrarily small or tangent regions can still be missed. Small stonework and Flat motifs under #7855 compare physical cuts against material detail (see fine-stonework.md). Separate closed layers require real reveal clearance, not coincident cut surfaces or draw-order tricks. The wall recipes relieve backing behind arch trim; this is not a general welded-topology guarantee.
- Generated meshes supply copied Spatial collision with matching per-part placements. Visible 45° stair noses mitigate the hard vertical-riser solver limitation in Engine #7831 without resolving it. The earlier dense preset timeout remains owned by Engine #7833; broad density increases and real-time mutation are outside this art study.
- Free-form debug camera placement does not validate a safe character pose. A root probe below the floor during #7855 caused Spatial.ProposeCharacterStep failure and runtime restart; use the standing-eye inspection presets for art evaluation. The error and excluded capture are recorded in `docs/fine-stonework.md`.
- Courtyard replacement is bounded by the Engine's committed baseline of 64 MiB for the assembled replacement; existing per-mesh limits remain unchanged. The product publishes the replacement snapshot before retiring its predecessor, and clears the snapshot before disposal. This is a lifecycle rule, not a second retained-scene owner.
- Whole-scene treatment switches can briefly trigger browser baseline recovery and `DEV_HOST_WORKER_TELEMETRY_DROPPED` when timing samples cannot enter the shell queue. Fresh bounded treatment roundtrips recover with the same product state and zero Engine errors, but this is not an instantaneous or zero-warning refresh claim. Engine #7833 owns this publication/telemetry pressure; timing samples can be stale during recovery.
- Earlier Den browser evidence uses software SwiftShader at 320×180 within a 1280×720 viewport, limiting conclusions about pacing, subpixel features and final aesthetics. The Wolf controller test on den-srv additionally exercised native Xbox movement and look in Firefox on the RX 9070 XT, with a 1280×720 streamed window. This is focused control evidence, not a frame-pacing or broad visual certification. Den Services #7853 still owns an explicit accelerated option for its browser harness.
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
- Standard controller input is product policy: the left stick has a radial
  deadzone and preserves its analog magnitude for movement; the right stick
  is integrated with the admitted simulation delta for look. A, B,
  left-stick click, X, RT and LT map to jump, crouch, sprint, impulse, clear
  terrain and set terrain. Trigger edits use the browser's digital button
  edges; the Engine also exposes proportional button values, but editing is
  deliberately a digital action here. The native Wolf test verified stick look,
  movement, jump, crouch, sprint and return to neutral. RT/LT reached terrain
  interaction with a cast-miss result; successful terrain edits were not certified
  by that run. See [controller playtest](controller-playtest.md).
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

## Generated cave levels (#7863)

The six-room cave uses a flat protected floor and two intended graph loops.
Source-field clearance witnesses and mesh topology diagnostics are not exhaustive
navigation or collision certification. Erosion, multilevel routes and streaming
remain outside this slice. The previous Procgen workbench UI is historical;
retained algorithms and offline artifacts live in local CraftSurvive projects.
See [procedural levels](procedural-levels.md) for stage ownership and evidence.
The GPU harness captures real controller interaction and images but does not
supply a browser-console warning delta, audio or measured frame-pacing proof.

The first GPU cave test recorded five recoverable hot-reload warnings, including
a late renderer-observation binding mismatch; Engine #7865 owns that follow-up.
Some streamed screenshots lagged requested inspection poses, so exact view/frame
binding remains unverified. The input/capture/release evidence is retained in
`docs/evidence/procedural-levels/` with those limits explicitly marked.
