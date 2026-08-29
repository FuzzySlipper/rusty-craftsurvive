# C# controller adoption

CraftSurvive's first-person controller is product-owned C# composition over
Rusty Engine's generated character, look, camera, and world-origin services.
The former Rust controller was semantic donor material and is now available
only in Git history.

## Ownership and controls

Engine owns the accepted entity transform, character-motion continuation,
capsule casts/overlaps, movement and collision response, ground/slope/step
classification, crouch clearance, platform support/carry, recovery, external
motion integration, camera resource, and atomic origin mechanism. Its look
service owns the canonical yaw/pitch basis.

CraftSurvive owns input interpretation, the 120 Hz call cadence, camera eye
offset, semantic bindings, game-feel tuning, moving-platform schedule, exact
global player/platform positions, and the meaning of the `H` impulse action.
The product registers its local player and platform roots and decides when to
request a rebase; Engine performs the rebasing mechanism.

The active bindings are:

| Input | Product meaning |
| --- | --- |
| WASD | planar movement |
| Space | jump |
| Control | crouch |
| Shift | sprint |
| H | lateral impulse with lift |
| mouse movement | first-person look |
| F / left mouse | destroy targeted voxel |
| G / right mouse | place selected material |
| 1 / 2 / 3 | brush radius 0 / 1 / 2 |

The browser host and native boundary deliver physical input to the product;
the DOM companion does not synthesize gameplay state.

## Product tuning

Controller values live in `Modules/Player/PlayerConstants.cs` so behavior can
be found and changed without hunting through the integration code. The
current values are:

| Area | Product value |
| --- | ---: |
| Standing / crouched height | 1.75 / 1.00 |
| Radius / contact skin | 0.30 / 0.015 |
| Walk / sprint speed | 7.0 / 8.0 |
| Ground acceleration / braking / friction | 48 / 58 / 9 |
| Air acceleration / cap | 10 / 7 |
| Gravity / jump / terminal fall | 24 / 8.5 / 24 |
| Maximum slope / step / floor snap | 50° / 1.05 / 0.25 |
| External decay / impulse | 3.0/s / 5.5 lateral + 2.5 lift |

These are product policy values, not a replacement for Engine collision
guarantees.

## Boundary and evidence

The C# controller retains no native pointer or renderer implementation. It
passes typed input, transforms, support facts, and call-local platform
obstacles through the generated safe Engine facade. If a future controller
feature cannot be expressed through that facade, file the exact Engine
capability request and stop that downstream slice.

Focused C# build/publish checks establish that the current boundary compiles;
they do not claim a broad browser or subjective game-feel certification. The
current behavioral limits are recorded in [`known-limitations.md`](known-limitations.md).
