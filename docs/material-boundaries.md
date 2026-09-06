# Implicit material boundaries

Engine #7845 and CraftSurvive #7846 follow the geometry-hole correction. The
remaining directional sawteeth are a different mechanism: assigning a material
to a complete triangle does not place an edge at the requested material cutoff.

## Diagnosis and Engine mechanism

A fixed rectangular strip with area 4 was colored below three horizontal
cutoffs. Neither its vertices nor its triangles changed. With centroid
assignment, cutoffs at 0.2, 0.5 and 0.8 colored areas 0, 2 and 4 respectively.
Interpolated boundaries colored areas 0.8, 2 and 3.2, matching the requested
fractions. Tests repeat the sweep on perpendicular wall orientations, checking
coverage and winding. This isolates material quantization from texture mapping,
camera direction and the earlier folded-polygon defect.

The same triangle patterns can repeat along similar-facing walls. Different
cutoff heights intersect different rows of triangles, so clean and sawtoothed
boundaries can alternate between the top and bottom. The screenshot was a
useful clue; the fixed-mesh test establishes the mechanism independently.

Engine `2e99efa5cbe9aa4c2f214384a2220ca01edde35d` adds the opt-in
`ImplicitMaterialBoundaryMode.Interpolated`. It samples each region at original
surface vertices and splits triangles along the zero contour of those linearly
interpolated samples. Earlier regions retain priority. UV charts and original
corner normals are constructed before clipping; new vertices interpolate their
attributes. Material cuts do not introduce a normal crease or change the
underlying extracted shape. The old nine-argument request constructor retains
`Centroid`, so this is a reusable choice rather than a retro-art policy.

Ten focused kernel tests pass, including cutoff coverage/winding, overlapping
region priority, consistent shared-edge crossings, UV continuity and smooth
normal interpolation. Focused Clippy passes with warnings denied. Generated
bindings, SDK Release build and both native implicit bridge tests pass.

## Approximation limits

Affine fields, such as planes, yield exact straight cuts within floating-point
precision. Nonlinear fields produce polygonal boundary approximations. A region
hidden between sampled vertices can be missed, and a thin stripe can disappear;
this does not replace source sampling or exact field/triangle intersection.
Added triangles and vertices count toward ordinary Engine mesh limits. Layered
brick/mortar construction remains available when separate solids fit the design.

## Courtyard comparison controls

The courtyard now defaults to Interpolated. Its existing panel offers
**Centroid / Interpolated** plus cutoff offsets **-0.08 / 0 / +0.08m**. Test-wall
construction and shading controls retain their prior meaning. The debug commands
are `craft.courtyard.boundaries centroid|interpolated` and
`craft.courtyard.cutoff <meters>`; cutoff is bounded to -0.15..0.15m.

The horizontal plaster/moss bands are expressed as plane regions at their prior
cutoff heights. This avoids the other sides of a box field distorting a linear
interpolant, without changing the wall or cap geometry. More intricate brick
regions retain their constructive fields and the approximation limits above.

Cutoff only changes the plaster material-region placement and wall-moss cutoff;
it does not change the geometry field. Mode/cutoff changes regenerate through
normal product updates, including the ordinary collision mesh copy. The readout
reports both settings so captures can be matched. The kernel fixed-mesh test
above isolates clipping without even rerunning extraction.

## Visible and physical result

Session `rusty-craftsurvive-playtest-20260906T103516.094639865Z-835292`
used the exact packaged pair above. The fixed corner pose was eye (7,4.55,-5),
target (11.4,5,-9.4), with Soft treatment, width 24 and the existing default seed.
Ordinary UI controls changed modes and cutoffs; the camera stayed fixed.

| Comparison | Evidence |
| --- | --- |
| Centroid, cutoff 0: repeated triangular plaster/moss borders | [before](evidence/material-boundaries/0003-centroid-corner.png) |
| Interpolated, cutoff 0: continuous borders on both walls | [after](evidence/material-boundaries/0002-interpolated-corner.png) |
| Interpolated, cutoff -0.08m | [lower cutoff](evidence/material-boundaries/0004-interpolated-minus.png) |
| Interpolated, cutoff +0.08m | [higher cutoff](evidence/material-boundaries/0005-interpolated-plus.png) |
| Separate layered masonry retained, grazing angle | [layered bay](evidence/material-boundaries/0009-layered-boundaries-grazing.png) |

Root inspection found the large teeth disappear in Interpolated mode and the
band edges move with the cutoff without returning to a triangular pattern. A
bounded independent Luna critique agreed about the visible boundary improvement.
This is a color-boundary correction, not a change to the wall's outer silhouette.
The near layered bay retains its separate brick/cap forms; its moss exclusion is
unchanged. No broad topology conclusion is inferred from the screenshots.

At the matched corner view:

| Measurement | Centroid | Interpolated |
| --- | ---: | ---: |
| Generated scene triangles | 99,010 | 116,088 |
| Generated scene vertices | 87,286 | 97,200 |
| Corner submitted triangles | 41,588 | 48,898 |
| Corner draw calls | 49 | 49 |
| Resident geometry resources | 113 | 113 |
| Resident materials | 315 | 315 |
| Layered test bay triangles | 1,140 | 1,140 |

The correction adds about 17.2% scene triangles. Observed generations were
0.507s for Centroid, 0.494s initially for Interpolated, and 0.520s after restoring
Interpolated/cutoff 0. These are individual observations, not a speed benchmark.
The renderer used SwiftShader with a 320x180 backing displayed at 1280x720;
hardware performance and subpixel edge quality remain unmeasured. Returning
from the cutoff sweep restored the same triangle, vertex and resource counts.

For physical use, root placed the player at (4,3.875,-6), facing the south wall.
Actual W input stopped at (4,3.876,-8.995), grounded, with `blocked=Wall` while
pushing forward. A subsequent physical W+A slide moved x from 4 to -3.064 while
z stayed -8.995 and the player stayed grounded at the observed endpoints. The
requested wait was 600ms, not a measured total key-hold interval. Delivered key
events, movement readouts and before/contact/slide screenshots are retained.
The controlled start is distinct from these physical movement segments.

## Diagnostics and evidence limits

The full-session Engine diagnostics had zero warnings/errors, zero diagnostic
drops and no lag. Browser console contained four SwiftShader `ReadPixels`
GPU-stall warnings and no page errors. These understood driver readback warnings
are retained under #7846 as capture-environment limitations, not suppressed.
The required warning script completed both captures without lag or diagnostic
drops and recorded one matching GPU-stall warning. No compatible baseline was
supplied: comparison is unavailable and `cleanClaimEligible` is false.

A valid 51,770,848-byte pre-finish index snapshot was preserved at
`/tmp/material-boundary-evidence/playtest-index.pre-finish.json` due to the
previous recorder-index issue #7842. The checked summary labels its provenance
and omits the large event/timeline payload; console and timeline are separate
files. This backup is not presented as a final run index.

The final index is valid JSON (60,734,456 bytes) with status
`pass`; #7842 truncation did not recur. Final summary and timeline are retained.
Cleanup reports disagree on the driver flag (finish true, final index false);
broker-tracked processes had exited to defunct state and the port37100 listener
was gone. Sequence-missing advisories remain in the timeline; UI effects and
physical input delivery were independently checked against visible results and
Engine readouts. The extraction degenerate counter does not count material-cut
subdivision; it is not a general output-topology certificate.
