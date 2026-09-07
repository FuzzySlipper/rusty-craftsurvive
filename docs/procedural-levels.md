# Procedural cave levels

CraftSurvive now combines level and aesthetic experiments. Select **Generated
cave**, choose seed **11**, **29** or **47**, then **Entry** and walk in. The
**Cave layout** study retains the same rooms/routes without rock courses or
material bands. Existing detail and normal-treatment controls still apply.
The other voxel studies remain available.

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
