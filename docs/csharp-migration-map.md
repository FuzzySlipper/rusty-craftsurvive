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

The product has one scene, the generated cubic world configured in
`Modules/Terrain/TerrainConfiguration.cs`. The Engine host owns canvas, renderer resources,
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
| Generation | `Modules/WorldGen`: `TerrainRecipe`, the contract, point-of-interest and crossing placement and structures | Keyed random draws |
| Terrain | `Modules/Terrain`: `TerrainWorld` over the residency streamer, the edit service and transaction, the overlay and its store, the chunk cache and presentation | Spatial sessions, voxel residency, edits and scene reads, authored content, directional voxel presentation |
| Blocks and atlas | `Modules/Content`: `BlockRegistry`, `TerrainAtlasLayout` | Authored materials |
| Player | `Modules/Player`: `PlayerController` over input, camera, water and climb probes, body, origin rebasing, vitals, progress and continuation | Character controller (walking, swimming, climbing), look, camera view, world origin |
| RPG rules | `Modules/Rpg`: combat, loot, progression, encounters, creature kinds, character sheet | none (pure rules) |
| Creatures | `Modules/Creatures`: `CreatureModule` over the roster, simulation, spawn plan and presentation | Perception, appearance, entity store; collision navigation while pursuing |
| Discovery | `Modules/Discovery`: `DiscoveryModule` and the journal | Persistence through its save slot |
| Manipulation | `Modules/Manipulation`: blast charges and dust, build stamps, block entities and their store | Voxel edits through `TerrainWorld`, particles |
| UI actions | `Modules/Actions`: `PlayerAction` (the payload) and `PlayerActionModule` | Input intents (product payload) |
| Sky | `Modules/Sky`: `WorldClock` (rules) and `DayNightSky` (panorama blend, sun and ambient lights) | Camera view sky blend, retained lights |
| World conditions | `Modules/Survival`: `WorldConditionsModule` (time of day and difficulty, saved) | Persistence |
| Inventory | `Modules/Inventory`: `InventoryModule` over `ItemCatalog`, `Recipes`, `SupplyCache` (saved); takes creature drops and first-visit caches, crafts as one edit | Mechanics `InventoryStore` and `InventoryEdit`, persistence |
| Survival | `Modules/Survival`: `SurvivalModule` over `SurvivalRules` (hunger, air, recovery; saved), acting through the player's vitals | Character controller submersion fact, persistence |
| Diagnostics | `Modules/Debugging` plus each module's `*DebugModule`: debug adapters over owners, and the Engine's playtest commands | Debug command catalog |

## Product/Engine boundary

Ordinary product code references the safe `Rusty.Engine` SDK surface. The SDK generates
ABI-sensitive implementation only below ignored `obj/`. Add a missing mechanism upstream as a
coherent Engine service family; do not add handwritten P/Invoke, JSON dispatch, a second
renderer, or a downstream substitute.

## Next areas

Campaign #8595 in Den holds the slices still to come - HUD and screens (S9, #8605), dimensions
and authored interiors, crafting and inventory, and performance budgets (S10, #8606). A slice
first names its C# owner and the Engine mechanisms it needs, and stops to file an upstream task
if a named capability is absent. See [`known-limitations.md`](known-limitations.md) for current
behavioural limits.
