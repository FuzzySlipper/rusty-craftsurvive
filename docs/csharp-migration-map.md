# CraftSurvive C# product map

## Current lane

CraftSurvive is one ordinary C# product project developed through the installed
`Rusty.Engine` SDK. The Engine's `rusty dev` command, on the pair pinned in
`Directory.Build.props`, stages and loads its CoreCLR bundle for both local development and
Den. The SDK owns the generated composition below `obj/`; NativeAOT is an explicit
fidelity/release check (`rusty build --project ... --aot`), never a checked product project or
normal host.

```text
src/CraftSurvive.Game      the one product project: game rules, state and SDK metadata
src/ui                     DOM companion: a HUD over the projection and an action bar
tests/                     managed check lanes (not product projects) and their shared harness
scripts/                   UI build/check, texture audit, CI serve check, the live-lane client
content/game               the content the product loads
Directory.Build.props      the one Engine SDK/runtime pair pin
```

`WorldCatalog` restores or generates the finite map before composing the
overworld; `TerrainConfiguration.Default` supplies an absent-save starting world.
Dungeons load as separate spaces.
The Engine host owns canvas, renderer resources,
frame construction, input delivery and runtime integration. Product C# publishes product facts
through named SDK services; neither C# nor UI code recreates those mechanisms.

## Installed-pair contract

`Directory.Build.props` pins one exact pair. The Engine `rusty` CLI installs it into its
shared cache and supplies that pair's SDK feed to restores, and `rusty dev` runs that pair's
runtime. Do not mix either with a backup, a package from another feed, or a separately
discovered checkout.

The only contributor exception is deliberate: `rusty dev` receives an absolute
`--engine-source` path and supplies the matching MSBuild properties. A direct MSBuild
invocation instead receives both `RustyEngineUseSourceDevelopment=true` and an absolute
`RustyEngineSourceDevelopmentPath`. The normal lane has no source-override path.

## Ownership

`CraftSurviveProduct` composes the owners below and runs one update in a fixed order: the
player, then the UI's action claims, then creatures, discovery, blast, build and the block-entity
save, then terrain's save, then one appearance snapshot.

| Area | Product owner | Engine services it uses |
| --- | --- | --- |
| Shared product facts | `Modules/World`: `ProductStep`, `WorldFrame` (the one world-to-local conversion), `ProductIds` | World origin |
| Saves | `Modules/World`: `SaveManifest` (every key), `ProductStore` (the one store), `ProductSaveSlot`, `SaveEnvelope`, `SaveRestore`; each owner saves its own key | Persistence |
| UI projection | `Modules/World`: `ProductUiPublisher` and `ProductUiProjection` | UI streams |
| World selection | `Modules/World`: `WorldCatalog`, `WorldMapCodec`, `WorldMapPresentation`; root `WorldSelection` | Persistence, mesh/material resources, camera, input intents and UI streams |
| Generation | `Modules/WorldGen`: `WorldMapGenerator`, immutable `WorldMap`, `TerrainRecipe`, continuous samples in `TerrainDensity`, the contract, point-of-interest and crossing placement and structures | Keyed random draws |
| Terrain | `Modules/Terrain`: `TerrainWorld`, `TerrainSurfaces`, the residency streamer, edit service and transaction, overlay and its store, chunk cache and presentation | Spatial sessions, density-bearing voxel residency, per-material surfaces, edits and scene reads, collision queries, authored content, directional voxel presentation |
| Blocks and atlas | `Modules/Content`: `BlockRegistry`, `TerrainAtlasLayout` | Authored materials |
| Player | `Modules/Player`: `PlayerController` over input, camera, water and climb probes, body, origin rebasing, vitals, progress and continuation | Character controller (walking, swimming, climbing), look, camera view, world origin |
| RPG rules | `Modules/Rpg`: combat, loot, progression, encounters, creature kinds, character sheet | none (pure rules) |
| Creatures | `Modules/Creatures`: `CreatureModule` over the roster, simulation, spawn plan and presentation | Perception, appearance, entity store; collision navigation while pursuing |
| Discovery | `Modules/Discovery`: `DiscoveryModule` and the journal | Persistence through its save slot |
| Manipulation | `Modules/Manipulation`: blast charges, build stamps, block entities and their store, lamps' lights (`LampLights`) | Voxel edits through `TerrainWorld`, retained point lights |
| Feedback | `Modules/Feedback`: `Cues` raised by gameplay, `FeedbackModule` over `BurstEmitter` (`Bursts`); `Modules/Audio`: `SoundPlayer` over `SoundCatalog` and the generated clips | One-shot particle emission, audio clips, voices and buses |
| UI actions | `Modules/Actions`: `PlayerAction` (the payload) and `PlayerActionModule` | Input intents (product payload) |
| Sky | `Modules/Sky`: `WorldClock` (rules) and `DayNightSky` (panorama blend, sun and ambient lights) | Camera view sky blend, retained lights |
| World conditions | `Modules/Survival`: `WorldConditionsModule` (time of day and difficulty, saved) | Persistence |
| Inventory | `Modules/Inventory`: `InventoryModule` over `ItemCatalog`, `Recipes`, `SupplyCache` (saved); takes creature drops and first-visit caches, crafts as one edit | Mechanics `InventoryStore` and `InventoryEdit`, persistence |
| Dungeons | `Modules/Dungeons`: `DungeonModule` (enter, load, leave), `DungeonSpace` (one finite session), `DungeonVolume` and `DungeonLayout` | A second spatial session, voxel residency, voxel projection with the world's materials, retained lights |
| Survival | `Modules/Survival`: `SurvivalModule` over `SurvivalRules` (hunger, air, recovery; saved), acting through the player's vitals | Character controller submersion fact, persistence |
| Diagnostics | `Modules/Debugging` plus each module's `*DebugModule`: debug adapters over owners, and the Engine's playtest commands | Debug command catalog |

## Product/Engine boundary

Ordinary product code references the safe `Rusty.Engine` SDK surface. The SDK generates
ABI-sensitive implementation only below ignored `obj/`. Add a missing mechanism upstream as a
coherent Engine service family; do not add handwritten P/Invoke, JSON dispatch, a second
renderer, or a downstream substitute.

## Extending the product

Use the gameplay and visual direction for design intent and the live Den task for
implementation scope. A slice first names its C# owner and the Engine mechanisms
it needs, and stops to file an upstream task if a named capability is absent.
See [`known-limitations.md`](known-limitations.md) for behavioural limits; an
implemented prototype mechanism is not a requirement to preserve its old genre.

## World map contract

`WorldCatalog` owns the selected world. New-game generation produces an immutable
`WorldMap` before `TerrainWorld` starts; restore reads its saved samples instead
of rerunning generation. The bounded grid has at most 64 segments per axis,
with fewer nodes for tiny test extents. It stores elevation in metres plus unit
fields for temperature, moisture, exposed rock, permitted local detail, candidate
ridge passages, protection, drainage and incision. Seed, extent and generator
version identify the recipe. Base geography establishes a bent ridge, a basin,
a lowered pass and continuous climate relationships. Stable geographic seed
mixing lets later refinement preserve those landforms; the full recipe version
still invalidates development saves and generated caches.

`WorldMapDrainage` builds a downhill receiver forest and accumulates catchments
at map resolution. Every edge node is an outlet; interior local minima remain
closed basins. Equal-height nodes descend by stable index, so flat regions also
terminate. This models static landforms, not flowing water. It does not fill or
breach every depression. One sorted traversal and two ordered passes bound the
work independently of the voxel world size.

The map stores each receiver, catchment size and channel bed alongside its
geographic fields. Downstream-first incision never puts an upstream bed below
its receiver or cuts through a protected downstream pass. The centre reserve,
important ridge crests and ridge pass constrain incision and local relief.
Named constants in `WorldMapDrainage` control catchment thresholds, depth,
climate transitions and cross-section widths.

Coordinates use world X/Z metres, north toward -Z, with the square centred on
the origin. Grid nodes include both edges. Sampling clamps geographic coordinates
at the edge and interpolates broad fields with smoothstep bilinear weights.
It then resolves saved nearby channel segments into narrowed floors and
shoulders, including for density and neighbouring normal samples. Each segment
joins adjacent map nodes and is narrower than half a cell; sampling inspects a
fixed four-by-four neighbourhood, never reruns drainage. Cross-section widths
blend continuously with climate: dry-country shoulders are steeper than cold
or temperate valley sides. Clamping does not extend the playable world:
`TerrainRecipe` retains finite occupancy and the provisional bedrock boundary.
The overview uses a scaled mesh in Engine presentation coordinates, independent
of the rebased local terrain frame; DOM code supplies labels and controls only.

Local density samples map elevation, then adds continuous world-coordinate noise
bounded by `WorldMap.LocalReliefLimit` and the map's detail field. Regional fields
select surface materials and vegetation eligibility. Map samples remain
unchanged by local noise or player edits. Channel floors and fully protected
areas suppress local noise. Eroded shoulders expose rock through the existing
material policy. These constraints preserve the map's intended routes without
claiming a globally navigable route network or hydraulic realism. The authored
landscape studies are separate loaded comparison spaces, not hidden overrides
of geographic sampling.

The map and its configuration share one Engine persistence record. That record
also selects a generation namespace for gameplay saves, so starting again with
the same seed still starts fresh. Each save slot captures its keys at creation;
retiring owners can flush without writing into the new world. The source stamp,
map fingerprint and recipe output identity invalidate generated chunk caches.
A generator-version change discards incompatible development saves.

The root replaces per-world owners on a new-game intent while retaining the
Engine product session, one persistence store and one UI stream. It releases
old appearance, spatial, camera and light handles before installing replacements;
debug adapters follow the active owners. Only nearby voxel chunks are generated
and admitted to the existing bounded residency window. The map is an inspection
view that suspends local updates; its region visit controls are not a travel
simulation or a commitment to unrestricted fast travel.
