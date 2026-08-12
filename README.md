# Rusty CraftSurvive

Rusty CraftSurvive is a deliberately small downstream
[Rusty Engine](https://github.com/FuzzySlipper/rusty-engine) experiment. It puts the Engine voxel
path under a different kind of pressure than Studio: a first-person player can destroy and place
terrain voxels and immediately observe rebuilt collision, navigation, and mesh projections.

The world is one deterministic, finite generated island with rolling elevation, layered materials,
and a few distant landmarks. It is not an infinite-world or streaming prototype, and it does not
try to be a survival game yet.

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
view-relative WASD plus mouse look. `Space` jumps, `Shift` sprints, `Control` crouches, and `H`
applies a bounded lateral impact used to inspect external-motion response. Left click or `F` breaks the
targeted voxel; right click or `G` places one. The concise HUD exposes the selected presentation,
accepted input sequence, authoritative player/world revisions, pose, view, grounded/velocity facts,
stance and blocked stand-up, ground/contact/step/platform facts, collision-world identity,
target, terrain seed/size, mesh/startup measurements, selected brush, and typed edit result.
Keys `1`, `2`, and `3` select spherical edit radii 0, 1, and 2. `den-serve restart
rusty-craftsurvive` rebuilds and restores the
service; `den-serve stop rusty-craftsurvive` releases the broker-owned process group.

The default URL uses greedy boxes. Select another independently initialized presentation without
restarting the service:

```text
http://127.0.0.1:4419/?surface=mc
http://127.0.0.1:4419/?surface=dc
http://127.0.0.1:4419/?surface=box&seed=0x2a&size=96
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
cargo run -- --surface box --seed 0x2a --size 64
```

Restart to change modes. `box` uses greedy cubes, `mc` uses marching cubes, and `dc` uses dual
contouring. All three read the exact same canonical material voxels; the choice changes only the
disposable render mesh. Grass, dirt, and stone are visibly distinct through one retained texture
atlas. Greedy quads repeat tiles across merged faces; MC/DC use each triangle's geometric face
normal for deterministic dominant-plane world-space coordinates, and steep grass triangles use the
grass-side tile rather than a smoothed vertex normal or stretched top projection. A fast headless readout is available with
`--summary`. Terrain sizes must be even and in the bounded `32..=128` range. The default is 96;
the seed and size are startup inputs and do not introduce streaming or a second world authority.

Controls:

- `WASD`: move horizontally
- `Space`: jump while grounded
- `Shift`: sprint while standing
- `Control`: crouch; release to stand when clearance permits
- `H`: apply one bounded lateral controller impulse
- arrow keys: look
- left mouse or `F`: destroy the targeted voxel
- right mouse or `G`: place a grass voxel against the targeted face
- `1`, `2`, or `3`: select edit radius 0, 1, or 2

The window title and Engine telemetry overlay show the selected surface and authority revision.
Startup summaries report generation, authority-build, and mesh-build milliseconds plus mesh size.
Edits are printed as typed Rust-side receipts with affected volume and measured mesh/total latency.

`den-serve` runs the optimized Rust host; debug builds make the intentionally complete coherent
rebuild path more than ten times slower and are not representative of the playable demo. A practical
browser probe loads the real application, performs one ordinary single-block destroy through
physical input, and emits startup, end-to-end, Rust edit, and mesh timing as JSON:

```bash
pnpm perf:edit
CRAFTSURVIVE_URL='http://127.0.0.1:4419/?surface=mc' pnpm perf:edit
```

The default-size local optimized measurements are a 111,775-voxel authority and 10,694 box /
93,528 MC / 46,764 DC triangles. A measured box run took 604 ms to usable UI and 226 ms for the
visible destroy (149 ms Rust edit, including a 37 ms presentation mesh). MC took 1.82 seconds to
usable UI and 1.30 seconds for the visible destroy; DC took 1.45 seconds and 773 ms. Both
reconstructed modes still spend most of that end-to-end time transferring and applying expanded
textured triangle streams after the roughly 160 ms Rust edit. These are diagnostic local
measurements rather than hardware-independent promises; report regressions with seed, size,
surface, mesh counts, and probe JSON instead of shrinking the terrain silently.

## Authority and renderer boundary

Rust owns the island recipe, admitted material voxels, player pose, physical-input meaning,
collision, ray selection, break/place decisions, edit revisions, and render-frame projection. The
Engine `CharacterControllerService` is the sole movement/collision authority over an Engine entity
transform plus durable character-motion component. CraftSurvive owns checked tuning, fixed-step
orchestration, bindings, camera-eye presentation, the visible active moving platform, and the
product-authored impact action. `FirstPersonLookService` supplies the canonical yaw/pitch basis. A
break or place operation, including a volume brush, is one revision-checked prepared
`VoxelEditService` transaction. CraftSurvive rejects the whole transaction when any placement voxel
overlaps the player capsule or any brush cell leaves the finite world bounds, and builds the
selected box/MC/DC presentation from the prepared deltas before commit. Engine swaps the rebuilt
canonical scene only after collision, navigation, canonical chunk mesh, and the selected downstream
presentation all succeed.
The atlas, region metadata, source-image provenance, and deterministic rebuild command live under
`content/textures/`; texture resources are replaced with the complete initial renderer content and
remain presentation data rather than voxel authority.

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

Each run checks service identity, pointer lock, rightward mouse handedness, ten accepted movement
commands with diagonal normalization, crouch/stand, camera-forward `W`, grounded jump over the
trench, wall blocking, controller diagnostics, external impulse response, support destruction and lower gravity landing, typed
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
`.den-playwright.alt.json` holds size at the default 96 while selecting `seed=0x2a`, so paired
default/alternate screenshots isolate seed-driven terrain variation; a runtime URL
argument does not override a manifest's fixed `startPath`.

A vision playtester should be given the exact committed revision and explicit manifest/scenario
paths. It should navigate through repeated screenshots or frame bursts and physical mouse/keyboard
input, record indexed screenshots/video, and report what a player can see. It must not inspect
source, shell state, DOM, hidden datasets, websocket payloads, or internal readouts. Run at least two
independent principal sessions on the same clean revision; use a fresh broker session for a
deliberate reproduction or mode sample.

A qualifying principal packet ends through `playtest_finish` with a classified outcome, concise
annotation, and an assertion ledger that references the repeated artifacts for every mission area.
A generic finished session or a prose-only report does not count as mission completion. The initial
pilot's two qualifying Luna/max principal packets both ran on exact clean revision
`a71e7546869768704d41b5195d78aa7cb266971a`; their indexed evidence is retained with Den task 6787.

The deterministic campaign and a Luna visual playtest answer different questions: the former
certifies exact typed consequences; the latter judges discoverability, visible orientation, and
whether the route is playable without privileged knowledge. A screenshot-only visual pass is not a
replacement for the deterministic campaign. Before either one, restart the broker service so the
session is clean. If the manifest health probe fails, inspect `den-serve status` and
`den-serve logs`; if pointer lock is lost, click the canvas again before sending mouse movement.

The initial Luna pilot's rejected pointer-lock movement was resolved by Den task 6825. Fresh
playtests may now certify look handedness and camera-relative movement from repeated visible frames;
the deterministic campaign retains the exact bounded-command regression.

## Current scope

This bootstrap deliberately excludes streaming, infinite terrain, chunk eviction, a general
procgen framework, inventory, crafting, survival stats, persistence, networking, and mobile-specific
shells. The next useful experiments should stay focused on incremental remeshing and terrain
calibration before growing product systems. Character tuning and game feel remain downstream;
reusable kinematic movement, collision, and look mechanisms are consumed directly from Engine.

See [donor provenance](docs/donor-provenance.md) and
[known limitations](docs/known-limitations.md) for the exact starting point.
