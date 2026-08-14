# Rusty CraftSurvive

Rusty CraftSurvive is a deliberately small downstream
[Rusty Engine](https://github.com/FuzzySlipper/rusty-engine) experiment. It puts the Engine voxel
path under a different kind of pressure than Studio: a first-person player can destroy and place
terrain voxels and immediately observe rebuilt collision, navigation, and mesh projections.

The world is deterministic terrain addressed from a versioned seed plus signed world coordinates.
A bounded chunk window keeps only nearby authority resident as travel continues; this proves
unbounded addressing within documented numeric and storage limits, not literally infinite
computation, memory, or precision, and it does not try to be a survival game yet.

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
targeted voxel; an accepted break emits a short-lived burst of material-tinted cube debris that
bounces against a bounded snapshot of adjacent terrain. The debris is disposable Engine
presentation and never delays or changes the voxel edit. Right click or `G` places one. The concise
HUD exposes the selected presentation,
accepted input sequence, authoritative player/world revisions, pose, view, grounded/velocity facts,
stance and blocked stand-up, ground/contact/step/platform facts, collision-world identity,
target, terrain seed/size, mesh/startup measurements, selected brush, and typed edit result.
Keys `1`, `2`, and `3` select spherical edit radii 0, 1, and 2. `den-serve restart
rusty-craftsurvive` rebuilds and restores the
service; `den-serve stop rusty-craftsurvive` releases the broker-owned process group.

The game-owned panoramic sky exercises Engine's authored camera-relative background contract. It
rotates with the camera while remaining fixed under player translation, and it is presentation
only: the panorama does not contribute lighting, reflections, collision, or gameplay authority.

Two small forest wisps directly ahead and to the right of the default spawn exercise the public
Engine lit-sprite route. The left wisp is the unlit reference; the right uses derived-gradient
lighting under the same host ambient/directional route and a moving retained cyan point light. They use the same checked RGBA8
texture, masked alpha, and cylindrical billboarding. Look right from spawn, then strafe around the
pair: their silhouettes should remain stable while the right wisp's modeled light response changes
and the left reference remains visually constant.

The default URL uses greedy boxes. Select another independently initialized presentation without
restarting the service:

```text
http://127.0.0.1:4419/?surface=mc
http://127.0.0.1:4419/?surface=dc
http://127.0.0.1:4419/?surface=box&seed=0x2a&size=96
```

The visible spawn route has an unequal pair of orientation pillars behind the player, a one-voxel
trench that can be jumped, a narrow bridge whose support can be destroyed, and a tall collision
wall. `?course=platform` selects an ordinary alternate spawn above the visible moving-platform
station for a bounded carry check; it does not change world or controller authority. All course
geometry is ordinary product state, not a hidden test API. If startup reports missing browser
dependencies, run the pnpm install command above; if it reports a missing Engine crate, confirm the
adjacent checkout exists.

`?course=stream` starts at the west end of a flat, ordinary terrain corridor for inspecting chunk
admission, eviction, reversal, and texture/collision seams. The HUD residency row reports signed
chunk center, resident/pinned/loading counts, cumulative evictions, dense authority bytes, and the
latest generation/admission latency. `pnpm smoke:streaming` drives that route through physical
browser input and records the lifecycle samples.

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
`--summary`. Terrain scale values must be even and in the bounded `32..=128` range. The default is
96; seed and scale are startup inputs and do not introduce a second world authority.

Controls:

- `WASD`: move horizontally
- `Space`: jump while grounded
- `Shift`: sprint while standing
- `Control`: crouch; release to stand when clearance permits
- `H`: apply one bounded lateral-and-upward controller impulse
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

The probe also reports dirty/rebuilt handles and encoded frame bytes so a small edit cannot silently
regress to global replacement. On the default 96-square terrain after chunk publication landed, one
ordinary destroy dirtied and replaced two stable chunk handles: box emitted about 61 KiB and reached
the visible result in 254 ms, MC emitted about 680 KiB in 539 ms, and DC emitted about 395 KiB in
683 ms on the development host. The reconstructed modes remain more expensive, but no mode sends
the complete world mesh. These are diagnostic local measurements rather than hardware-independent
promises; report regressions with seed, size, surface, chunk counts, encoded bytes, and probe JSON
instead of shrinking the terrain silently.

For a quick debris check, start the normal default route, click the canvas, and break the voxel under
the crosshair with left click or `F`. Observe the burst through its full roughly one-second lifetime,
then break several neighboring blocks. The authoritative block must disappear immediately; cubes
may bounce or settle against only the nearby collision snapshot and must expire without affecting
movement, placement, or later edits.

The same real-browser performance probe can preserve synchronized debris evidence without changing
its normal CI behavior. Create the destination first, then set
`CRAFTSURVIVE_EDIT_CAPTURE_DIR=/absolute/path`; optional
`CRAFTSURVIVE_EDIT_CAPTURE_DELAYS_MS=0,500,4000` controls the post-edit screenshot schedule. The
probe reports actual capture completion times because software-rendered screenshots can be much
slower than the requested delay.

## Authority and renderer boundary

Rust owns the island recipe, admitted material voxels, player pose, physical-input meaning,
collision, ray selection, break/place decisions, edit revisions, and render-frame projection. The
Engine `CharacterControllerService` is the sole movement/collision authority over an Engine entity
transform plus durable character-motion component. CraftSurvive owns checked tuning, fixed-step
orchestration, bindings, camera-eye presentation, the visible active moving platform, and the
product-authored impact action. `FirstPersonLookService` supplies the canonical yaw/pitch basis. A
break or place operation, including a volume brush, is one revision-checked prepared
`VoxelEditService` transaction. CraftSurvive rejects the whole transaction when any placement voxel
overlaps the player capsule or any brush cell leaves Engine's admitted coordinate envelope. Engine atomically
publishes the coherent collision, navigation, and selected chunk-mesh revision; the stateful Engine
voxel projector then creates, replaces, or destroys only those stable retained chunk handles.
CraftSurvive adapts each emitted chunk payload to its atlas without coalescing chunks or creating
another world authority.

## Unbounded addressing and finite residency

Terrain seed plus signed voxel coordinates deterministically define generation version 2. Chunk
payloads are generated X-fastest from that recipe and a caller-owned edit overlay;
the same seed, recipe version, chunk coordinate, and overlay always produce the same payload.
CraftSurvive requests a 3 by 3 column neighborhood around the player, retains a 5 by 5 hysteresis
neighborhood, pins currently requested non-empty chunks through Engine leases, and applies at most
16 admissions or evictions per input tick. Empty chunks are not materialized. The bounded route
stayed at or below 29 resident chunks (232 KiB of dense material slots) while crossing six signed
chunk centers and reversing, with 60 evictions. A far-coordinate browser run at chunk 4090 crossed
five centers, stayed at or below 24 chunks / 192 KiB, persisted one edit through reload exactly
once, and isolated a changed seed from that save.

Accepted edits are retained by signed voxel address even after their chunk is evicted. Re-admission
regenerates the deterministic base and applies that overlay before Engine's guarded residency
transaction; a focused leave/return test proves a removed voxel does not resurrect. Source failure
or invalid admission leaves the prior coherent scene untouched. The current mechanism consumes
Engine chunk publication from `0bd00d9`/`fc0925d` and residency transactions introduced by
`e3037cb`; the adjacent rolling Engine head used for this implementation was
`5930942b384ad9ec63ec7ee76afd4a84756eae2b`. Browser sessions atomically persist a canonical,
fingerprinted JSON overlay under `target/craftsurvive-saves`, keyed by generation version and seed.
Malformed, oversized, wrong-seed, or unsupported-version documents fail before a session opens;
there is intentionally no guessed migration. The overlay is bounded to 65,536 entries and 8 MiB.

Engine admits voxel addresses through ±1,000,000. CraftSurvive now certifies first-person
simulation and rendering through both signs at ±262,144 world units. Engine task 6895 supplies an
exact `i64` cell plus normalized `f64` offset global position and a guarded world-origin rebase;
CraftSurvive keeps that exact global pose as authority while character collision, voxel authority,
camera, and retained rendering operate in a bounded local `f32` frame. A rebase is scheduled when
the player reaches 32 local units, atomically translates all registered local roots and spatial
derivatives, and preserves stable entity/chunk render handles. The deterministic far route crosses
multiple origin revisions in both signs, keeps local X/Z below the trigger, and checks residency,
edit persistence, and seed isolation. Terrain has no authored edge inside the supported range, but
this remains a bounded numeric and storage contract rather than a limitless-world claim.
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
`.den-playwright.stream.json` and `product-playtest.streaming.scenario.json` select the ordinary
west-corridor spawn for a bounded streaming, reversal, and edit-retention judgement; the explicit
manifest is required because broker startup paths do not inherit a later runtime query argument.
`.den-playwright.unbounded.json` and `product-playtest.unbounded.scenario.json` select a fixed seed
near the certified positive-coordinate edge for long-route, edit, reload, rebase, and jitter
judgement. The deterministic `pnpm smoke:unbounded` route additionally traverses both signed edges
and requires multiple origin revisions while the local player frame remains bounded.

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

This bootstrap deliberately excludes limitless numeric precision, a general procgen framework,
inventory, crafting, survival stats, networking, and mobile-specific shells. Retained terrain
publication and finite residency are chunk-granular. Character tuning and game feel remain downstream;
reusable kinematic movement, collision, and look mechanisms are consumed directly from Engine.

See [donor provenance](docs/donor-provenance.md) and
[known limitations](docs/known-limitations.md) for the exact starting point.
