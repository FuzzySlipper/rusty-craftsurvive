# Stepped cave study

CraftSurvive #7857; Engine #7858. This is a bounded, walkable composition study
of runtime implicit geometry, following the fine-stonework sampling tests.

## Controls

Open **Courtyard → Cave → Balanced → Cave entrance**. **Column**,
**Carved wall**, and **Terraces** give standing-eye views inside the room.
WASD/mouse use the ordinary player; Escape releases the mouse for controls.
The floor is bounded: this is a chamber, not a streamed cave world.

**Geometry detail** changes only the two carved faces: Coarse requests 14 cm,
Normal 7 cm, Fine 3.5 cm. The incisions are 12 cm wide. The wall cuts through a 32 cm face,
with bronze starting 10 cm behind its rear face. The column cuts directly into
a tapered course; a smaller bronze core sits behind the incisions.
Broad rock masses keep their own sampling (generally 30 cm; column courses
16 cm and hanging tapers 18 cm). Actual octree spacing is in
`craft.courtyard.parts`; it rounds according to each padded domain.

Soft/Balanced/Faceted changes normal treatment. Cave material contrast uses
separate physical surfaces, so the material-sampling buttons do not change the
cave geometry. The earlier studies remain available.

## Composition and ownership

`CaveRecipe.cs` authors the terraced walls, two layered columns, irregular skylight,
hanging tapers, moss shelves, loose stones and carved inlays. Deep wall/column
rock closes the intentional bedding gaps. A flat central floor provides the
walking route. Cool rock, warm deposits, moss and bronze reuse the existing
Stoneworks palette. A product lighting profile supplies cool skylight and warm
local light; changing studies restores the original profile.

The Engine's existing `ImplicitSurfaces` family owns field evaluation, DC
extraction, normals/UVs and material splitting. Graphics owns generated mesh
resources, appearances and retained publication. Spatial takes copied mesh
collision. The recipe is synchronous and product-local, with the same
replacement/disposal path as the earlier studies.

Engine #7858 adds the general `AddFrustum` primitive: two endpoints and two
radii specify a capped cylinder, cone or taper. Geological clipping, stepped
profiles, palette and density are product choices. Tests cover cap and side
signs, reversed endpoints, a diagonal cylinder, a cone and closed generated
meshes. The final pair is `b84b3dcb8840ef72305af10faf42be5bff3fc20b`.

A reuse audit confirmed that Engine already supports multiple appearances and
placements over one generated mesh, plus matching collision instances. This
study varies its courses rather than introducing an instancing framework.
No renderer, mesher, collision cache or platform mechanism is implemented in
the product. No asynchronous generation, streaming, editing or performance
budget claim is made by this study.

## Observations and evidence

The first pass read too much like a rectangular hall: a bright floor, square
roof opening and a column-mounted plaque. The final composition darkens the
floor, varies rock-course heights, replaces the hatch with stepped irregular
skylight contours and cuts directly into the column course. It reads as a
stylized cavern ruin; an organic wilderness cave would need further authored
irregularity and dressing. This is useful art-direction evidence, not finished
cave art.

| Final Balanced state | Triangles | Vertices | Observed construction |
| --- | ---: | ---: | ---: |
| Normal carvings | 75,657 | 52,605 | 0.607 s |
| Fine carvings | 80,995 | 56,323 | 0.658 s |

Both use 119 parts. These are individual local synchronous generation
observations, not frame-rate measurements. Extra carving detail changes the
mesh, but the Normal/Fine visual difference is small at the recorded cameras.
The software renderer's 320×180 backing limits fine-feature and bronze-material
judgment. Normal is a useful starting point for this larger-scale design.

Recorded session `rusty-craftsurvive-playtest-20260907T042848.630965555Z-1701294`
uses the stated Engine pair. Selected indexed images and per-part readouts are
in `docs/evidence/stepped-cave`. The short canvas-focused W/S roundtrip moved
through a visually continuous central floor and returned to its starting
framing. It did not contact obstacles, so it does not certify every collision
surface or terrace traversal. No obvious holes or intersections were observed
in the bounded views. The older moving-platform demo remains outside this
study's ownership and can appear beyond the cave exit.

The tester's first W press with a UI button focused did not move. A canvas click
established pointer lock and subsequent W/S worked. Synthetic Escape did not
release the lock in this run; `document.exitPointerLock()` browser assistance
was used to resume UI comparisons. The timeout and advisory missing sequence
fields remain in session evidence. No launcher/input infrastructure was changed.

Full final Engine diagnostics recorded zero errors and six warning events
around live source restaging/rebinding; readiness recovered before the final
comparisons. No diagnostic loss or lag was reported. The warning capture is
report-only because no compatible baseline was supplied, so this is not a
zero-warning delta claim. These diagnostics and the exact capture map are
retained with the study.
