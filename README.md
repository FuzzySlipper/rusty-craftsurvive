# Rusty CraftSurvive

Rusty CraftSurvive is a deliberately small downstream
[Rusty Engine](https://github.com/FuzzySlipper/rusty-engine) experiment. It puts the Engine voxel
path under a different kind of pressure than Studio: a first-person player can destroy and place
terrain voxels and immediately observe rebuilt collision, navigation, and mesh projections.

The initial world is one deterministic, finite island. It is not an infinite-world or streaming
prototype, and it does not try to be a survival game yet.

## Local topology

Keep this repository beside a current Rusty Engine checkout:

```text
dev/
  rusty-engine/
  rusty-craftsurvive/
```

`Cargo.toml` has one unconditional dependency on the complete Engine facade at
`../rusty-engine/rust/crates/rusty-engine`. There are no crate-by-crate dependencies, version pins,
SHA records, updater scripts, or pull/freshness ceremony. On another machine, place a compatible
Engine checkout beside this repository and decide locally when to pull it; upstream breakage is
intentionally fixed forward here.

## Run

Choose the terrain presentation when the process starts:

```bash
cargo run -- --surface box
cargo run -- --surface mc
cargo run -- --surface dc
```

Restart to change modes. `box` uses greedy cubes, `mc` uses marching cubes, and `dc` uses dual
contouring. All three read the exact same canonical material voxels; the choice changes only the
disposable render mesh. A fast headless readout is available with `--summary`.

Controls:

- `WASD`: move horizontally
- `Space` / left Shift: move vertically (the first experiment uses bounded fly movement)
- arrow keys: look
- left mouse or `F`: destroy the targeted voxel
- right mouse or `G`: place a grass voxel against the targeted face

The window title and Engine telemetry overlay show the selected surface and authority revision.
Edits are also printed as typed Rust-side receipts.

## Authority and renderer boundary

Rust owns the island recipe, admitted material voxels, player pose, physical-input meaning,
collision, ray selection, break/place decisions, edit revisions, and render-frame projection. A
break or place operation is one `VoxelEditService` transaction. Engine swaps the rebuilt canonical
scene only after collision, navigation, and its canonical chunk mesh all succeed; this demo then
rebuilds the selected box/MC/DC presentation mesh from the accepted material-voxel readout.

The downstream application owns the native window and calls the Engine-owned
`RendererWebviewAdapter`. It does not import renderer TypeScript, own a canvas, decode the private
bridge, or reach into Three/WebGL. The renderer observes Rust frames and camera poses and returns
raw physical input; it never owns gameplay or voxel authority.

There is no downstream TypeScript in the initial repository. If a browser/Tauri/Electron product
shell or richer DOM HUD is added later, TypeScript may own trusted local content composition and
disposable UI state. Rust must still own live world facts and consequences, while the Engine
application host owns renderer/DOM composition. Do not create a downstream renderer, duplicate
world store, generic JS command bus, or TS gameplay runtime.

Studio also remains an Engine-hosted authoring product. This demo currently has no authored project
document or Studio adapter, so it intentionally has no `.rusty-studio.json`. Add that narrow project
adapter only when there is real persisted content to open; never copy Studio or renderer internals
into this repository.

Downstream content is trusted local project content. Do not add a security/sanitization framework
for ordinary local UI or content composition unless an actual untrusted boundary is introduced.

## Verify

```bash
./scripts/verify.sh
```

The public CI is intentionally one small Rust job. It clones rolling-current Engine beside the
checkout, then runs formatting, tests, and Clippy. It has no browser campaign, headed renderer
proof, upstream pin certification, or scheduled freshness job.

## Current scope

This bootstrap deliberately excludes streaming, infinite terrain, chunk eviction, complicated
world generation, gravity/jumping, inventory, crafting, survival stats, persistence, networking,
and mobile/browser shells. The next useful experiments should stay focused on edit latency,
incremental remeshing, terrain-material presentation, and player collision before growing product
systems.

See [donor provenance](docs/donor-provenance.md) and
[known limitations](docs/known-limitations.md) for the exact starting point.
