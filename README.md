# Rusty CraftSurvive

Rusty CraftSurvive is a C# game product developed with the installed
`Rusty.Engine` SDK and paired runtime pack. The ordinary development and Den
lane is `rusty dev`, which stages and loads the CoreCLR product. NativeAOT is
available only as an explicit fidelity/release verification.

> The product decides. The Engine guarantees.

C# owns game rules, terrain recipe, player policy, edits, persistence meaning,
and product state. Rusty Engine supplies lifecycle, input, camera and character
mechanisms, voxel residency and presentation, resources, and persistence
primitives through the safe SDK. The small DOM companion is UI-only; it does
not render game elements, retain game state, or implement a transport or loop.

The repository holds the game only. The earlier authoring and study lanes (the procedural
dungeon workbench and its offline tool, the courtyard and stoneworks studies, the ghost
plate, microvoxel and rope studies, and the live substrate proof) left the working tree;
Den's `history/authoring-lane` names the last commit that holds them and where each went.

## Repository shape

```text
src/
  CraftSurvive.Game/          checked C# product domains and SDK metadata
  ui/                         DOM-only companion source
content/game/                 the content the product loads (staged whole)
content/                      authoring sources and preserved assets (not staged)
tests/                        managed check lanes and their shared harness (not product projects)
scripts/                      UI build and checks, the texture audit, the live-lane client
docs/                         current ownership, limitations and live proofs
Directory.Build.props         the one Engine SDK/runtime pair pin
```

The SDK creates its CoreCLR and NativeAOT composition below ignored `obj/`.
Those generated artifacts are not checked projects and must not be edited.
Only `content/game/` is staged into the product. `content/animations/` and
`content/voxels/` are preserved user-owned assets that the product does not load.

## Develop or use the Den service

Install the UI dependency once:

```bash
pnpm install --frozen-lockfile
```

The Engine's `rusty` command installs and runs the pinned pair. Get it once
with the Engine bootstrap
(`curl -fsSL https://raw.githubusercontent.com/FuzzySlipper/rusty-engine/main/scripts/install-rusty.sh | bash`),
then install the pin and start a standalone development session:

```bash
rusty install
rusty dev \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --live-debug --bind-host 127.0.0.1 --port <port>
```

Open the printed address in a browser and click the world to give it keyboard focus.
`--live-debug` enables the debug command channel that the developer tools and the live-lane
client use; the game and its UI work without it.

Den uses the same command through `.den-serve.json`. When a broker-owned
session is already live, inspect or use that owner rather than launching a
second process.

`<RustyEnginePackageVersion>` in `Directory.Build.props` is the one pin, read by
the product project, the focused managed checks and CI; `rusty status` shows it
and whether it is installed. Move it only with `rusty update`, which lists the
release notes to read. `Directory.Build.props` also declares the pair's feed, so
plain `dotnet run` of the focused checks resolves exactly the pinned SDK.

Engine contributors can opt into a source build only with an explicit Engine
source path. `rusty dev --engine-source` supplies the matching MSBuild override
properties automatically:

```bash
rusty dev \
  --engine-source /absolute/path/to/rusty-engine \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj

dotnet build src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  -p:RustyEngineUseSourceDevelopment=true \
  -p:RustyEngineSourceDevelopmentPath=/absolute/path/to/rusty-engine
```

Ordinary product work must not set those overrides or depend on checkout
location.

## Current product slice

- `Modules/WorldGen` and `Modules/Terrain`: the versioned generator, residency around the
  player, a fingerprinted chunk cache, bounded edits admitted before the Engine is asked, the
  player's edit overlay and its save, and voxel presentation.
- `Modules/Player`: input, look, movement, the camera, origin rebasing, vitals, progress and
  the continuation save; the Engine performs character, camera and origin mechanisms.
- `Modules/Creatures` and `Modules/Rpg`: creatures that spawn, see, chase, strike and are
  defeated, under pure combat, loot, progression and encounter rules.
- `Modules/Discovery`: points of interest noticed and reached, kept in a saved journal.
- `Modules/Manipulation` and `Modules/Actions`: blast charges with dust, build stamps and block
  entities (doors, lights, containers), reached from the UI's action bar and debug commands.
- `Modules/Content`: the block registry and the terrain atlas layout, checked against each other.
- `Modules/Sky`: the world's clock, and the day and night panoramas and lights that show it.
- `Modules/Survival`: the world's conditions (time of day, difficulty) and the player's hunger and air.
- `Modules/Inventory`: what the player carries, the recipes, and the caches places hold.
- `Modules/Dungeons`: going into a dungeon - its own finite space, loaded whole - and coming out.
- `Modules/World`: the save manifest and store, the UI projection, and the world frame.
- `Modules/Debugging`: debug-command adapters over the owners, and the Engine's playtest commands.
- `src/ui`: a HUD over the product's projection and an action bar that claims its intent.

This is a bounded survival slice, not a claim of complete survival gameplay. See
[`docs/known-limitations.md`](docs/known-limitations.md) for its limits and
[`docs/csharp-migration-map.md`](docs/csharp-migration-map.md) for who owns what.

## The world

The product has one scene: the adventurer's generated cubic world, configured in
`Modules/Terrain/TerrainConfiguration.cs`. It has the settled block floor, water bodies,
surface features, points of interest and crossings, an authored bedrock border, and a
finite extent of about 105 km2 (10,240 voxels per side). The generator lives in
`Modules/WorldGen`; its output is pinned per version by a golden fingerprint (see
`tests/TerrainResidency` and `Modules/Terrain/TerrainGenerationGoldens.cs`).

## Working on the product

Read [`AGENTS.md`](AGENTS.md) and
[`docs/csharp-migration-map.md`](docs/csharp-migration-map.md) before changing
the product/Engine boundary. Use the generated safe `Rusty.Engine` API in
ordinary product code. Do not add downstream Rust, handwritten P/Invoke,
unsafe game code, a second renderer or loop, UI-owned gameplay state, or a
custom JSON/WebSocket bridge.

If a needed mechanism is absent from the SDK, record the exact upstream Engine
capability and stop that slice. A missing capability is a valid result; a
downstream substitute is not.

Focused normal-lane checks:

```bash
pnpm run check:ui
pnpm run audit:textures
dotnet build src/CraftSurvive.Game/CraftSurvive.Game.csproj --configuration Release
dotnet run --project tests/<lane> -c Release   # TerrainResidency, RpgCore, DiscoveryCore, SaveCore, SubstrateProof
```

Each lane reports every failed check, not just the first. `tests/SubstrateProof` is an Engine
canary: it links no product code. Against a running host, `scripts/live.mjs` walks the player,
checks discovery and runs debug commands; see [`docs/live-proofs.md`](docs/live-proofs.md).

Run NativeAOT verification only when the task needs fidelity/release evidence:

```bash
rusty build --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj --aot
```

For the deferred owner-machine pacing investigation, the capture recipe is in Den
(`history/performance-investigation`).
