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

The normal browser-playable path is broker-managed:

```bash
pnpm install --frozen-lockfile
den-serve up rusty-craftsurvive -repo /home/dev/rusty-craftsurvive
den-serve status rusty-craftsurvive
den-serve logs rusty-craftsurvive
```

Open the local or LAN URL printed by `den-serve up`, click the world to capture the mouse, and use
view-relative WASD plus mouse look. `Space` jumps while grounded. Left click or `F` breaks the
targeted voxel; right click or `G` places one. The HUD reports the authoritative grounded/velocity
state and target or an explicit out-of-reach/miss result. `den-serve restart
rusty-craftsurvive` rebuilds and restores the service; `den-serve stop rusty-craftsurvive` releases
the broker-owned process group. Set `RUSTY_CRAFTSURVIVE_SURFACE=mc` or `dc` before `up`/`restart`
to select another startup surface. If startup reports missing browser dependencies, run the pnpm
install command above; if it reports a missing Engine crate, confirm the adjacent checkout exists.

The native development path remains available and chooses terrain presentation directly:

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
- `Space`: jump while grounded
- arrow keys: look
- left mouse or `F`: destroy the targeted voxel
- right mouse or `G`: place a grass voxel against the targeted face

The window title and Engine telemetry overlay show the selected surface and authority revision.
Edits are also printed as typed Rust-side receipts.

## Authority and renderer boundary

Rust owns the island recipe, admitted material voxels, player pose, physical-input meaning,
collision, ray selection, break/place decisions, edit revisions, and render-frame projection. A
break or place operation is one prepared `VoxelEditService` transaction. CraftSurvive rejects a
placement whose voxel overlaps the player capsule, and builds the selected box/MC/DC presentation
from the prepared deltas before commit. Engine swaps the rebuilt canonical scene only after
collision, navigation, canonical chunk mesh, and the selected downstream presentation all succeed.

The downstream application owns the native window and calls the Engine-owned
`RendererWebviewAdapter`. It does not import renderer TypeScript, own a canvas, decode the private
bridge, or reach into Three/WebGL. The renderer observes Rust frames and camera poses and returns
raw physical input; it never owns gameplay or voxel authority.

The browser shell imports only `@rusty-engine/application-host`. Its TypeScript owns trusted local
HUD composition and translates physical browser input into bounded semantic proposals. Rust still
owns live world/player facts and consequences, while the Engine application host owns the sole
canvas, renderer lifecycle, frame decoding, and DOM/renderer composition. This exact bundle can be
hosted by a browser, Tauri, or Electron wrapper. Do not create a downstream renderer, duplicate
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

The public CI is intentionally one small job. It clones rolling-current Engine beside the checkout,
then runs Rust formatting/tests/Clippy plus TypeScript typechecking, the application-host-only
boundary audit, and the production build. It has no browser campaign, upstream pin certification,
or scheduled freshness job. With a healthy local service and system Chromium, run the bounded live
proof with `pnpm smoke:browser`.

## Current scope

This bootstrap deliberately excludes streaming, infinite terrain, chunk eviction, complicated
world generation, inventory, crafting, survival stats, persistence, networking, and mobile-specific
shells. The next useful experiments should stay focused on edit latency, incremental remeshing, and
terrain-material presentation before growing product systems. The grounded controller is a bounded
downstream mechanism rather than a general physics framework.

See [donor provenance](docs/donor-provenance.md) and
[known limitations](docs/known-limitations.md) for the exact starting point.
