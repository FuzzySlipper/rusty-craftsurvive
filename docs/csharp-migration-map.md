# CraftSurvive C# migration map

## Current lane

CraftSurvive is now a C# product hosted by Rusty Engine's generated
NativeAOT boundary. The C# product owns application state and gameplay
meaning; Engine owns reusable mechanisms. The earlier Rust session and
browser runtime were retired in task 7497. Their exact source and experiment
assets are recoverable from Git history, but are not alternate implementation
targets.

```text
CraftSurvive.Game          safe C# product state and gameplay domains
CraftSurvive.NativeProduct thin generated NativeAOT composition
Rusty.Engine               lifecycle, input, spatial, rendering, persistence
src/ui/main.ts             DOM-only companion UI
content/textures/          canonical terrain content and metadata
```

The Engine owns the canvas, renderer resources, retained frame construction,
browser host, and browser transport. Product C# publishes facts through named
Engine services; neither C# nor TypeScript recreates those mechanisms.

## Landed ownership

| Area | Current owner | Status |
| --- | --- | --- |
| Product lifecycle and bootstrap | `CraftSurviveProduct`, `CraftSurvive.NativeProduct` | Active. The Engine runtime discovers the product through the generated bootstrap. |
| Browser host and canvas | Rusty Engine `csharp-product-runtime` and generated product-browser-host | Active. `scripts/run-csharp.sh` is the direct runner and `.den-serve.json` is the normal Den manifest. |
| DOM companion | `src/ui/main.ts` | Active. Static status only; it does not own game facts or input meaning. |
| Terrain recipe and bounds | `Modules/Terrain` | Active. Deterministic generation-v2 recipe, 16³ chunks, bounded residency, and named product tuning. |
| Voxel residency and edits | `TerrainWorld` plus Engine Voxel/Spatial services | Active. Product admits revision-checked edits and leases; Engine owns voxel scene and collision/presentation mechanisms. |
| Terrain persistence | `TerrainOverlayState` and `TerrainOverlayCodec` plus Engine Persistence | Active. One bounded canonical overlay owner. |
| Terrain presentation | `Modules/Terrain/TerrainAtlasCatalog`, `TerrainWorld`, Engine AuthoredContent/Appearance/VoxelScenePresentation | Active. C# selects the canonical atlas and source-slot/face policy; Engine admits resources, resolves materials, retains mesh/renderer handles, and returns copied mapping rows. |
| Sky background | `Modules/Sky` plus Engine Appearance/CameraView | Active. C# selects and admits the canonical panorama; Engine retains, realizes, republishes, and clears it. |
| Player input and look | `PlayerInputState`, `PlayerController` plus Engine Input/Look | Active. Product maps admitted input into movement/edit policy and Engine integrates look. |
| Character movement and support | `PlayerController` plus Engine Character/Spatial | Active. Product owns cadence, tuning, platform policy, and global position; Engine owns collision and motion response. |
| Camera and origin | `PlayerController` plus Engine CameraView/WorldOrigin | Active. Product composes camera policy and rebasing decisions; Engine performs the mechanism. |
| Historical Rust/browser experiments | Git history | Retired. No Cargo crate, bespoke session transport, browser renderer, garden, or smoke lane remains in the checkout. |

## Product/Engine boundary

Ordinary product code references the generated safe `Rusty.Engine` surface.
The NativeAOT composition project is the only C# project that opts into the
generated unsafe bootstrap. Add a missing mechanism upstream as a coherent
Engine service family; do not add handwritten P/Invoke, JSON dispatch, a
second renderer, or a downstream substitute.

## Deliberate next areas

The current lane is a bounded terrain/player continuation rather than a full
survival game. Inventory, crafting, resource interactions beyond the admitted
terrain atlas, animation, networking, and broader world simulation remain product work. A
future slice should first name its C# owner and required Engine mechanisms,
then stop and file an upstream task if a named capability is absent.

See [`known-limitations.md`](known-limitations.md) for current behavioral
limits and [`donor-provenance.md`](donor-provenance.md) for the retained
semantic sources.
