# Rusty CraftSurvive

Rusty CraftSurvive is a downstream NativeAOT C# game product hosted by
[Rusty Engine](https://github.com/FuzzySlipper/rusty-engine). The C# product
lane is the normal runtime and owns the game rules, terrain recipe, player
policy, edits, persistence meaning, and product state.

> The product decides. The Engine guarantees.

Rusty Engine supplies lifecycle and host integration, input delivery, camera
and character mechanisms, voxel residency and presentation, resources, and
persistence primitives through its generated C# surface. The browser-side
TypeScript companion owns only a small DOM panel. It does not render game
elements, hold gameplay state, or implement a transport or game loop.

## Repository shape

```text
src/
  CraftSurvive.Game/          safe C# product domains
  CraftSurvive.NativeProduct/ thin NativeAOT composition project
  ui/                         DOM-only companion and TypeScript source
content/
  textures/                   canonical terrain content and metadata
docs/                         current migration, ownership, and limitations
scripts/
  generate-browser-bundle.mjs generated Engine host bundle helper
  run-csharp.sh               direct NativeAOT product runner
```

The former downstream Rust crate and bespoke browser application were retired
after the terrain/player slices became runnable. Their implementation and
experiment assets remain available through Git history, not as a second lane
that agents might accidentally extend. Current untracked `content/animations/`
and `content/voxels/` assets are preserved for future product work.

## Run the C# product

Keep this repository beside `rusty-engine`:

```text
dev/
  rusty-engine/
  rusty-craftsurvive/
```

Install the one UI build dependency once:

```bash
pnpm install --frozen-lockfile
```

Run directly with the adjacent Engine checkout:

```bash
./scripts/run-csharp.sh --bind-host 127.0.0.1 --port 4419
```

Or let Den own the host process:

```bash
den-serve up rusty-craftsurvive -repo /home/dev/rusty-craftsurvive
den-serve status rusty-craftsurvive
```

The runner compiles the DOM companion, generates the ignored Engine
product-browser-host bundle, publishes the NativeAOT product, and starts
Engine's `csharp-product-runtime` host. The normal Den service is
`.den-serve.json`; there are no alternate browser-smoke or Rust-host
manifests.

## Current product slice

- `Modules/Terrain` owns deterministic bounded generation, chunk residency,
  revision-checked edits, overlay persistence, Engine voxel presentation, and
  the terrain UI projection.
- `Modules/Player` owns input interpretation, look and movement policy,
  product tuning, moving-platform policy, world-position tracking, origin
  rebasing, camera composition, and player UI facts. Engine performs the
  collision, character, camera, appearance, and origin mechanisms.
- `Modules/Sky` selects the canonical authored panorama through Engine
  Appearance and CameraView. Engine owns its resource and renderer lifecycle.
- `src/ui/main.ts` mounts static DOM guidance beside the Engine-owned canvas.

This is a runnable continuation lane, not a claim of complete survival
gameplay or a broad interactive certification. See
[`docs/known-limitations.md`](docs/known-limitations.md) for the deliberately
bounded product surface.

## Working on the product

Read [`AGENTS.md`](AGENTS.md), the adjacent Engine's C# SDK guidance, and
[`docs/csharp-migration-map.md`](docs/csharp-migration-map.md) before changing
the product/Engine boundary. Use the generated `Rusty.Engine` safe API in
ordinary product code. Do not add downstream Rust, handwritten P/Invoke,
unsafe game code, a second renderer or game loop, browser gameplay state, or a
custom JSON/WebSocket bridge.

If a needed mechanism is absent from the generated API, record the exact
upstream Engine capability and stop that slice. A missing capability is a
valid result; a downstream substitute is not.

Focused checks are available for the actual lane:

```bash
pnpm run check:ui
pnpm run audit:textures
dotnet build src/CraftSurvive.Game/CraftSurvive.Game.csproj --configuration Release
```

Use NativeAOT publish or a bounded direct run when the task needs that
evidence. Do not resurrect the removed broad browser smokes or retired
experiment gates.
