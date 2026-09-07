# Stoneworks recipe study

Den campaign #7849: recipe separation #7850, environment #7851, sampling and
presentation evaluation #7852. The original phase used Engine pair `0.1.0-dev.2e99efa5cbe9`.
Engine #7861 has since upstreamed the reusable vocabulary; see
[the current foundation](voxel-foundation.md).

## Ownership and reuse

The SDK's `Rusty.Engine.Implicit.WallLayout` owns local wall coordinates, opening dimensions and
globally phased masonry courses. X follows the wall, Y is up and -Z is its
decorated side. `ArchitecturalRecipes` composes Engine fields for backing,
courses, broken plaster and arch trim from that same layout. Material handles
are borrowed from a caller-provided palette. Style and dimensions remain C#
product policy.

`ImplicitRecipe` is a scoped vocabulary over Engine operations; it neither
evaluates fields nor generates triangles. `RecipeWriter` submits a
`RecipeSurface` synchronously while its field remains alive. Surface bounds,
sampling, material regions and placement travel together. It retains no meshes,
appearance handles, collision world, player facts or UI state.

`CourtyardScene` remains the sole retained owner. It generates surfaces through
Engine ImplicitSurfaces, creates appearances, and copies exactly the generated
meshes into Spatial using the same placements. A replacement snapshot is
published before predecessor resources retire. `CourtyardRecipe` contains the
original comparison geometry; `StoneworksRecipe` is the second composition.
Rigid placement turns the local geometry, texture frame and collision together.
Each arch layer has ordered reveal clearance: trim owns the requested opening, plaster retreats 0.08m, courses 0.16m and mortar 0.24m. Otherwise separately extracted cut faces can overlap even when the front faces have real relief. This is not a claim that independently extracted pieces form a welded mesh.

The layout/composition vocabulary and its caller-supplied palette/output
contract now live in the SDK. Keep the stoneworks dimensions,
wear pattern, material assets, scene switches and inspection cameras downstream.
There is no new field AST, dispatch registry, evaluator, mesher or renderer.

## Controls

The Courtyard panel exposes Stoneworks, Reference bay and Sampling plaques.
Reshape changes width 24→26m, door width 3.4→3.8m, door offset 0→0.25m and seed.
Original dimensions restores those values. Arrival, Plaster, Arcade, Carving
and Samples are inspection cameras, not evidence of physical movement.

`craft.courtyard.study stoneworks|reference|sampling` selects the recipe.
`craft.courtyard.detail coarse|normal|fine` changes only sampling plaques:
requested cell sizes 0.32/0.16/0.08m. Shading presets affect the general scene.
The readout reports the applied study, settings and generated geometry counts.

Plaques repeat one local design at 0°, 45° and 90°. Groove/stripe widths are
0.04/0.08/0.16/0.32m from left to right; circular patches use 0.6 times each
width as radius. Four raised silhouette fins use the same widths. Local extraction is shared in meaning, then rigidly placed;
this isolates orientation in presentation without changing the source grid.
Interpolated material boundaries sample existing vertices. Refining a material
region is not guaranteed simply by asking for a smaller geometric cell size:
the adaptive extractor can still leave a flat surface coarsely tessellated.

## Validation

`dotnet run --project tests/ArchitecturalRecipes -c Release` checks that bay
partitioning preserves occupied brick intervals and that rotated/resized
openings retain their dimensions and center. `pnpm run check:ui` and the normal
product Release build check integration. All three checks pass.

## Visual iteration

Session `rusty-craftsurvive-playtest-20260907T000850.502569316Z-1541405`
contains a deliberate source restage between screenshots 0006 and 0007.
Earlier arch captures exposed overlapping mortar/course cut faces; disabling
shadows did not remove the dark patches. Ordered reveal clearances removed
those patches in the matched post-restage [arcade view](evidence/stoneworks/arcade.png).
The [carved focal panel](evidence/stoneworks/carving.png) now shows its recessed
rays. The [plaster study](evidence/stoneworks/plaster.png) was captured before
that reveal adjustment; its uninterrupted west-wall recipe is unaffected.

Root visual judgment: the material hierarchy and arch rhythm justify continuing
this authoring approach. A Luna critique independently found clear material
separation and a coherent arcade, while noting repeated pointed plaster breaks,
soft columns and a broad pale path. Those are art-direction tradeoffs for the
next iteration, not mandates to change the mesher. This remains an environment
study, not final art approval.

Automated captures remain SwiftShader 320×180 enlarged to 1280×720. The Engine
already preserves ordinary resolution for accelerated browsers. Den Services
#7853 owns the missing explicit accelerated playtest launch control. Use the
normal LAN browser and `engine.renderer.detail` to verify adapter and backing
size for an owner-machine comparison; no software screenshot certifies that.

Current default source readout after the reveal fix: 160 parts, 164,272
triangles, 130,787 vertices, 0 extraction degenerates, observed generation
0.874s. This is a single source-generation observation, not frame-rate or
hardware performance evidence. The degenerate counter excludes material-cut
subdivision and is not a topology certificate.


## Route, reshaping and sampling

The first physical route attempt drifted into the garden after an unintended
pointer delta. For the accepted attempt, the canvas was captured first and root
set an explicit start at global body (0,3.875,-7), yaw 180°, pitch 0°. Actual W
input then crossed the stairs, gate and arcade to global (0.056,5.890,29.385),
grounded at the shrine: [endpoint](evidence/stoneworks/walk-hall.png) and
[global entity readout](evidence/stoneworks/walk-global.txt). The local player
readout ends at z4.385 because origin rebasing had shifted the local frame;
`entity.component craft 1 1024` is the existing global-position projection.
Inspection-camera placements are setup, not movement evidence.

Reshape applied width 26, door 3.8, offset 0.25 and seed 12345: 165 parts,
168,614 triangles, 134,472 vertices, observed 0.981s. The
[reshaped arrival](evidence/stoneworks/reshaped-arrival.png) retains aligned
trim/opening and material layers. Restoring default dimensions and selecting
Reference reproduced 110 parts, 116,088 triangles, 97,200 vertices and the
46-part/1,140-triangle layered bay. This checks that extracting the original
recipe from the runtime owner preserved its geometry output.

Matched sampling captures: [coarse](evidence/stoneworks/sampling-coarse.png)
and [fine](evidence/stoneworks/sampling-fine.png). Requested 0.32m→0.08m
increased the three plaques from 354→3,750 triangles. Grooves and top fins become
much more visible, but the intended upper closed material stripes/patches
remain absent in these views. This distinguishes geometric detail from
material-region coverage; it does not certify a per-width pixel visibility
threshold at this software resolution. Engine #7854 owns an opt-in mechanism
to preserve bounded material motifs independently of geometric flatness,
starting with a numeric fixed-surface reproduction. No downstream evaluator,
mesher or shader substitute was added.


## Diagnostic limits

The required warning script completed browser and Engine capture at cursor
19→19 without lag, drops or findings. No compatible baseline was supplied, so
it is report-only (`cleanClaimEligible=false`). It covers its final window,
not the earlier source-edit transition.

The whole-session Engine read recorded 17 warnings and 0 errors: 13 timing-sample
queue drops (the existing Engine #7833 limit); one worker-replacing rejection,
one stale lifecycle binding and two degraded browser transitions during the
intentional source restage. A subsequent ready/baseline-established event names
the replacement binding. The browser retained 4 ReadPixels GPU warnings and one
HTTP 500 from `/runtime/debug/execute` whose body was `DEV_HOST_WORKER_REPLACING`
during that restage, with 0 page exceptions. The rejected debug request was not
an applied operation. The restage evidence belongs to this #7851/#7852 run;
it is not presented as a warning-free continuous session. Capture cursor data
itself had no drops or lag.

The final source restores Stoneworks, normal plaque detail, Soft, default
dimensions, cutoff 0 and shadows on. A valid 134,822,155-byte pre-finish index is
preserved at `/tmp/stoneworks-playtest-prefinish.json`; the committed summary
explicitly omits large network event payloads. The full broker index and
separate timeline remain the reconstruction authority.
