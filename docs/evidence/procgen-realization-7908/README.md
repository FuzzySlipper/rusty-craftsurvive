# Realized route and protected-gate checks — #7908

Product: CraftSurvive, based on `63322980012a565d29571df28dc32373ab6dc1ce`.
Matched Engine SDK/runtime: `0.1.0-dev.2e4255bd3ad5`.

## Measured behavior

A side aperture is subtracted from one closed gate without changing candidate
intent. The aperture leaves the center and legacy +/-0.4 ray samples blocked.
New Engine standing-capsule casts detect a clear side lane where intent requires
a barrier. Repair returns the original mesh collision projection hash.

The completed `runtime-matrix.json` covers these states:

| Sample | Intact | Side breach | Repaired | Open |
| --- | --- | --- | --- | --- |
| workbench-11 | 38/38 | 34/38; 4 detected failures | 38/38 | 38/38 |
| preview-11 | 48/48 | 44/48; 4 detected failures | 48/48 | 48/48 |
| recovery-11 | 38/38 | 34/38; 4 detected failures | 38/38 | 38/38 |
| complex-29 | 340/340 | 336/340; 4 detected failures | 340/340 | 340/340 |
| complex-83 | 340/340 | 336/340; 4 detected failures | 340/340 | 340/340 |

No unavailable capsule queries occurred in that matrix. The four failures are
the selected gate's full side lane and local cross-section, each in both
directions. Legacy route/separation ray checks still pass for each breach.
The result captures include candidate identity, physical state, geometric
requirement-state identity, versioned realization identity, actual collision
revision/hash, total check counts, and all nonpassing reported rows. Reported
rows are bounded to 64 with failures/unknowns first; summary counts include every
executed probe. This file is a compact extraction of the completed runtime
readouts, not a reconstructed collision calculation.

Runtime matrix setup used existing debug commands to load/treat/repair and place
the player beside stations. Preview observation and recovery spend/recover were
performed before activating their control. This is assisted state coverage, not
proof of walking the motif witness. An earlier exploratory placement put the
capsule at the west wall and restarted the runtime; those results were discarded.
The completed run places beside each station toward the room center. An initial
unbounded large readout exceeded the debug output channel; final UI readouts are
bounded and the complete matrix was rerun successfully.

## Scope

Engine `OverlapCapsule` rejects occupied endpoints. Engine `CastCapsule` measures
the player's standing 1.75-height, 0.30-radius body in the actual admitted static
mesh collision projection. A start contact, nonconverged cast, or query error
reports unavailable rather than a passing barrier. Probe positions derive from
the candidate, not the aperture. No collision, path solver or input transport is
implemented downstream.

This demonstrates one side-gate bypass and repair. Horizontal samples do not
exhaustively certify navigation. Jump/climb, crouch-only passage, destruction,
and movable-prop bypasses are untested. Engine collision queries and native
controller evidence are separate layers.

## Verification

- `dotnet run --project tests/Workbench -c Release`: passed, including immutable
  candidate intent, state-dependent requirements, both gate orientations and
  coverage of the deliberate side aperture.
- UI typecheck and Game Release build/CoreCLR staging: passed.
- Five-sample, four-state Engine runtime matrix: passed as described above.
- Native remote GPU movement comparison: crossed breach, stopped at repair, and traversed preserved start-to-relay passage.

## Native controller comparison

Crew-services Wolf session `0b5eed6f-c175-4c2d-ae35-48e7b13d4013`, slot-1;
Firefox/Gamescope via managed localhost forwarder to the shared LAN demo.
This is the remote GPU lane, not the unrelated local software-renderer harness.
Root operated native input after the independent observer's initial input did
not move the player in inspection mode. Native Tab switched to gameplay mode.
Readout showed `InteractionModeLoss` before Tab and admitted `MappedAxis` after.

All three comparisons used ordinary native `ly=1` controller input for 1500 ms:

- Breach, closed gate: assisted initial pose x=1.05,z=4.5 facing +Z. Player
  crossed the aperture into `goal`, finishing x=1.086,z=15.278, switch still
  closed. Original `side-aperture.png` shows the opening; `crossed-gate.png`
  shows the red goal marker in the foreground.
- Repaired, closed gate: same assisted starting pose and input stopped at
  x=1.05,z=6.185, before the gate front z=6.5. `repaired-gate.png` shows the
  close wall. The standing radius/skin explains the stopping offset.
- Preserved intended route: ordinary Enter reset, then the same input reached
  `relay` at x=10.778,z=0.037 through the start-to-relay corridor. No geometry
  changed for this run. `preserved-route.png` records its end view.

`native-states.json` binds these observations to candidate, requirement-state,
realization and collision identities. `native-receipts.json` preserves native
input and capture metadata, including original source paths. Copied PNG bytes
are unmodified. Parent inspected originals; an independent observer also saw
the aperture, goal-marker foreground after crossing, and repaired wall view.
Images alone do not certify collision positions; runtime readback supplies that
separate evidence. This is assisted, bounded traversal, not full motif completion
or unaided navigation, and it does not test jump/climb/destruction/prop bypasses.

Native UI button clicks on the large complex also reproduced the transition:
`ui-bypass-detected.png` shows FAIL 336/340 with four named failed checks and a
red gate/probe overlay; `ui-repaired.png` shows PASS 340/340 and the restored
intended gate. The counts and 276 omitted passing rows are visible. The parent
inspected both original captures. `ui-receipts.json` retains native click and
capture receipts.

Cleanup: the owned Wolf session was stopped; `cleanup.json` and independent
`post-stop-status.json` retain release and pool readback. No local alternate
server or browser was created during this task's controller test.
