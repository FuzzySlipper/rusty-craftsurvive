# CraftSurvive C# migration map

## Purpose and lane

CraftSurvive is becoming a C# product hosted by Rusty Engine's generated
NativeAOT boundary. This map treats the existing Rust and TypeScript code as
semantic donor material, not an implementation that C# must mirror. The C# map
starts deliberately small so later gameplay slices can choose clear domain
owners instead of inheriting a host-shaped file layout.

```text
CraftSurvive.Game          safe product state, terrain policy, player rules
CraftSurvive.NativeProduct generated NativeAOT composition only
Rusty.Engine               lifecycle, input, spatial, rendering, persistence
src/ui/main.ts             DOM-only static companion UI
```

The Engine owns canvas, renderer resources, retained-frame implementation, and
browser transport. No C# or TypeScript migration work should recreate them.

## Current starting point

| Area | Donor location | C# destination | Status |
| --- | --- | --- | --- |
| Product lifecycle | `src/main.rs`, `src/session.rs` | `CraftSurvive.Game/CraftSurviveProduct.cs` | Minimal lifecycle shell exists; no gameplay has moved. |
| NativeAOT bootstrap | none in CraftSurvive | `CraftSurvive.NativeProduct` | Generator-selected product; no handwritten ABI. |
| Browser host | `src/bin/browser-host.rs`, `web/src/main.ts` | Engine `csharp-product-runtime` plus generated bundle | Replaced by the standard host path. |
| DOM shell/HUD | `web/src/game-ui.ts` | `src/ui/main.ts` | Static DOM guidance exists; a later slice can use the admitted Engine UI projection rather than recreate gameplay state in the browser. |
| Terrain recipe and bounds | `src/config.rs`, `src/island.rs`, `src/world.rs` | `CraftSurvive.Game/Modules/Terrain` | Safe C# generation-v2 recipe, chunks, bounded residency policy, canonical overlay state, and atomic edit admission are active. |
| Voxel residency, edit, and ray target | `src/world.rs` | `CraftSurvive.Game/Modules/Terrain/TerrainWorld.cs` | Active through generated Spatial/Voxel sessions, revision-checked residency/edit transactions, leases, Engine pick facts, and product admission decisions. Player supplies the typed overlap fact at the edit boundary. |
| Player movement, look, camera, rebase, and moving platform policy | `src/player.rs`, `src/session.rs` | `CraftSurvive.Game/Modules/Player` | Active through product-owned input/pose policy and generated Look, Spatial character (including one call-local platform obstacle), WorldOrigin, CameraView, and Appearance services. Terrain retains voxel residency, edits, presentation, and UI-stream ownership. |
| Save overlay policy | `src/save.rs` | `CraftSurvive.Game/Modules/Terrain` | One-owner bounded canonical binary overlay uses generated Engine persistence storage. |
| Terrain presentation | `src/projection.rs`, `src/terrain_texture.rs` | `CraftSurvive.Game/Modules/Terrain/TerrainWorld.cs` | Active through Engine-owned voxel scene projection and three material bindings; C# retains no mesh or renderer implementation. |
| Sky, sprite, debris, animation labs | `src/projection.rs`, `src/sky_background.rs`, `src/sprite_scene.rs`, `web/src/*garden.ts` | none yet | Still retained donor/experiment material; not part of the terrain C# slice. |

## Intended migration order

1. Establish and keep this generated product/bootstrap/host lane buildable.
2. Port the deterministic terrain recipe and owned world/edit policy into a
   `Terrain` domain after the voxel capability slice is explicitly confirmed.
3. Port the `Player` domain through Engine input, first-person look, character
   controller, ray target, and camera mechanisms.
4. Connect terrain residency, accepted edits, persistence overlays, and
   Engine-owned terrain presentation through named generated services.
5. Add product-facing DOM UI only for facts the Engine can project; then retire
   the donor runtime deliberately once the C# path reaches a useful interactive
   continuation state.

Each step may stop for an upstream capability task. A missing service is not a
reason to reintroduce downstream Rust, an unsafe native call, a browser
renderer, or a custom transport.

## Donor boundaries

The useful semantic donor is the normal CraftSurvive terrain/player/edit loop.
The old native host and non-UI TypeScript presentation experiments are not
structural donors. Existing browser smokes, proof scripts, and detailed
certification text are historical evidence only; they are not acceptance gates
for this migration.
