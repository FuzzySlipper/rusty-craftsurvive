# Procgen workbench evidence

Task: Den #7906. Source: current uncommitted CraftSurvive checkout based on
`d275aebd656c42bbb17955caef7e47208f2abc06`, 2026-09-09.

## Focused checks

- `tests/Workbench`: legal action witness, closed shortcut rejection, artifact
  roundtrip/identity, malformed/missing/duplicate JSON fields, invalid identities.
- Existing `tests/Procgen` and Tool `--self-check`: passed. The Tool self-check
  must run from the CraftSurvive checkout so it can find its retained corpus.
- `pnpm run check:ui`, Game Release build and CoreCLR staging: passed.
- Broker-owned den-serve restarted at 04:02:58 UTC on
  `http://192.168.1.22:37300/`; launch/current fingerprints matched. The served
  `product-ui/workbench.js` matched the generated local module.

## Live debug integration (not native gameplay)

[runtime-checks.json](runtime-checks.json) retains command receipts and C#
readouts for loading, enter/reset, stale revision rejection, witness stepping
without physical changes, switch rejection away from the control, and a missing
content path leaving the accepted candidate intact.

[switch-integration.json](switch-integration.json) uses debug-assisted placement
at the control to exercise the same switch operation. Closed state has 27/27
clear route rays and 27/27 blocked separation rays. Open state has 36/36 clear
route rays and 18/18 blocked separation rays. Reset returns the player to start
and closes the shortcut. These are actual collision-mesh ray results, not
exhaustive navigation or bypass proofs.

One earlier diagnostic setup teleported the character **center** too low
(Y3.05 against floor Y3), intersecting the floor and producing an Engine
`Spatial.ProposeCharacterStep` failure/runtime replacement. The corrected
probe uses center Y3.95, consistent with the standing controller. This correction
does not establish walking through the level or native input consumption.

## Visible interaction

Crew-services Wolf GPU session `d630c1dc-7640-451c-9327-72125795dfb4`:
root-operated, readout-guided functional pass for keyboard Load/Enter, controller
walking start → relay → control, controller Y activation, reaching goal, and
returning through the opened shortcut. No teleport or debug mutation was used
for that positive run. HTTP position readouts guided movement durations.

After a diagnostic HTTP Reset, ordinary controller movement into the closed
shortcut stopped at Z6.185 with `blocked=Wall`; see native-blocked-player.txt.
This negative check uses assisted setup. A native pointer Reset attempt did not
activate; capture `84b2bea4-60b1-4edc-ac96-af8c009a47cb` was prematurely labelled
closed-gate-blocks and is excluded as closed-gate evidence (it remained open).
Pointer targeting is uncertain; native keyboard Load/Enter succeeded. This is
not blind discoverability, full capsule/bypass coverage, or GPU performance proof.

The earlier observer reached the panel but could not activate Load. Root fixed
background polling disabling focused controls/dropping actions, redeployed and
verified keyboard activation. The observer's earlier retired-broker baseline is
excluded. Independent inspection of the final original images is recorded below.

`native-captures.json` retains original paths and capture metadata. Key captures:

- Entry: `64b7a7d1-235d-4ba1-a9f5-5d43085f8ce2`.
- Goal, switch open, completed: `bbcd6f2f-3b9f-47de-b278-f54fa484d0a4`.
- Return to start, switch open: `a6aee3a3-dfa2-4f8e-b719-f1dc3ae5d2c2`.
- Closed gate after assisted reset: `c66a6fd9-f400-4235-95a7-eeeb479ddfec`.

Native switch/goal/return readouts are retained separately. Native cleanup:
released=true, local_capture_stopped=true, errors=[], see native-cleanup.json.
Original images remain under the returned crew-services artifact directory;
metadata is retained here without copying private Moonlight logs.

Independent image inspection (playtester, originals only): entry showed start /
closed at revision 2; goal showed goal / open and reported complete at revision 3;
return showed start / open; final closed view showed revision 4, passage / outside
rooms and closed, with a nearby wall. The stills support displayed state and
world views, not collision alone. Collision evidence comes from controller
actions plus Engine readout. Blue text selection in the last capture and native
pointer-targeting uncertainty are retained. Root operated the successful run;
this is not an independently operated successful full mission.
