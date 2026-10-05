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
| Terrain | `Modules/Terrain`: `TerrainWorld`, `TerrainSurfaces`, `TerrainBlendSettings`, the residency streamer, edit service and transaction, overlay and its store, chunk cache and presentation | Spatial sessions, density-bearing voxel residency, per-material surfaces, aliased terrain layers and their interpolation, edits and scene reads, collision queries, authored content, directional voxel presentation |
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
`WorldMap` before `TerrainWorld` starts; restore reads its saved fields instead of
rerunning generation. Generation never runs on the update thread: a world created
from the map view, and the first world of a fresh or retired store, is simulated
off-thread as pure product computation and admitted on a later update. Startup never
waits on it; until the first world is committed, the product shows the map view's
generating state and holds no terrain, player or gameplay owners. Seed, extent and generator version
identify the recipe. Stable geographic seed mixing lets later refinement preserve
landforms; the full recipe version still invalidates development saves and caches.

`MapGrid` resolves geography at a fixed 32 m node spacing, so a larger world holds
more geography instead of a stretched copy. Only extents beyond 512 segments per axis
coarsen the spacing; tiny test extents keep at least 8 segments. `MapSimulation` is
the one-time modelled stage:

1. `MapRelief` places land, sea and rock from seeded, domain-warped noise in metre
   space: continents, ridged mountain belts gated by a regional activity field,
   rolling uplands, and variable rock hardness. Each border is either open sea or a
   mountain rim; at least one is sea. The arrival area is biased toward calm land.
2. `MapErosion` evolves the landscape under uplift with detachment-limited stream
   power (Braun and Willett's implicit solver), rainfall-weighted discharge, rock
   erodibility and linear creep. `MapFlow` fills closed depressions with a jittered
   epsilon gradient (Priority-Flood+ε) before every routing, so all water reaches
   base level and filled basins stand in for lake sedimentation. Most evolution runs on
   a lattice of twice the spacing; a short refinement at full resolution then
   organises the finer valleys.
3. Heights are scaled so the 99.5th land percentile reaches `MapSimulation.PeakElevation`;
   rarer summits are compressed smoothly below `WorldMap.MaximumElevation`.
   Talus relaxation then limits map-scale slopes to an angle of repose.
4. `MapClimate` derives temperature from a seeded latitude axis and altitude, and
   moisture from a seeded prevailing wind that recharges over the sea and rains out
   as air climbs relief, leaving rain shadows. Rain is spread over a few hundred
   metres and ranked, so every world has wet and dry country. River corridors are
   moister.

The saved record holds five single-precision fields per node: elevation, temperature,
moisture, rock hardness and discharge. Generation routes the rounded heights exactly
as a restore will, so saved discharge always matches rebuilt drainage. Rivers, exposed
rock, the local-detail allowance, the arrival reserve and sites are derived
deterministically from those fields when a `WorldMap` is constructed.

`MapRivers` traces reaches between confluences wherever rain-weighted catchment
reaches one square kilometre, smooths them, and adds a seeded meander and a slower
valley-floor wander. Both fade toward reach ends so confluences and mouths stay
joined. The water surface interpolates filled node heights and never rises
downstream. Width and depth grow with catchment. A spatial bucket index keeps
local river queries bounded.

`MapBiomes` classifies samples into sea, ice field, tundra, boreal forest, cold
steppe, temperate forest, grassland, shrubland, desert, rainforest and alpine
heights. Sites are one representative interior place per environment present.

Coordinates use world X/Z metres, north toward -Z, with the square centred on
the origin. Grid nodes include both edges. Sampling clamps geographic coordinates
at the edge and interpolates node fields with a uniform cubic B-spline. It then
resolves the nearest river: a parabolic channel below its water surface, low soil
banks, and a quieter zone where local relief fades out. The same sample serves
density and neighbouring normal samples. Clamping does not extend the playable world:
`TerrainRecipe` retains finite occupancy and the bedrock boundary, whose wall rises a
fixed height above the ground it stands on. The overview uses a scaled, vertically
exaggerated mesh of biome colours and river nodes in Engine presentation coordinates,
independent of the rebased local terrain frame; DOM code supplies labels and controls only.

Local density samples map elevation, then adds continuous world-coordinate noise
bounded by `WorldMap.LocalReliefLimit` and the map's detail field.
`RegionalTerrain` blends dry shelves/scarps, rolling uplands/outcrops and stretched
frozen ridges using continuous climate weights. Each family has independently
named structural and fine noise banks; wavelengths are in world metres and the
finest lattice remains larger than a voxel. A seeded bearing and smooth domain
warp break grid alignment. A broader activity field leaves quieter patches,
while rock exposure permits more fine relief. The density grid remains one metre;
changing its resolution is separate from tuning those banks.

Regional fields also select surface materials and tree density per biome, and
`TerrainColumn` carries one map sample per column so voxels never resample geography.
Map samples remain unchanged by local noise or player edits. River channels, banks and
the arrival reserve suppress local noise. Steep and resistant ground exposes stone,
submerged ground is a sand bed, and river channels hold water up to their surface. These constraints preserve the map's intended routes without
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
and admitted to the bounded residency window. Each chunk column contributes only the band
around its own generated surface, plus the player's storey and edited chunks, so buried
mountain rock never crowds visible ground out of the window. The map is an inspection
view that suspends local updates; its region visit controls are not a travel
simulation or a commitment to unrestricted fast travel.
