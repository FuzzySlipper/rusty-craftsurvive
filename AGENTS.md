# Rusty CraftSurvive guidance

## Direction and ownership

CraftSurvive is an ordinary C# product that develops against the installed
Rusty.Engine SDK and its paired runtime pack. `rusty dev` loads the staged
CoreCLR product; that is the normal local and Den lane. NativeAOT is an
explicit fidelity/release verification, not a second product project or the
development host.

> The product decides. The Engine guarantees.

- C# owns CraftSurvive's game rules, terrain recipe, player and world policy,
  edits, content meaning, and product state.
- Rusty Engine owns lifecycle, input delivery, renderer resources and frame
  construction, spatial mechanisms, content/persistence primitives, and other
  named Engine services.
- `src/ui/main.ts` is a DOM-only companion. It must not render game elements,
  retain gameplay state, implement a transport, or create a game loop.
- Do not add downstream Rust, handwritten P/Invoke, unsafe game code, a
  renderer, custom JSON/WebSocket transport, or a second product runtime.

Read the packaged SDK's C# guidance when changing the product/Engine boundary.

## Installed development pair

- `.runtime/runtime-pack-b9740421c2d2/` is the current runtime pack. Its `bin/rusty dev`
  command is the only normal loader and stages the CoreCLR product.
- `.runtime/sdk-feed/Rusty.Engine.0.1.0-dev.b9740421c2d2.nupkg` is the exact SDK
  package pinned in `CraftSurvive.Game.csproj`. Do not substitute a package,
  runtime pack, or backup directory independently; update the pair together.
- During joint Engine/product work, do not keep this product on an older
  known-good pair after the intended Engine revision advances. Update the
  declared revision; if its exact pair has not been staged yet, leave the
  resulting build failure visible and report it instead of pinning backward.
- `NuGet.Config` intentionally resolves the SDK from that installed feed. The
  product does not discover or require an Engine source checkout.
- An Engine contributor may override only deliberately: pass
  `--engine-source <absolute-path>` to `rusty dev`, which supplies the matching
  MSBuild properties. A direct MSBuild invocation instead sets
  `RustyEngineUseSourceDevelopment=true` with an absolute
  `RustyEngineSourceDevelopmentPath`. Never infer either path.

## Den and missing capabilities

- Project ID: `rusty-craftsurvive`. Resolve live Den guidance before
  substantial work. The current task overrides stale documents.
- If Den is unreachable, stop and report that failure. Do not invent local
  task records.
- If a needed mechanism is not expressible through the SDK, identify the exact
  upstream capability, file or link the owning task when authorized, and stop.
  Do not recreate it in product code or UI code.

## Product shape

- `src/CraftSurvive.Game` is the sole checked product project. It is safe C#
  organized by gameplay domain; each mutable state family has one explicit
  owner and thin coordination follows Read -> Decide -> Apply -> Publish where
  that boundary is useful.
- The SDK generates all composition and staging artifacts below ignored `obj/`.
  They are not source, configuration, or an extension point.
- `src/ui/main.ts` compiles into the staged product UI. The Engine host owns
  canvas, rendering, input delivery, and runtime integration.
- `.den-serve.json` and `.den-playwright.json` both start the packaged
  CoreCLR lane. Preserve a broker-owned live session; do not start a competing
  host merely to inspect it.

## C# product style

- Use Engine-provided update facts, input, spatial, camera, appearance,
  persistence, and UI services for product behavior. Do not retain native
  pointers, create a second loop, or depend on UI state for game meaning.
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
  downstream needs. The SDK surface is not a claim that every Engine source API
  is available in C#.
- Report a short milestone before expensive integration: goal advanced,
  necessary surfaces, proof scaffolding, drift/unsupported boundary, and any
  upstream request.
- Focused normal-lane proof is `pnpm run check:ui` and a Release build of
  `CraftSurvive.Game`. Run `VerifyRustyEngineAot` only when a task explicitly
  requires fidelity/release evidence.
- Preserve unrelated work. In particular, `content/animations/` and
  `content/voxels/` are user-owned assets in the working checkout: never
  modify, stage, or clean them.

## Documentation status

The current documentation set is rooted in the checked product and installed
SDK pair. Start at `docs/csharp-migration-map.md` and
`docs/known-limitations.md`. Historical experiments may be consulted as
semantic evidence only; do not recreate a parallel runtime or archive copy in
the working tree.
