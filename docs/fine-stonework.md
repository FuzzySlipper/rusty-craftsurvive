# Fine stonework and material sampling

Tasks: Engine #7854/#7856 and CraftSurvive #7855. The recipe remains product-local;
Engine owns field evaluation, dual contouring, material subdivision, mesh
admission and collision. Den Services #7853 (accelerated playtest launch) stays
separate from this work.

## Trying the study

Open **Courtyard → Small stonework → Detail overview**. Four stations run
left to right. **Detail 1–4** frame individual stations; **Tiny close-up**
approaches the smallest carving; **Small bricks** looks down at the 8 cm brick courses. Escape releases the mouse for controls.

| Station | Block width | Block height | Joint width | Carved stroke width |
| --- | ---: | ---: | ---: | ---: |
| 1 | 64 cm | 30.72 cm | 7.68 cm | 16 cm |
| 2 | 32 cm | 15.36 cm | 3.84 cm | 8 cm |
| 3 | 16 cm | 7.68 cm | 1.92 cm | 4 cm |
| 4 | 8 cm | 3.84 cm | 0.96 cm | 2 cm |

Each carving cuts through a limestone face to expose a separate backing 6 cm
behind its rear surface. Its visible face is 29 cm behind the limestone front;
the recess deliberately makes even narrow cuts unambiguous when they survive.
The fourth backing also carries a matching bronze material motif. Disconnected
masonry is meshed as one union per station, with a continuous footer remaining
extractable even when coarse sampling misses all tiny bricks.

**Geometry detail** requests 16 cm (Coarse), 8 cm (Normal), or 2 cm (Fine)
sampling for masonry and physical cuts. Actual octree spacing rounds down to a
power-of-two subdivision of the padded cubic enclosure. The plain backing retains
its separate 8 cm geometry spacing; the fourth, region-bearing backing follows
the selected detail spacing. This is a sampling comparison, not a claim
that a stroke as wide as one cell will always survive.

**Flat motifs** displays the same four designs on uncarved slabs with bronze
material regions. Their geometry spacing stays fixed at 8 cm. **Original
vertices / 4 cm samples / 2 cm samples** changes only material field sampling;
it cannot repair a missing physical cut. Selecting material sampling also
selects Interpolated boundaries. Centroid assignment uses whole triangles and
ignores the product's stored material sampling preference.

The original Stoneworks, Reference bay and Sampling plaques remain available.
The material sampling controls apply to the region-bearing Stoneworks sampling
plaques as well. Original vertices is the default, preserving existing studies.

## Engine behavior and numerical evidence

`ImplicitGenerateRequest.MaterialSampleSpacing` defaults to zero. Positive
spacing requires Interpolated boundaries. The Engine subdivides shared edges
consistently until each triangle edge meets the requested length, then samples
region fields and clips using the existing first-region precedence. It does
not move the extracted surface; normals and UVs retain their interpolation.

A fixed two-triangle, 1 m square with a 7 cm diameter enclosed circle has zero
region coverage without refinement. Across 0°, 45° and 90° placements:

| Material spacing | Circle area error | Output triangles (approximately) |
| --- | ---: | ---: |
| Original vertices | 100% missing | 2 |
| 4 cm | 7.51% | 4,140 |
| 1 cm | 0.45% | 65,730 |

These are numerical surface-area measurements, independent of browser pixels.
They demonstrate a useful spacing/accuracy/cost tradeoff, not a universal
minimum size. Arbitrarily small or tangent regions can still fall between
samples. The 262,144 vertex and triangle limits apply during refinement and
clipping; an excessive request fails explicitly. Narrow geometric features
still depend on DC sampling and can disappear independently of material detail.

Use `craft.courtyard.readout` for scene totals and `craft.courtyard.parts` for
per-part triangle/vertex/group counts, actual cell spacing and generation time.
Those service times are not frame rate or end-to-end replacement latency.
An Engine generation rejection disposes the unpublished replacement and leaves
the previous scene/settings applied; `generationError` in the readout explains
the rejection. It is not silently reduced to a lower density.

## Visual observations

The paired-runtime capture compares identical cameras and settings. On Normal
geometry, the 8 cm bricks merge into larger bands and the 2 cm lattice is
swollen/distorted. Fine geometry restores much more of the brick subdivision
and produces narrower, more regular carving channels. Actual Fine spacing is
about 1.48 cm for masonry and 1.72 cm for carved faces. The smallest 0.96 cm
mortar joint is still below one geometric sample, so exact joint preservation
is not established by a readable image.

At the same Fine geometry, changing Soft (110°) to Faceted (0°) removes much
of the broad pillowy shading around the incisions without changing the carved
shape. Shading treatment is an important independent aesthetic choice.

The 2 cm flat motif is absent with original vertices and appears with 4 cm and
2 cm material spacing. The thinner strokes are still pixel-stepped/broken in
the software capture; this does not establish whether each break is geometry
or subpixel rasterization. Numeric coverage tests remain the independent
material-preservation evidence.

| Study state | Total triangles | Observed synchronous generation |
| --- | ---: | ---: |
| Small stonework, Coarse (Balanced) | 2230 | 0.038 s |
| Small stonework, Normal | 11,358 | 0.069 s |
| Small stonework, Fine | 88,938 | 0.437 s |
| Flat motifs, original vertices | 294 | 0.015 s |
| Flat motifs, 4 cm samples | 121,894 | 0.179 s |
| Flat motifs, 2 cm samples | 470,678 | 0.880 s |

Counts include the display floor. These are one local observation per state,
not performance targets. Material subdivision currently refines the whole
requested mesh, including portions without a motif. Splitting authored
surfaces into bounded pieces keeps that cost local; 2 cm sampling on an entire
wall is not the intended default. Here, physical layered carving is cheaper
than densely subdividing all four flat slabs.

Automated captures remain software-rendered at 320×180 within a 1280×720
viewport. Ordinary-resolution/hardware evaluation belongs to the separate
playtest work. This study gives usable authoring comparisons, not a universal
minimum feature size or final-art certification.

## Capture provenance and diagnostics

Session `rusty-craftsurvive-playtest-20260907T024052.400466360Z-1701294`
uses Engine pair `ca51d868ba71a68fcb8b21c7d6708fa996048db7`.
Selected captures and per-part readouts are in `docs/evidence/fine-stonework`.
Original and 4 cm numeric readouts were reproduced after their screenshots with
the same recipe settings; generation times are from those readbacks. Final UI
restaging added the Small bricks view and reversed station placement to make
large→small run left-to-right. Earlier individual-panel comparisons retain
identical shapes/settings; their overview order is reversed.

One root diagnostic camera move placed the player below the floor and caused
`Spatial.ProposeCharacterStep` to fail, with terminal callback diagnostics,
an HTTP500 and a runtime restart. Captures 0012 and 0013 are diagnostic only;
0014 and later follow explicit scene restoration. This was an invalid root
camera setup, not an ordinary detail-study control. The full session error
record is retained; the run is not claimed error-free. The required warning
capture has no compatible baseline, so its empty observation window cannot
establish a clean warning delta.

A separate deliberate request for 2 cm material sampling across the larger
Sampling plaques correctly exceeded the mesh budget but exposed Engine #7856:
the old bridge poisoned the product callback even when C# caught the exception.
`budget-rejected-readout.txt` and `engine-diagnostics-before-recovery-fix.json`
retain that **before-fix** restart evidence. The updated Engine pair
`d7e59b48ca0cbe08f6fa4a64f8e83403d97de3ac` returns an owned operation diagnostic;
its native regression rejects an over-budget request, releases the diagnostic
lease exactly once, generates successfully and commits within the same callback.

Fresh final session `rusty-craftsurvive-playtest-20260907T031438.132853034Z-1701294`
used that updated pair and the final UI. `final-fine-overview.png` shows the
large-to-small ordering; `final-small-bricks.png` exercises the actual Small
bricks control. The smallest courses read clearly across much of the panel,
but some joints merge, particularly toward its right side: this is a useful
practical limit, not proof of faithful sub-cell reproduction.

The intentional Sampling plaques rejection preserved **generation 5**, the
13-part fine stonework scene and its 254,496 triangles (2 cm material sampling
was enabled on the fourth backing). The diagnostic named the triangle budget.
Ordinary controls then restored original material vertices at **generation 9**,
88,938 triangles and `generationError=none`. Before/after screenshots and the
rejection/restored text readouts are retained under `final-*` and
`budget-rejected-after-fix-readout.txt`.

Final full Engine readback had zero errors, seven warnings and no dropped or
lagged diagnostics. Four early warnings accompanied root live restaging before
the study actions (worker replacement, stale control binding and temporary
browser degradation); readiness recovered before the comparisons. Three were
known timing-publication pressure warnings owned by Engine #7833. The warning
capture itself remains report-only with no compatible baseline. This does not
claim an entirely warning-free session. The worker recorded advisory missing
sequence fields on some inspection calls; indexed captures remained available.

The final runtime pair is `ad9879ae369d28781fa34818a84f4c60d6bc88c3`.
It adds the reviewed terminal handling for malformed native pointer/length
requests, tested directly at the ABI. It changes neither the meshing algorithm
nor ordinary generated C# requests; the preceding visual and recovery captures
remain the relevant comparisons. Final pair adoption and LAN readback are
recorded separately from that browser evidence.
