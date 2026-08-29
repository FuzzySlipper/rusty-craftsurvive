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
| `content/animations/` and `content/voxels/` | User-owned untracked assets retained for future product slices | They are intentionally outside this cleanup and are not part of the current runtime. |

The retired depth-splat, voxel-sprite, ghost-plate, held-animation, and
surface-smoke experiments are not current product architecture. Their source,
fixtures, and scripts were removed from the checkout as part of 7497 and can
be inspected at historical revisions when a future task needs their ideas.
