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

Combined level and voxel-art experiments are described in
The procedural cave levels. CraftSurvive is now the active
home for the retained Rusty Procgen algorithms and offline artifact tool.

The near-spawn [rope playground](docs/rope-playground.md) explores Engine-owned
tethers, character swing coupling, dynamic reactions and a short articulated rope.

## Repository shape

```text
src/
  CraftSurvive.Game/          checked C# product domains and SDK metadata
  ui/                         DOM-only companion source
content/                      canonical product content and provenance
docs/                         current ownership and limitations
Directory.Build.props         the one Engine SDK/runtime pair pin
```

The SDK creates its CoreCLR and NativeAOT composition below ignored `obj/`.
Those generated artifacts are not checked projects and must not be edited.
Current `content/animations/` and `content/voxels/` assets are preserved for
future product work.

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
  product tuning, moving-platform policy, world-position tracking, origin
  rebasing, camera composition, and player UI facts. Engine performs the
  collision, character, camera, appearance, and origin mechanisms.
- `Modules/Sky` selects the canonical authored panorama through Engine
  Appearance and CameraView. Engine owns its resource and renderer lifecycle.
- `Modules/Terrain/CourtyardScene` retains generated geometry and copied
  collision for the default Stoneworks environment, Reference courtyard and
  Sampling plaques. The SDK's `Rusty.Engine.Implicit` vocabulary separates local frames,
  openings, layered materials and field composition from that runtime owner.
  The recipe study that documented those controls is in Den (`history/stoneworks-recipes`).
- `src/ui/main.ts` mounts DOM guidance, Ghost Settings, and live diagnostics beside the Engine-owned canvas.

This is a runnable continuation lane, not a claim of complete survival
gameplay or broad interactive certification. See
[`docs/known-limitations.md`](docs/known-limitations.md) for the deliberately
bounded product surface.

## Scenes and the authoring lane

The boot scene is chosen in exactly one place, `TerrainSceneSelection.Default`
in `Modules/Terrain/TerrainConfiguration.cs`: the adventurer's generated cubic
world, which became the default in S2 of campaign #8595. It has the settled block
floor, water bodies, surface features, an authored bedrock border, and the finite
extent of about 105 km2 (10,240 voxels per side).

Every other scene is reached explicitly with `CRAFTSURVIVE_SCENE`:

- `courtyard` — the authored Stoneworks/Reference study. This is the authoring
  lane's scene: fixed geometry for evaluating materials, masonry, plaza and
  stair work.
- `traversal` — the generated cubic world, which is also the default. It is the
  target for generation, residency, edits and the live proofs in
  [live-proofs.md](docs/live-proofs.md).

The authoring lane is exercised by one documented flow — the study scene plus the
focused lanes that cover its recipe and artifacts:

```sh
CRAFTSURVIVE_SCENE=courtyard rusty dev \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --live-debug --debugger --bind-host 127.0.0.1 --port 37300
dotnet run --project tests/Workbench -c Release    # level plans and authored recipes
dotnet run --project tests/Procgen -c Release      # generator and artifact checks
pnpm run audit:textures                            # authored texture hashes
```

The Courtyard/Stoneworks studies, the procgen workbench, the level plans, and the
offline artifact bank are the **authoring lane**: they are how dungeons and set
pieces get made for the RPG, and they are expected to keep working as such. They
are not the game's world, and the generator no longer carries hand-placed testbed
furniture — the old traversal route, clearing, gaps, trench, bridge and pillars
were removed from the world recipe rather than inherited by the world model.

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

## Ghost Plate comparison

Open **Ghost Settings** without capturing the mouse. **Front / Right / Back /
Left** move the ordinary C# player to comparison positions on the existing flat
showcase pad. Close the panel and use WASD to circle the wizard. Direction is
selected by camera position around the plate; turning in place keeps the sector.
The panel shows the Engine-selected sector, local azimuth, configured/retained
counts, source match, and fallback.

**Use preset** loads the selected C# preset. **Apply settings** sends the editable
fields, **Reset to accepted** restores that preset, and **Recapture** freezes a
new source capture. **Use observed values** replaces a draft with the current C#
readout; background reads preserve unsaved input. Visibility, relief, direction,
capture framing/lighting, placement, and size all use the packaged Engine debug
client and existing `craft.ghost.*` commands.

The runtime courtyard controls and treatment comparison stay in the product; the
study records that documented them, and every other retired experiment, are kept in
Den rather than here. See [docs/index.md](docs/index.md) for the map.
