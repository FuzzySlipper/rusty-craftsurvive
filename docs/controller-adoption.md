# FPS controller adoption

CraftSurvive is the durable proving consumer for Rusty Engine's reusable kinematic FPS controller.
The game calls the complete Engine facade directly; it does not copy the solver or retain a second
renderer/TypeScript collision path. The first adoption was validated against Engine revision
`9142e7bf77a909c6657bb1c69abda77a6a6b4434`. That revision is certification evidence, not a
dependency pin: the adjacent Engine path remains rolling-current.

## Ownership and controls

Engine owns the accepted entity-center transform, character-motion continuation facts, capsule
casts/overlaps, movement and collision response, ground/slope/step classification, crouch
clearance, platform support/carry, recovery, and external-motion integration. Its companion look
service owns the canonical yaw/pitch basis.

CraftSurvive owns the 120 Hz call cadence, camera eye offset, semantic bindings, game-feel tuning,
moving-platform schedule, and the meaning of the `H` impact action. Browser and native inputs map
to the same `PlayerInput`: WASD movement, Space jump, Control crouch, Shift sprint, H impact, and
mouse/arrow look.

## Checked tuning

The source-owned tuning constructor is `craftsurvive_controller_config()` in `src/player.rs` and is
validated before player construction. Principal values are:

| Area | Product value |
|---|---:|
| Standing / crouched height | 1.75 / 1.00 |
| Radius / contact skin | 0.30 / 0.015 |
| Walk forward / backward / strafe | 7.0 / 7.0 / 7.0 |
| Sprint forward / backward / strafe | 8.0 / 8.0 / 8.0 |
| Ground acceleration / braking / friction | 48 / 58 / 9 |
| Air acceleration / cap | 10 / 7 |
| Gravity / jump / terminal fall | 24 / 8.5 / 24 |
| Maximum slope | 50 degrees |
| Maximum step / floor snap | 1.05 / 0.25 |
| External decay / H impact | 3.0 per second / 5.5 lateral + 2.5 lift |

## Evidence and diagnostics

Focused Rust consumer tests cover command mapping, normalized diagonal motion, canonical look,
jump, crouch and blocked stand, wall slide, one-voxel step facts, active moving-platform carry,
external impulse separation, and live edit reconciliation. The deterministic browser campaign adds
physical pointer lock, ten-command cardinal/diagonal comparison, crouch/stand, jump/land, wall and
ledge behavior, edits, renderer-visible changes, collision diagnostics, and H-impact response.

The HUD projects accepted pose/velocity, stance and blocked stand, ground source/normal,
contacts/blocks, step facts, platform identity/displacement, cast/recovery counts, and the
collision-world hash. These are observations of Rust authority, not browser gameplay state.

The Luna/max route in `product-playtest.scenario.json` requires an exit interview distinguishing
harness/control difficulty from game defects and asks explicitly about feel, clipping,
camera/collider disagreement, latency, snagging, slopes/steps, platform behavior, and edit recovery.

## Game-feel certification

Den task 6849 owns the indexed playtest artifacts and reviewer-visible exit interview. The final
product behavior was certified at CraftSurvive revision
`21bc767ababb409b619e7ecd2ad262063b4413b5` in two same-revision sessions:

- `rusty-craftsurvive-playtest-20260812T055214.182589316Z-1022575` ran the documented
  `grounded-voxel-loop`. Its visible route covered pointer look, straight/diagonal walk and sprint,
  jump/landing, crouch/stand, wall/corner slide and snagging, the identifiable trench/step route,
  and brush 1/2/3 edit recovery. It persisted the requested structured exit-interview fields.
- `rusty-craftsurvive-playtest-20260812T054157.110336028Z-987006` supplied the final delta for the
  broker navigation limitation in the broad run: visible moving-platform support, H departure, and
  lower-floor landing without the previously reproduced runtime rejection.

The broad operator could not identify a literal smooth slope in the voxel presentation and did not
claim a subjective smooth-slope judgment. Deterministic Engine-consumer tests remain the mechanism
evidence for legal and over-limit true mesh ramps; that distinction is a known visual-route limit,
not a substituted playtest claim.

## Known boundary

The canonical voxel island has box collision even when MC/DC presentation is selected. Engine's
true static-mesh slope mechanism is covered by upstream and direct consumer tests; CraftSurvive
does not make its reconstructed render triangles authoritative collision. See
`docs/known-limitations.md` for the remaining bounded product limits.
