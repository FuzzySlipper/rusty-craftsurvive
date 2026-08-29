# Rusty CraftSurvive C# guidance

## Direction and ownership

CraftSurvive is an ordinary NativeAOT C# downstream product hosted by the
adjacent Rusty Engine checkout. The C# lane is the only normal runtime. The
former Rust session and bespoke browser application were retired in task
7497; their implementation is available in Git history only as semantic
donor material.

> The product decides. The Engine guarantees.

- C# owns CraftSurvive's game rules, terrain recipe, player and world policy,
  edits, content meaning, and product state.
- Rusty Engine owns host lifecycle, input delivery, renderer resources and
  frame construction, spatial mechanisms, content/persistence primitives, and
  other named Engine services.
- TypeScript is limited to the DOM companion and explicit Engine host/backend
  composition. It must never render non-UI game elements, retain gameplay
  state, implement a transport, or create a second game loop.
- Do not add downstream Rust, handwritten P/Invoke, unsafe game code, browser
  renderers, custom WebSocket/JSON transports, or a second product runtime.

Read the adjacent Engine's `AGENTS.md`, `docs/csharp-sdk.md`, and
`docs/csharp-product-style.md` before changing the product/Engine boundary.

## Den and missing capabilities

- Project ID: `rusty-craftsurvive`. Resolve live Den guidance before
  substantial work. The current task overrides stale documents and old
  assumptions about downstream Rust, compiled TypeScript gameplay, or Product
  Model.
- If Den is unreachable, stop and report that failure. Do not invent local
  task records.
- If a needed mechanism is not expressible through the generated API, identify
  the exact upstream capability, file or task, and stop. Do not recreate it in
  C#, TypeScript, or browser code merely to complete a task.

## Current C# lane

- `src/CraftSurvive.Game` is safe product code organized by gameplay domain;
  each mutable state family has one explicit owner and thin coordination
  follows Read → Decide → Apply → Publish where that boundary is useful.
- `src/CraftSurvive.NativeProduct` is the thin NativeAOT composition project.
  It selects the product with `[assembly: EngineProduct(...)]`; the Engine
  source generator owns exported entry points and ABI plumbing.
- `src/ui/main.ts` is a small DOM-only companion. The generated Engine
  product-browser-host bundle owns the canvas, renderer transport, and browser
  input delivery.
- `scripts/generate-browser-bundle.mjs` creates the ignored host bundle, and
  `scripts/run-csharp.sh` publishes the NativeAOT library and runs Engine's
  `csharp-product-runtime` host.
- `.den-serve.json` is the single normal Den runtime manifest. Old browser
  smoke/garden manifests and the bespoke `serve-den.sh` host are retired.

## C# product style

- Use Engine-provided update facts, input, spatial, camera, appearance,
  persistence, and UI services for product behavior. Do not retain native
  pointers, create a second loop, or depend on browser state for game meaning.
- Prefer file-scoped namespaces, nullable reference types, `internal` and
  `sealed` defaults, records/value types for small immutable data, and explicit
  composition. Keep unsafe/PInvoke out of ordinary product projects.
- Never bury numeric or string tuning/identities in behavior. At minimum give
  a local value a named `const` or `static readonly` declaration; prefer typed
  definitions or product-owned configuration adapters for values that need
  tuning. Avoid a giant global constants dump unless a value is genuinely
  cross-domain.

## Work and evidence

- Add capabilities as coherent Engine service families informed by real
  downstream needs. The generated surface is not a claim that every Rust
  source API is available in C#.
- Report a short milestone before expensive integration: goal advanced,
  necessary surfaces, proof scaffolding, drift/unsupported boundary, and any
  upstream request.
- Tests are evidence, not the deliverable. Run generation, focused
  compilation, NativeAOT publish, or a direct exercise only when it answers
  the active task. Do not chase the removed browser/garden smokes, old Rust
  verification, packaging, security, or conformance gates without a task
  requirement.
- Preserve unrelated work. In particular, `content/animations/` and
  `content/voxels/` are user-owned untracked assets in the working checkout:
  never modify, stage, or clean them.

## Documentation status

The current documentation set is intentionally small and rooted in the
landed C# source. Start at `docs/csharp-migration-map.md` and
`docs/known-limitations.md`. Use Git history as donor material for retired
Rust/browser experiments; do not recreate a parallel runtime or archive copy
in the working tree.
