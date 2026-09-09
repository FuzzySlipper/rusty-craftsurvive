# Procgen motifs and layout inspection — #7907

Source: shared uncommitted CraftSurvive checkout based on
`d275aebd656c42bbb17955caef7e47208f2abc06`, 2026-09-09.

## Engine and focused checks

Updated from `0.1.0-dev.94490b482f96` to the latest published matched pair at
inspection, `0.1.0-dev.cda0274a1f6d`. GitHub release asset archive checksum and
bundled pair verifier passed. No Engine checkout mutation or product source
override. Exact SDK and runtime were staged together; metadata: engine-pair.json.

Passed: Workbench tests (three completing motifs, failures, legal counterexample
replay, strict v2 decoding and identity), existing Procgen tests, Tool self-check,
TerrainResidency/player input checks against the new SDK, UI typecheck, Game
Release build and normal CoreCLR staging. No NativeAOT release claim.

## Runtime diagnostic checks

`mesh-readouts.json` contains all six staged candidate readouts. Positive model
witnesses have 4 / 8 / 5 actions; all reachable states can reach completion.
Intentional bad candidates have no completing witness and preserve inspectable
counterexamples. Preview gates both goal routes and has a visible-before-access
mesh ray; sealed preview fails that ray as intended. Closed mesh route/separation
rays pass for all six final samples.

`mesh-readouts-before-station-fix.json` preserves the initial recovery failure:
the station on the goal's east side obstructed two route rays. Moving it to the
west side fixed the passage without relaxing the checks. Map and mesh consume
the same updated station position.

`runtime-transitions.json` uses diagnostic placement and commands (not native
walking). It checks irreversible spend, exhausted-key activation rejection,
once-only recovery, activation consumption, completion, reset, preview activation
rejection before observation, actual eye-to-goal observation, both gates opening,
sealed preview rejection, and failure-trace model steps leaving physical state
and position unchanged. Open preview reports 36/36 clear route rays.

## GPU / map interaction

Root-operated Wolf session `88f76e45-e62a-484e-beb5-80dccca058a5` passed guided
native keyboard map interaction and controller runs for both new motifs.
`native-captures.json` indexes original images and service metadata. Native UI
checks covered loading/entering samples, expanded layout and graph views, gate
layer toggling, persistent room selection, trace steps with separate model/world
positions, and observation interaction. Preview walking hit the closed window
wall (player Z=6.185), traversed relay/control, opened both gates and reached the
goal. Recovery walking spent the key, received rejection without a key, recovered
once, activated and completed. Readouts and native input receipts are adjacent.
Navigation used the map and HTTP position/state readouts; it was not blind play.

An independent playtester inspected the original map/world images and confirmed
readable rooms/edges, selection bounds, separate markers and a red goal visible
through the preview slot. Their own native attempt opened the panel but failed
to activate Load by pointer; pointer UI targeting remains uncertain. Successful
native operation above was by the implementing root, not the independent agent.
Both owned sessions were released; root `cleanup.json` reports capture stopped,
released true and no errors. No other agent's occupied slot was taken over.

The new Engine presentation readback was available in captures, with submitted
camera/viewport facts and no pending realizations in inspected snapshots. PNG
frame correlation and GPU completion remain unavailable. Original screenshots
are not proof of collision, performance or whole-world readiness on their own.

These checks are scoped witnesses, not exhaustive navigation, human recognition,
full camera-frustum visibility, blind usability or GPU performance certification.

Final host restart served matching fingerprint `26f704f34cffbcb9cccfee7d37082e3d9367e510231cd408e505b40c53fee5a8`. A fresh Wolf session `a5f7410a-20d7-428f-b0b8-65c80ed9d9ef` verified the last orientation-caption fix through native load/enter/expand. `final-map-capture.json` indexes the original; `expanded-preview-map.png` is an unchanged copy. `final-cleanup.json` records release.

## LAN startup regression found after GPU acceptance

The user subsequently reported `globalThis.crypto.randomUUID is not a function`
on plain LAN HTTP. Engine #7914 owns the unconditional presentation-surface ID
call in `render/packages/renderer-host/src/surface.ts:841`, shipped in pair
`0.1.0-dev.cda0274a1f6d`. No product UI call or downstream shim is involved.
The saved Wolf launch receipt used `browser_url: http://localhost:37300/`,
although its advertised URL was `http://192.168.1.22:37300/`. Thus the GPU runs
validated native gameplay under a localhost browser origin, not LAN-origin
startup. Future LAN acceptance must inspect the actual browser origin.
The LAN demo remains blocked pending an upstream paired release.
