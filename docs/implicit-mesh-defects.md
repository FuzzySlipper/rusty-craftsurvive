# Implicit mesh defect investigation

Engine #7843 and CraftSurvive #7844 follow the layered masonry test. The Engine
correction is `71ba020dc702bada13443b3fdfe95df4b8eb24c8`; the earlier comparison
used `b9740421c2d266f34416234a6021511a0821360f`. The product's field recipe,
materials, layered construction and sampling presets are unchanged.

## What was wrong

Fidget 0.5 emits a fan around each sampled grid-edge intersection. At adaptive
transitions, that point can fall outside the polygon of neighboring cell
vertices. Connecting every polygon edge through it folds triangles back over
the surface. This happens even on a sphere; it is not inherently caused by
complex brick recipes or textures.

A radius-0.7 sphere at octree depth 5 in [-1,1]^3 produced 9,432 raw triangles,
including 192 inward facets in three-triangle fans. The raw connectivity was
closed and consistently wound. Engine's old per-triangle source-gradient flips
made those facets point outward but introduced 576 inconsistently oriented
shared edges. Flipping faces did not repair the folded geometry.

Engine now triangulates the ordered boundary of each dual-cell polygon,
preserving its shared-edge winding. Triangles use their three boundary vertices;
quads choose a diagonal favoring agreeing face normals, with shorter-diagonal
tie breaking. Unused intersection vertices are removed. Unfamiliar/non-simple
fan forms are retained rather than guessed into new polygons. The adapter is
specific to Fidget 0.5's documented-in-source fan index convention; focused
integration regressions protect that assumption.

Seven kernel tests pass, covering closed consistently oriented sphere and
opening meshes, outward box/cap facets, concave quad area/winding, inner cavity
orientation, and existing normal/material grouping behavior. Focused Clippy
also passes with warnings denied. The retained reorientation counter now reads
zero because individual faces are no longer flipped; this does not mean every
possible geometric defect has been solved.

## What remains distinct

- Material regions still classify whole triangles at their centroids. They do
  not introduce edges at requested material boundaries. The layered brick/mortar
  technique remains useful for crisp ownership.
- Very narrow joints and cut bricks can fall below the sampling grid. A wall
  probe with 0.09m joints at about 0.18m actual spacing still contained facets
  spanning unresolved gaps after retriangulation.
- Cell vertex placement can still overshoot or produce self-intersections.
  Zero-area triangles are still omitted. This change supplies no general
  watertightness or intersection-free guarantee.

Fidget supplies field evaluation and adaptive cell connectivity; Rusty Engine
owns final triangulation and surface attributes. C# continues to own only the
product recipe. This is a general Engine correction, with no retro-art rules.
The upstream author's [discussion of meshing limits](https://www.mattkeeter.com/blog/2026-07-03-meshing/)
also distinguishes manifold connectivity from self-intersection and thin-feature
capture. Our diagnosis and correction above come from local source and probes.

## Same-recipe courtyard comparison

The corrected packaged pair is `0.1.0-dev.71ba020dc702`. Soft treatment remains
0.20m requested cells and 110-degree normal crease; width 24m and seed
289142818388 are unchanged. Counts below are complete generated scene totals,
with matched front-view renderer counts separately identified.

| Measurement | Previous b9740421c2d2 | Corrected 71ba020dc702 |
| --- | ---: | ---: |
| Layered scene triangles | 208,486 | 99,010 |
| Layered scene vertices | 147,429 | 87,286 |
| Layered bay triangles (46 parts) | 2,380 | 1,140 |
| Layered front submitted triangles | 103,066 | 49,194 |
| Layered front draw calls | 98 | 98 |
| Original scene triangles | 211,468 | 100,418 |
| Original scene vertices | 149,370 | 88,326 |
| Original bay triangles (1 part) | 5,362 | 2,548 |

The layered scene has about 52.5% fewer triangles without changing its field
recipe. One observed generation was 0.510s versus the old 0.719s; these are
individual observations, not a performance benchmark. The browser uses
SwiftShader with a 320x180 backing buffer displayed at 1280x720, so neither fine
surface quality nor hardware frame rate is established here. Both observed
constructions report zero discarded degenerate triangles and zero face flips.

Matched screenshots use the existing front/grazing inspection poses:

| View | Before | After |
| --- | --- | --- |
| Original front | [before](evidence/layered-masonry/original-front.png) | [after](evidence/implicit-mesh-correction/corrected-original-front.png) |
| Original grazing | [before](evidence/layered-masonry/original-grazing.png) | [after](evidence/implicit-mesh-correction/corrected-original-grazing.png) |
| Layered front | [before](evidence/layered-masonry/layered-front.png) | [after](evidence/implicit-mesh-correction/corrected-layered-front.png) |
| Layered grazing | [before](evidence/layered-masonry/layered-grazing.png) | [after](evidence/implicit-mesh-correction/corrected-layered-grazing.png) |
| Material regions | [before](evidence/layered-masonry/regions-front.png) | [after](evidence/implicit-mesh-correction/corrected-regions-front.png) |

Root inspection and a bounded independent Luna critique found the layered
masonry retained its joints and readability. Original courses appear more
continuous. Triangular color patches remain in the region treatment and at
plaster boundaries, sometimes larger with fewer triangles; the correction does
not make material assignment follow authored boundaries. These images support
aesthetic comparison, not a proof that every courtyard surface is watertight.

Original -> Regions -> Layered regenerated through the normal product update
path. The restored readout and renderer resource counts match the initial
Layered configuration. Raw counts and readouts are retained alongside images.

## Physical use and diagnostics

The playtest first failed to deliver movement: top-level action arguments did
not execute, and a later `key_down` alias was rejected. The unchanged position
and absent key events overruled the initial worker contact claim. Those attempts
remain in the timeline and `failed-input-*` files. Correct documented
`keyboard_down` / `keyboard_up` actions then delivered actual input.

From the controlled front pose, W-only movement stopped at body x=-10.995m,
consistent with the brick face at -11.31m and the 0.315m capsule radius. The
player remained grounded; the Engine readout reports `blocked=Wall` while
pushing forward. From a separate controlled stair start (0,3.875,3.5), a W-only
three-second walk reached (0.779,5.890,23.676), grounded in the raised chamber.
No jump or sprint was used. Before/after screenshots and readouts distinguish
controlled positioning from the physically traversed segments. This exercises
the visible beveled stairs; it does not close hard-riser Engine #7831.

The session-wide Engine diagnostics contained zero warnings, zero errors,
zero diagnostic drops and no lag. Its telemetry still recorded dropped
realtime simulation steps during long operations; this is not a claim to close
Engine #7833. Browser console contained four SwiftShader `ReadPixels` GPU-stall
warnings and no page errors. These driver readback performance warnings are
retained as understood capture-environment limitations in #7844, not suppressed.

The required warning script completed both captures without lag or diagnostic
drops. It recorded one corresponding GPU readback warning. No compatible
baseline was supplied, so comparison is unavailable and `cleanClaimEligible`
is false. We make no clean-warning-delta claim.

The source session is
`rusty-craftsurvive-playtest-20260906T090432.495399617Z-475237`.
A valid 116,633,805-byte pre-finish index was preserved at
`/tmp/implicit-mesh-evidence/playtest-index.pre-finish.json` before cleanup,
because Den-services #7842 previously truncated large final indexes. The
checked `playtest-index.pre-finish-excerpt.json` is explicitly a derived excerpt
omitting the large network-event log; its provenance includes the original
snapshot SHA-256. Raw screenshots and the pre-finish timeline are retained.

Finalization produced a valid 128,587,623-byte final index with status `pass`;
#7842's truncation did not recur in this run. The checked final excerpt omits
only the large event log, and the final timeline is retained. Cleanup metadata
is inconsistent about the driver flag (finish response true, final index false);
root process checks found both broker-tracked PIDs defunct (exited, not yet
reaped), and the actual dev-host child absent. The final index
and timeline, rather than the pre-finish backup, record the completed run.
