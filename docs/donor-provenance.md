# Donor provenance

The C# migration deliberately keeps semantic ownership in the product while
using Rusty Engine for reusable mechanisms. The old downstream Rust/session
and browser experiment was retired in task 7497; Git history is its source of
truth rather than a maintained archive copy.

| Source | C# lane use | Boundary retained |
| --- | --- | --- |
| Previous CraftSurvive Rust terrain/player/edit loop | Deterministic recipe vocabulary, bounded terrain fixtures, first-person controls, edit semantics, platform route, and global-position intent | C# owns policy and state; Engine owns collision, spatial services, presentation, persistence primitives, and host lifecycle. |
| Rusty Engine character/look services (tasks 6847/6849) | Generated character step, look integration, support/carry, crouch/step facts, external impulse, and controller continuation | Product owns cadence and tuning; it does not copy the solver or create a second renderer. |
| Rusty Engine voxel services (tasks 6848/6851) | Generated voxel sessions, chunk admission/leases, revisioned edits, retained scene projection, and material bindings | Product owns recipe and edit admission; Engine owns resident scene, mesh, collision, handles, and render resources. |
| Rusty Engine world-origin service (task 6895) | Exact global/local values, explicit player/platform roots, synchronized rebase, and continuation after origin changes | Product decides when to rebase and retains global meaning; Engine performs the atomic mechanism. |
| `content/textures/` | Canonical terrain atlas, sky panorama, source images, and checked metadata retained for future C# content work | Current terrain slice uses named flat material colors and does not treat browser copies as content authority. |
| `content/animations/` | User-owned untracked assets retained for future product slices | They are intentionally outside this cleanup and are not part of the current runtime. |
| `content/voxels/woodland-shrine-nano-model-solid64.vox` | Selected runtime source for the C# microvoxel presentation | User-owned content is admitted through Engine `ProductContent`; CraftSurvive does not reimplement the VOX parser or conversion pipeline. |

The selected microvoxel runtime copy is
`content/voxels/woodland-shrine-nano-model-solid64.vox` (99,682 bytes, SHA-256
`a0a38ff44c6f753df55c772bbf957841fe82d074128dbd9b0e07deaebadd20c6`). It is
byte-identical to
`/home/dev/asset-pipeline/live-evidence/voxel-experiment-gallery/imports/shrine-nano-tripo-p2-vengi-solid64-20260822-001/members/model-solid64.vox`.
That member belongs to the asset-pipeline artifact set
`shrine-nano-tripo-p2-vengi-solid64-20260822-001`. Its recorded source and
conversion chain is Nano Banana 2 source image → manually run Tripo Studio P2
mesh → Vengi `voxconvert` 0.5.0.0 at revision
`4d5fbc9993c9bd877e0a4936cacdca41320439`, using longest-axis resolution 64,
enclosed-cavity fill, and palette creation → the selected `.vox` output. The
artifact set and source comparison live under ignored asset-pipeline evidence;
the copied `.vox` is the current CraftSurvive runtime content.

The retired depth-splat, voxel-sprite, ghost-plate, held-animation, and
surface-smoke experiments are not current product architecture. Their source,
fixtures, and scripts were removed from the checkout as part of 7497 and can
be inspected at historical revisions when a future task needs their ideas.
