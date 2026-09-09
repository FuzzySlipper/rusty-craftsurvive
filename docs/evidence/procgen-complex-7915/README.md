# Seeded large complex — #7915

The candidate and Engine realization remain in CraftSurvive. This slice adds a
bounded 36-room, 45-route grid complex (10 independent loops), varied rectangular
room sizes, four switch-gated edges in the retained seeds and one control action.
It is still one floor; vertical routes/mixed chamber forms are tentative #7916.

Engine pair: `0.1.0-dev.538724836d65` (upstream LAN fix already adopted).
Samples: `content/procgen/complex-29.json` and `complex-83.json`, with offline
receipts. The model finds completion with 71 reachable room/switch states and no
unrecoverable states for both retained samples. This is a bounded product graph
experiment, not a general solver.

## Coverage

Mesh probes use nine rays per route and per omitted neighboring grid connection.
A missing edge between distant rooms is not treated as a wall: collinear routes
may legitimately form a longer hallway. Probe failures identify room pair,
height and lateral offset. These are scoped straight-ray witnesses, not full
capsule/step/jump/climb/destruction/navigation or exhaustive bypass certification.
The same generated mesh is retained by Engine collision and presentation.

Runtime diagnostics below use explicit placement to exercise both gate states;
that evidence is separate from native walking. The Wolf launch uses a loopback
TCP forwarder, so its GPU/browser evidence is localhost-origin even though the
server/profile URL is on the LAN. See `launch-origin.json`.

Final runtime, native observation and cleanup results are recorded below.

## Final source and runtime checks

Workbench checks passed for eight seeds including both retained samples and
`ulong.MaxValue`: same-seed determinism, topology variation independently of seed
metadata, closed/open connectivity, gates, strict artifact roundtrip, witnesses,
behavioral failure traces and malformed inputs. Existing Procgen checks, Tool
self-check, UI typecheck, Release build and CoreCLR staging passed. Independent
source review found no further blockers. No NativeAOT/release claim.

`runtime-states.json` captures both retained seeds with gates closed and open.
Closed: 369/369 route rays clear, 171/171 gate/wall rays blocked. Open: 405/405
route rays clear, 135/135 omitted-neighbor wall rays blocked. Both have 15-action
model completion witnesses. These are actual generated-mesh spatial queries.
`entrance.json` records reset to sample29 for native evaluation.

An earlier diagnostic incorrectly placed the capsule center at floor+0.1,
embedding it in the floor; Spatial.ProposeCharacterStep failed and the worker
restarted. Those interrupted probes are not passing evidence. Corrected placement
uses floor+1.0 and settles through the existing player owner. Normal loading now
also places the player at the entrance before the next controller step, and Enter
uses the same placement path. No Engine failure workaround was introduced.

Measured host mesh/collision construction (seconds exclude browser readiness):

| Seed | Gate state | Triangles | Vertices | Seconds |
| --- | --- | ---: | ---: | ---: |
| 29 | closed | 30,544 | 32,270 | 0.992 |
| 29 | open | 30,276 | 31,608 | 0.901 |
| 83 | closed | 30,730 | 32,273 | 0.982 |
| 83 | open | 30,636 | 31,642 | 0.966 |

Each realization uses one blockout mesh plus the control and goal marker meshes.
The room envelope is approximately 85 × 80 world units (plus the enclosing rock
shell). Seed29 has 3 dead ends and 19 degree-three-or-greater rooms; seed83 has
4 dead ends and 21 branching rooms. The existing CI terrain job now also runs
`tests/Workbench`, including the larger generator regression cases.

## Independent native evaluation

The playtester operated the existing owned Wolf session and reported **pass**
for the bounded mission. It inspected compact and expanded Layout Fit overviews, expanded
Graph Fit, Graph 2× centered on selected r13, and Layout 3×. Selected-room routes
and bounds were readable. The initial handoff mislabelled the compact Layout
capture as expanded; root inspection of the original corrected that label.

Native controller traversal reached `start → r01 → r07 → r13 → r12 → control`,
including turns and branch rooms. A native Y press at the visible station opened
the physical gates. `native-control-readout.json` independently records physical
control-room membership, switch open and passing post-rebuild mesh probes.
The entire goal route and every loop were not walked. The test used supplied map,
witness and position-readout guidance; it is not blind exploration acceptance.
Root also inspected the original graph/detail/world/station images. The world
shows continuous corridors with changing widths, visible room openings and the
green activated station. Stills alone do not prove collision or performance.

The four adjacent PNGs are unchanged copies of service originals, not edited
or reconstructed images. Engine diagnostics retained browser transition and
request/renderer-unavailable warnings, but no error-severity entries in the final
host interval; see the summary. This is separate from browser-console capture.

`native-captures.json` retains the 16 capture metadata records for the owned
session. Some observer images came from `observe`, which returned only an
artifact ID and PNG path; those are not fabricated capture IDs. Expanded Layout
Fit is observe artifact `fb1b6888-bf51-4dec-a49d-4fcaa3b9c46c`, copied unchanged
as `layout-overview.png`. World corridor is observe artifact
`e9c9ca1c-1aa6-4d80-b1fe-e4cebdd752d6`. Both originals are under
`/home/dev/dsh-crew/experiments/wolf-den-srv/controller/state/4ac0155f-db3b-4f16-b824-8f6ce45f3d72/`.

The worker's inline stop receipt reported released=true, local capture stopped,
no errors at 2026-09-09T09:47:02Z. No standalone cleanup file was returned;
`post-stop-status.json` is root's subsequent authoritative status snapshot showing
no session, lease, lobbies or target sessions. The worker's native input/curl
results remain in its transcript, not separately reconstructed receipt files.
Den handoff message: 32534. The adjacent native readout was independently saved
by root after activation.
