# Runtime courtyard experiment

`CRAFTSURVIVE_SCENE=courtyard` selects the normal scene (and is the default).
`CRAFTSURVIVE_SCENE=traversal` selects the retained voxel showcase; any other
value fails product creation. `TerrainWorld` captures this startup choice once.
`CourtyardScene` owns the new layout recipe and calls the packaged Engine's
general `ImplicitSurfaces` service. It does not evaluate fields or mesh in C#.
The current paired runtime and SDK revision is `b9740421c2d2`.

The route crosses a 24×20m ruined courtyard, climbs six steps through a framed
opening, follows a 12m covered passage, and enters a 12×10m open chamber with
an altar and clipped columns. Masonry courses, caps, sparse chips, rocks,
roots, moss and material choices are runtime recipe data. Collision copies
these generated Engine meshes into the existing Spatial session. This scene
uses explicit whole-scene regeneration; the old voxel edit tools apply only
to the retained voxel scene mode. The visible 45° stair-nose bevels are part
of those same generated collision meshes. They work around the hard vertical
riser solver limitation tracked by Engine #7831; that limitation is not fixed.

Normal first-person controls remain WASD, mouse look, Space to jump, Shift to
sprint and Control to crouch. The generated debug panel exposes:

- `craft.courtyard.readout`: generation count, dimensions, selected treatment,
  total mesh vertices/triangles, generation time, and correction counts.
- `craft.courtyard.treatment`: `balanced`, `faceted`, or `soft`.
- `craft.courtyard.masonry`: `original`, `regions`, or `layered` for the west-wall test bay.
- `craft.courtyard.inspect`: `front` or `grazing` views that follow the current wall width.
- `craft.courtyard.layout`: width (20–30m), doorway width (2.4–4.2m), doorway
  offset (−0.3–0.3m), and seed. These queue regeneration for a normal update.
- `craft.courtyard.view`: eye XYZ and target XYZ for repeatable comparisons,
  using the ordinary player/camera owner.
- `craft.courtyard.shadows`: queues directional shadow intent for an optional
  controlled lighting comparison.

The materials are six deterministic 32×32 pixel textures with nearest/repeat
sampling selected explicitly when their resources open, plus a dark neutral
material. `scripts/generate-courtyard-textures.py` records their authored
source. Texture UV scale remains independent of mesh sampling. The Engine owns
crease normals, planar UV charts, material regions, resource lifetimes,
collision and presentation recovery. A replacement publishes its new snapshot
before retired resources release; teardown clears that snapshot before disposal.
The committed replacement baseline permits a full replacement up to 64 MiB,
while existing per-mesh bounds remain unchanged. The former dense 0.16m
setting is not current: a soft-to-dense regeneration exceeded the host's
five-second operation timeout. This is the long-operation limit tracked by
Engine #7833, not a solver change.

The compact DOM chrome starts with diagnostics collapsed. **Show metrics**
uses the Engine renderer show/hide commands. The collapsed **Courtyard** panel
offers shading presets, three masonry constructions, front/grazing inspection
views, and Refresh. Its regeneration receipts mean queued, not applied; Refresh
reads the C# generation state. Layered masonry is the default in the four-metre
west-wall test bay only; see [the comparison](layered-masonry-test.md). The optional
shadow command remains available through the generic debug surface.

Before the layered-bay test, `b9740421c2d2` observations with original masonry were:

- `balanced` at 0.22m / 38°: 205,336 triangles, 177,393 vertices, about
  0.737 s.
- `faceted` at 0.26m / 0°: 199,924 triangles, 337,327 vertices, 0.658 s.
- `soft` at 0.20m / 110°: 211,468 triangles, 149,370 vertices, 0.603 s.
- `craft.courtyard.layout 26 3.8 0.25 12345`: generation 4 with `soft`, 67
  parts (rather than the default 65), 216,176 triangles, 153,052 vertices,
  and 0.627 s. It completed in the same runtime without hand repair.

The changed Soft and Balanced layouts were walked with physical W-only input
through the visible steps, passage and chamber. Soft is selected as the default;
see [the result and evidence](courtyard-verdict.md). Browser evidence uses software
SwiftShader at 320×180 in a 1280×720 viewport; that ceiling limits pacing,
hardware, and aesthetic assessment.

The initial coarse voxel scaffold was replaced by the generated surface route;
it is not a second collision or layout authority. Final visual and authoring
acceptance is tracked by Den campaign #7823 and tasks #7828–#7830.
