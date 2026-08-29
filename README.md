# Rusty CraftSurvive

Rusty CraftSurvive is a downstream NativeAOT C# product migration for
[Rusty Engine](https://github.com/FuzzySlipper/rusty-engine). Its retained
Rust/TypeScript voxel experiment is a semantic donor; new product work belongs
in C#.

> The product decides. The Engine guarantees.

CraftSurvive will own its terrain recipe, player and world rules, edit policy,
and product state in C#. Rusty Engine supplies lifecycle/host integration,
input delivery, spatial mechanisms, rendering infrastructure, resources, and
persistence primitives through the generated C# API. TypeScript is limited to
the DOM companion UI and explicit Engine host/backend implementation.

## Repository shape

```text
src/
  CraftSurvive.Game/          safe C# product domains
  CraftSurvive.NativeProduct/ thin generated NativeAOT composition
  ui/                         DOM-only UI and ignored generated host bundle
docs/csharp-migration-map.md  donor ownership and planned slices
```

The first committed C# slice is intentionally only a buildable lifecycle and
browser-host shell. It contains no fake terrain, player, renderer, or custom
transport. Terrain, player controller/look, voxel edits/residency, persistence,
and presentation migrate as separate domain slices once their generated Engine
capabilities are confirmed.

## Run the C# shell

Keep this repository beside `rusty-engine`:

```text
dev/
  rusty-engine/
  rusty-craftsurvive/
```

```bash
den-serve up rusty-craftsurvive -repo /home/dev/rusty-craftsurvive
den-serve status rusty-craftsurvive
```

The service compiles the typed DOM UI, builds the generated Product Browser Host bundle, publishes the
NativeAOT library, and runs Engine's `csharp-product-runtime`. The visible UI
truthfully identifies this as a migration shell; it is not yet an interactive
voxel world.

For the direct equivalent:

```bash
./scripts/run-csharp.sh --bind-host 127.0.0.1 --port 4419
```

## Working on the migration

Read `AGENTS.md`, the adjacent Engine's `docs/csharp-sdk.md` and
`docs/csharp-product-style.md`, then
[`docs/csharp-migration-map.md`](docs/csharp-migration-map.md). Use the
generated `Rusty.Engine` safe surface; ordinary game code must not add unsafe
calls, handwritten P/Invoke, raw ABI types, another renderer, a TypeScript
gameplay path, or a custom JSON/WebSocket bridge.

If a required Engine mechanism is missing, record the exact capability and
stop that slice for upstream work. Do not construct a downstream substitute.

`content/animations/` and `content/voxels/` are user-owned untracked assets in
the current checkout. Preserve them exactly; do not stage, modify, or clean
them as part of the migration.

## Historical donor material

The existing `src/*.rs`, `src/bin/browser-host.rs`, and `web/src` files remain
temporarily as donor material. The non-UI browser rendering gardens and their
old proof scripts are not a C# architecture and are not active migration
targets. Their historical detail remains in Git and the retained source while
the product path is rebuilt deliberately.
