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
targeted voxel; right click or `G` places one. The concise HUD exposes the selected presentation,
accepted input sequence, authoritative player/world revisions, pose, view, grounded/velocity facts,
target, and typed edit result. `den-serve restart rusty-craftsurvive` rebuilds and restores the
service; `den-serve stop rusty-craftsurvive` releases the broker-owned process group.

The default URL uses greedy boxes. Select another independently initialized presentation without
restarting the service:

```text
http://127.0.0.1:4419/?surface=mc
http://127.0.0.1:4419/?surface=dc
```

The visible spawn route has an unequal pair of orientation pillars behind the player, a one-voxel
trench that can be jumped, a narrow bridge whose support can be destroyed, and a tall collision
wall. All are ordinary island voxels, not test-only geometry. If startup reports missing browser
dependencies, run the pnpm install command above; if it reports a missing Engine crate, confirm the
adjacent checkout exists.

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
or scheduled freshness job. With a freshly restarted local service and system Chromium, run the
deterministic physical-input campaign across all three presentations:

```bash
den-serve restart rusty-craftsurvive -repo /home/dev/rusty-craftsurvive
pnpm smoke:surfaces
```

Each run checks service identity, pointer lock, rightward mouse handedness, camera-forward `W`,
grounded jump over the trench, wall blocking, support destruction and lower gravity landing, typed
player-overlap rejection without a world revision, accepted placement, and that the new voxel is an
immediate collision blocker. The proof reads visible HUD facts and compares Engine-canvas pixels;
it does not call a test-only gameplay API.

## Black-box playtesting

`.den-playwright.json` and `product-playtest.scenario.json` are the canonical Den playtest adoption
packet. The manifest starts an isolated dynamic-port product service at 1280 by 720, headless, with
video recording and `live-ui` artifacts. The scenario supplies the ordinary controls and visible
mission without expected coordinates or an internal state trace. `.den-playwright.mc.json` and
`.den-playwright.dc.json` provide only shorter presentation-mode sampling at the corresponding
ordinary query URLs; they do not create another gameplay authority.

A vision playtester should be given the exact committed revision and explicit manifest/scenario
paths. It should navigate through repeated screenshots or frame bursts and physical mouse/keyboard
input, record indexed screenshots/video, and report what a player can see. It must not inspect
source, shell state, DOM, hidden datasets, websocket payloads, or internal readouts. Run at least two
independent principal sessions on the same clean revision; use a fresh broker session for a
deliberate reproduction or mode sample.

The deterministic campaign and a Luna visual playtest answer different questions: the former
certifies exact typed consequences; the latter judges discoverability, visible orientation, and
whether the route is playable without privileged knowledge. A screenshot-only visual pass is not a
replacement for the deterministic campaign. Before either one, restart the broker service so the
session is clean. If the manifest health probe fails, inspect `den-serve status` and
`den-serve logs`; if pointer lock is lost, click the canvas again before sending mouse movement.

The initial Luna pilot found one evidence-backed calibration limit, tracked by Den task 6825:
after canvas pointer lock, repeated genuine mouse moves of at most 100 pixels left the visible view
unchanged and surfaced `rejected: invalidCommand` in independent box sessions and both MC/DC mode
samples. Until that follow-up closes, the black-box lane can certify visible startup, grounded
movement, collision, and target-dependent edits, but it must report mouse-look handedness and
camera-relative movement as failed or uncertain rather than infer them. The deterministic campaign
continues to own the exact input-command regression for this discrepancy.

## Current scope

This bootstrap deliberately excludes streaming, infinite terrain, chunk eviction, complicated
world generation, inventory, crafting, survival stats, persistence, networking, and mobile-specific
shells. The next useful experiments should stay focused on edit latency, incremental remeshing, and
terrain-material presentation before growing product systems. The grounded controller is a bounded
downstream mechanism rather than a general physics framework.

See [donor provenance](docs/donor-provenance.md) and
[known limitations](docs/known-limitations.md) for the exact starting point.
