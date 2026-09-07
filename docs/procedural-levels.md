# Procedural cave levels

CraftSurvive now combines level and aesthetic experiments. Select **Generated
cave**, choose seed **11**, **29** or **47**, then **Entry** and walk in. The
**Cave layout** study retains the same rooms/routes without rock courses or
material bands. Existing detail and normal-treatment controls still apply.
The other voxel studies remain available. The inherited moving platform is hidden
and excluded from character obstacles in these two studies; older traversal and
art studies retain their existing behavior.

## Ownership and stages

- `CraftSurvive.Procgen` is a pure local C# library migrated from Rusty Procgen:
  graph/progression, canonical identity, generation/catalog policy and bounded
  workloads. The retained offline Artifacts/Tool projects have no game host.
- `CaveLevelPlan` owns the product's six-room layout and seven routes, with two
  undirected loops. Engine keyed random draws choose room dimensions, positions,
  bends and the cache approach. It has no Engine handles or field evaluator.
- `GeneratedCaveRecipe.Carve` turns room ellipsoids and route polylines into air,
  clipped within a bounded rock solid. The floor is at three metres and a single
  explicit entrance bypasses the protected shell. Chambers deliberately extend
  below the floor before clipping, producing a level walkable floor.
- `Treat` adds broad recessed rock courses; material regions add pale strata and
  a moss floor. Treatment never changes the intended graph or random draws.
- The existing Engine implicit services own all field evaluation and DC
  extraction. The existing retained resource and Spatial collision paths consume
  the result. No new Engine service, duplicated evaluator, renderer or navigation
  state was needed for this slice. Layout/gameplay policy stays downstream;
  future reusable geometric mechanisms should extend those upstream owners.

This consolidates active experimentation, not every old workbench UI feature.
Procgen's separate Product, old pages and historical fixture corpus are preserved
in that repository as donor history. Do not maintain parallel algorithm copies.
See `src/CraftSurvive.Procgen/README.md` for the donor snapshot.

## Checks and inspection

```bash
dotnet run --project tests/Procgen -c Release
dotnet run --project src/CraftSurvive.Procgen.Tool -c Release -- --self-check
dotnet run --project tests/TerrainResidency -c Release
pnpm run check:ui
dotnet build src/CraftSurvive.Game -c Release
```

Live debug commands use the ordinary Engine development host:

```text
craft.courtyard.study level
craft.courtyard.seed 11
craft.courtyard.inspect level-entry
craft.courtyard.level-plan
craft.courtyard.readout
craft.courtyard.parts
```

Room views are `level-hub`, `level-left`, `level-right`, `level-goal`; `level-roof`
inspects the outside. Views place the ordinary player and are not evidence of
walking a route. `level-plan` reports accepted local room and passage coordinates.
Readout distinguishes logical graph/loop counts, source-field body-clearance
witnesses and extracted mesh diagnostics. Clearance checks use 1,008 points around
route body footprints; they are not exhaustive connectivity, mesh collision,
self-intersection or navigation guarantees.

## Limits

This is bounded procedural layout and carving, not hydraulic erosion. The two
loops describe intended routes; meshing and decorative recesses can alter the
physical shape. This slice intentionally has a flat floor; vertical room volume
varies, but multi-level routes and slopes are future level-authoring work.
Adaptive DC still has known small cut notches documented in `voxel-foundation.md`.
Detail settings are not a performance budget or a promise that arbitrary large
volumes fit in a single mesh.

GPU playtest evidence and measured seed results are recorded under
`docs/evidence/procedural-levels/`. The GPU harness's capture path does not certify
browser-console warning deltas, audio, measured frame freshness or frame pacing.

## Erosion-like surface studies

`level-weathered` replaces the regular carved courses with seeded multiscale
irregularity; `level-weathered-strata` adds that irregularity to the courses.
Both retain the same room/route plan, protected floor and enclosing shell.
The Courtyard panel exposes both studies alongside the baseline and bare layout.

The Engine's `ImplicitRecipe.DisplaceWaves` evaluates continuous normalized
spectral noise. This is a finite wave sum with octave frequency/amplitude
controls, not Perlin fBm, water transport, sediment deposition or actual erosion.
The recipe combines broad anisotropic variation (four octaves) with finer
roughness (two octaves), then clips the air to the protected interior. Mineral
bands receive a separate gentle perturbation. The final geometry feeds the same
DC extraction, material splitting and collision path as the baseline.

Tune `GeneratedCaveRecipe.Weather` for field amplitudes, coordinate frequencies,
lacunarity and gain. Amplitudes are field-value units, not metres: normalized
ellipsoids and distance-valued capsules respond differently. This first test
keeps floor height fixed and checks existing route body witnesses; it does not
simulate floor erosion or certify all possible navigation. The initial Fine trial exceeded the old 16 MiB encoded mesh-resource budget.
Engine #7868 now aligns mesh admission at 64 MiB and complete retained-baseline
delivery at 256 MiB. These are Engine policies, not Three.js limits. Packed
streams and their larger inline JSON encoding are each budgeted separately;
undersampled features can still disappear.

The local authoring manifests currently use `rusty dev --debugger` so deliberate
fine-mesh generation may run beyond the normal five-second callback deadline.
This disables worker startup/callback deadlines; it is an explicit development
setting, not asynchronous generation. Regeneration still blocks the product
callback. Remove that flag to restore normal deadline/recovery behavior. The
normal-detail weathered presets were also exercised under the ordinary deadline.

The weathered recipes explicitly request extraction budgets of 1,048,576
vertices and triangles. Those apply before attribute/material splitting and do
not override renderer resource-byte limits. See [the evidence report](evidence/weathered-caves/README.md).

## Strong structural disruption

**Disrupted cave** (`level-disrupted`) removes the regular courses and combines
five independently seeded displacement bands: chamber-scale bulges, medium
lobes, smaller recesses, roughness, and fine grain. The bands use 15 octaves in
total. Their amplitudes are 1.15, 0.65, 0.30, 0.12 and 0.035 field units; base
frequencies range from 0.035 to 1.7 cycles per coordinate unit. These are explicitly
much stronger shape changes than the weathered studies, not a simulated water
or sediment process. Tune `GeneratedCaveRecipe.DisruptionBands`.

A narrow 1.25-metre-radius passage is unioned back along each intended route
after disruption. This protects the walking core while allowing the rest of the
chamber to contract, expand and merge. The existing enclosure clips the result;
the floor remains flat. The plan describes intended connections, not an exact
guarantee that no additional connection has emerged. The same clearance and
mesh diagnostics remain visible; strong fine-scale displacement can expose
non-manifold edges and undersampling. Do not interpret zero boundary edges as
proof of a defect-free surface.
