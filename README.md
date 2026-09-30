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
docs/                         current ownership and limitations
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
  --live-debug --bind-host 0.0.0.0 --port 4419
```

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

- `Modules/Terrain` owns deterministic bounded generation, chunk residency,
  revision-checked edits, overlay persistence, Engine voxel presentation, and
  terrain UI projection.
- `Modules/Player` owns input interpretation, look and movement policy,
  product tuning, world-position tracking, origin rebasing, camera composition, and player UI facts. Engine performs the
  collision, character, camera, appearance, and origin mechanisms.
- `Modules/Sky` selects the canonical authored panorama through Engine
  Appearance and CameraView. Engine owns its resource and renderer lifecycle.
- `src/ui/main.ts` mounts DOM guidance, renderer metrics and live diagnostics beside the Engine-owned canvas.

This is a runnable continuation lane, not a claim of complete survival
gameplay or broad interactive certification. See
[`docs/known-limitations.md`](docs/known-limitations.md) for the deliberately
bounded product surface.

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
```

Run NativeAOT verification only when the task needs fidelity/release evidence:

```bash
dotnet msbuild src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  -target:VerifyRustyEngineAot -property:Configuration=Release
```

For the deferred owner-machine pacing investigation, the capture recipe is in Den
(`history/performance-investigation`).
