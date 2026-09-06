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

## Repository shape

```text
src/
  CraftSurvive.Game/          checked C# product domains and SDK metadata
  ui/                         DOM-only companion source
content/                      canonical product content and provenance
docs/                         current ownership and limitations
.runtime/
  runtime-pack-8058772cbad2/  paired `rusty dev` runtime
  sdk-feed/                   paired Rusty.Engine package feed
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

For a standalone development session, use the installed runtime pack:

```bash
./.runtime/runtime-pack-8058772cbad2/bin/rusty dev \
  --runtime ./.runtime/runtime-pack-8058772cbad2 \
  --project ./src/CraftSurvive.Game/CraftSurvive.Game.csproj \
  --live-debug --bind-host 0.0.0.0 --port 4419
```

Den uses the same command through `.den-serve.json`. When a broker-owned
session is already live, inspect or use that owner rather than launching a
second process.

`.runtime/runtime-pack-8058772cbad2/` and
`.runtime/sdk-feed/Rusty.Engine.0.1.0-dev.8058772cbad2.nupkg` form one installed,
exactly matched pair. Keep the pack, SDK feed, and project version together;
do not select a backup pack or replace only one artifact.

Engine contributors can opt into a source build only with an explicit Engine
source path. `rusty dev --engine-source` supplies the matching MSBuild override
properties automatically:

```bash
./.runtime/runtime-pack-8058772cbad2/bin/rusty dev \
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
- `src/ui/main.ts` mounts DOM guidance, Ghost Settings, and live diagnostics beside the Engine-owned canvas.

This is a runnable continuation lane, not a claim of complete survival
gameplay or broad interactive certification. See
[`docs/known-limitations.md`](docs/known-limitations.md) for the deliberately
bounded product surface.

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

For the deferred owner-machine pacing investigation, see
[the focused capture recipe](docs/performance-investigation.md).

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
