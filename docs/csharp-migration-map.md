# CraftSurvive C# product map

## Current lane

CraftSurvive is one ordinary C# product project developed through the installed
`Rusty.Engine` SDK. The paired `.runtime/runtime-pack-cabba0f/bin/rusty dev` command
stages and loads its CoreCLR bundle for both local development and Den. The
SDK owns the generated composition below `obj/`; NativeAOT is an explicit
fidelity/release target, never a checked product project or normal host.

```text
CraftSurvive.Game          checked C# product state and gameplay domains
Rusty.Engine SDK           safe services and generated composition/staging
.runtime/runtime-pack-cabba0f/ paired CoreCLR development runtime
.runtime/sdk-feed/         paired Rusty.Engine package feed
src/ui/main.ts             DOM-only companion UI
content/                   canonical terrain, sky, and voxel content
```

The Engine host owns canvas, renderer resources, frame construction, input
delivery, and runtime integration. Product C# publishes product facts through
named SDK services; neither C# nor UI code recreates those mechanisms.

## Installed-pair contract

`NuGet.Config` resolves the exact package version declared by
`CraftSurvive.Game.csproj` from `.runtime/sdk-feed/`. The pinned
`Rusty.Engine.0.1.0-dev.cabba0f.nupkg` and `.runtime/runtime-pack-cabba0f/`
are a single exact installed pair. Do not mix either with a backup, a package
from another feed, or a separately discovered checkout.

The only contributor exception is deliberate: `rusty dev` receives an
absolute `--engine-source` path and supplies the matching MSBuild properties.
A direct MSBuild invocation instead receives both
`RustyEngineUseSourceDevelopment=true` and an absolute
`RustyEngineSourceDevelopmentPath`. The normal lane has no source-override
path.

## Landed ownership

| Area | Current owner | Status |
| --- | --- | --- |
| Product lifecycle and bootstrap | `CraftSurviveProduct` and SDK composition | Active. The SDK emits and stages the CoreCLR product from one explicit product type. |
| Runtime pack and Den host | Packaged `rusty dev` | Active. `.den-serve.json` is broker-owned and starts the same CoreCLR lane. |
| DOM companion | `src/ui/main.ts` | Active. Static product UI only; it does not own game facts or input meaning. |
| Terrain recipe and bounds | `Modules/Terrain` | Active. Deterministic generation-v2 recipe, 16³ chunks, bounded residency, and named product tuning. |
| Voxel residency and edits | `TerrainWorld` plus Engine Voxel/Spatial services | Active. Product admits revision-checked edits and leases; Engine owns voxel scene and collision/presentation mechanisms. |
| Terrain persistence | `TerrainOverlayState` and `TerrainOverlayCodec` plus Engine Persistence | Active. One bounded canonical overlay owner. |
| Terrain presentation | `Modules/Terrain/TerrainAtlasCatalog`, `TerrainWorld`, Engine AuthoredContent/Appearance/VoxelScenePresentation | Active. C# selects the canonical atlas and source-slot/face policy; Engine admits resources, resolves materials, retains mesh/renderer handles, and returns copied mapping rows. |
| Sky background | `Modules/Sky` plus Engine Appearance/CameraView | Active. C# selects and admits the canonical panorama; Engine retains, realizes, republishes, and clears it. |
| Player input and look | `PlayerInputState`, `PlayerController` plus Engine Input/Look | Active. Product maps admitted input into movement/edit policy and Engine integrates look. |
| Character movement and support | `PlayerController` plus Engine Character/Spatial | Active. Product owns cadence, tuning, platform policy, and global position; Engine owns collision and motion response. |
| Camera and origin | `PlayerController` plus Engine CameraView/WorldOrigin | Active. Product composes camera policy and rebasing decisions; Engine performs the mechanism. |

## Product/Engine boundary

Ordinary product code references the safe `Rusty.Engine` SDK surface. The SDK
generates ABI-sensitive implementation only below ignored `obj/`. Add a missing
mechanism upstream as a coherent Engine service family; do not add handwritten
P/Invoke, JSON dispatch, a second renderer, or a downstream substitute.

## Deliberate next areas

The current lane is a bounded terrain/player continuation rather than a full
survival game. Inventory, crafting, resource interactions beyond the admitted
terrain atlas, animation, networking, and broader world simulation remain
product work. A future slice should first name its C# owner and required Engine
mechanisms, then stop and file an upstream task if a named capability is
absent.

See [`known-limitations.md`](known-limitations.md) for current behavioral
limits and [`donor-provenance.md`](donor-provenance.md) for retained semantic
sources.
