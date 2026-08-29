# Rusty CraftSurvive C# guidance

## Direction

CraftSurvive is migrating to an ordinary NativeAOT C# downstream product on
the adjacent Rusty Engine checkout. The old Rust and TypeScript implementation
is retained as semantic donor material while the C# lane becomes capable; it
is not an architecture to extend or a runtime to imitate.

> The product decides. The Engine guarantees.

- C# owns CraftSurvive's game rules, terrain recipe, player and world policy,
  edits, content meaning, and product state.
- Rusty Engine owns host lifecycle, input delivery, renderer resources and
  frame construction, spatial mechanisms, content/persistence primitives, and
  other named Engine services.
- TypeScript is limited to DOM UI and explicit Engine host/backend code. It
  must never render non-UI game elements or retain gameplay state.
- Do not add downstream Rust, handwritten P/Invoke, unsafe game code, browser
  renderers, custom WebSocket/JSON transports, or a second game loop.

Read the adjacent Engine's `AGENTS.md`, `docs/csharp-sdk.md`, and
`docs/csharp-product-style.md` before changing the product/Engine boundary.

## Current C# lane

- `src/CraftSurvive.Game` is safe product code. Organize new code by domain;
  each mutable state family has one explicit owner and thin coordination follows
  Read → Decide → Apply → Publish where that boundary is useful.
- `src/CraftSurvive.NativeProduct` is the thin NativeAOT composition project.
  It selects the product with `[assembly: EngineProduct(...)]`; the Engine
  source generator owns exported entry points and ABI plumbing.
- `src/ui/main.ts` is a small DOM-only companion. The generated product-browser
  host owns the canvas, renderer transport, and browser input delivery.
- `scripts/generate-browser-bundle.mjs` creates the ignored host bundle, and
  `scripts/run-csharp.sh` publishes the NativeAOT library and runs the Engine
  `csharp-product-runtime` host.

The generated C# API is a named capability surface, not a promise that every
Rust source API is callable. If a needed Engine mechanism is absent, name the
exact call shape, file or link the upstream task when authorized, and stop the
downstream substitute work. That is a valid result.

## Migration scope

`docs/csharp-migration-map.md` records the current Rust/TypeScript donor areas,
their intended C# owners, and explicit deferrals. Do not port or delete donor
code opportunistically. In particular, terrain, collision/controller,
ray-picking, persistence, and presentation each need an intentional slice once
their generated Engine capabilities are confirmed.

## Working posture

- Project ID: `rusty-craftsurvive`. Resolve live Den guidance before
  substantial work. If Den is unreachable, stop and report that failure.
- Preserve unrelated dirty work. `content/animations/` and `content/voxels/`
  are currently user-owned untracked assets: never modify, stage, or clean them.
- Use focused generation, C# build, NativeAOT publish, or direct capability
  checks only when they answer the active task. Tests and proof scaffolding are
  evidence, not the deliverable.
- Report a short milestone before expensive integration: goal advanced,
  surfaces, proof scaffolding, drift/unsupported boundary, and upstream needs.
