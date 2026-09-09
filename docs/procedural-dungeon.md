# Enclosed dungeon test

Open the running CraftSurvive host at http://192.168.1.22:37300/ and select
**Courtyard → Dungeon**. **Dungeon split** renders the same field in 14 m
spatial sections; **Dungeon** keeps the whole shell. **Dungeon layout** removes the stone courses for a
plain geometry comparison. Dungeon views provide Start, Middle, Far room and
Entrance positions; these reposition the ordinary player. Click the game canvas
before moving with WASD/mouse or the native controller.

This first scale test has twelve rectangular rooms, fourteen axis-aligned
corridors, three alternative route loops, and one external entrance. The outer
solid occupies 58×11×44 metres. Its three-metre floor, roof and exterior walls
remain protected when air and recessed courses are carved. Rooms vary in width,
depth and height by Engine keyed seed; the connected graph is deliberately
stable. There are no locked doors, stairs, hydraulic erosion or furnished rooms.

The pure `DungeonLevelPlan` uses the existing Procgen graph/identity library.
`GeneratedDungeonRecipe` composes Engine implicit boxes, union/subtraction and
material regions. Engine owns extraction, mesh resources, collision, rendering
and world-origin rebasing. One continuous carved shell and an exterior landing
are published through existing retained owners. Room lighting, courses, palette
and layout are product policy. No new Engine evaluator or downstream mesher was
needed. The initial measurements below used paired SDK/runtime `0.1.0-dev.623cc45f2b48`, including
the mesh-limit removals and retained-mesh copy improvements.

## Observed results

Packaged CoreCLR host, accelerated Firefox through the existing single-slot
Wolf harness. Times below are the product's generation/resource/collision-build
stopwatch, **not** browser application time, GPU execution or frame time.

| Study | Seed | Cell | Triangles | Vertices | Build seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Layout | 289142818388 | 0.28 m | 7,094 | 4,812 | 0.253 |
| Dressed | 289142818388 | 0.28 m | 103,152 | 67,432 | 0.461 |
| Dressed fine | 289142818388 | 0.16 m | 300,450 | 216,237 | 1.604 |
| Dressed | 11 | 0.28 m | 104,616 | 68,963 | 0.437 |

All four reported zero boundary edges, non-manifold edges, inconsistent winding
and degenerate triangles. Each passed 3,260 source-field clearance witnesses and
124 shell witnesses. These complement the mesh counts; they are not exhaustive
collision, self-intersection or navigation certification. Narrow courses vary
in visibility with sample resolution; the fine setting captures more of them.

Native forward movement crossed the first corridor and continued along the
first row while grounded. In that initial test, the longer run crossed the then-configured 32 m world-origin
rebase without losing the scene/collision. Middle and far room screenshots use
explicit inspection placement, not claimed walkthroughs of all fourteen routes.
A first unfocused controller action produced no movement; clicking the canvas
made subsequent actions reach the player. The GPU lease was released afterward;
The original exercise left Den Serve on Dungeon/normal/seed11 at the start room.

[Evidence index](evidence/dungeon-7892/index.json) retains screenshots, generation
readouts, native receipts and player motion facts. Engine diagnostic capture
used the Engine warning-capture script's checkpoint/findings helpers. It is
Engine-host-only: the GPU harness does not expose complete browser-console
capture. There were recoverable browser/network transitions during explicit
source restaging, and worker timing-observation drops during restaging and fine
regeneration. No Engine errors or diagnostic-ring drops were reported. The
observation loss is tracked by **Engine #7894**; this is not a warning-free or
complete frame-time baseline claim. No timing threshold was added.

## Reproduce

```text
craft.courtyard.study dungeon
craft.courtyard.seed 11
craft.courtyard.detail normal
craft.courtyard.inspect dungeon-0
craft.courtyard.level-plan
craft.courtyard.readout
craft.courtyard.parts
```

Use `dungeon-5`, `dungeon-11`, `dungeon-entry`, or `dungeon-roof` for inspection.
`dungeon-layout` is the bare study; `coarse`, `normal` and `fine` select extraction
spacing. Changing a study queues generation on the next product update.

Validation passed: Release product build, UI boundary/type check and Procgen
tests. New focused tests exercise deterministic layout, graph connectivity,
route endpoints and room containment at minimum/midpoint/maximum keyed draws.

## Spatial presentation comparison (#7896 / #7897)

`dungeon-split` uses the same seeded field, extraction settings and original
collision mesh as `dungeon`. After extraction, Engine `Graphics.PartitionMesh`
assigns complete triangles to 14×14×14 m bins with origin (-7,-1,-7). Each output
is an ordinary mesh resource and appearance. Normals, UVs, colors, material
slots and triangle positions are copied exactly; shared vertices are duplicated
where necessary. Actual section bounds include triangles crossing cell edges.
There are no caps, independently meshed room volumes, or changes to voxel data.
The source topology readout still describes the complete mesh: section boundary
edges are intentional presentation boundaries, not holes in the assembled dungeon.

This is frustum culling, not room/portal occlusion. Geometry behind a wall can
still be submitted. More visible sections also mean more material-group draws;
matching materials do not automatically batch different geometries. The product
readout reports `renderSections` separately from its original `parts` and
triangle counts. The original mesh stays retained for collision, so this first
comparison favors isolation over reducing CPU mesh storage.

The normal product rebase threshold is now 1,024 m instead of 32 m. This avoids
rebases during ordinary dungeon traversal; it does not by itself establish the
cause of the previously observed hitch.

### GPU comparison

Final pair: `0.1.0-dev.5c71990e89f8`. Seed11, same camera coordinates and normal
lighting/shadow settings, accelerated Firefox, 1276×590 backing canvas.

| View | Whole submitted triangles / draws | Split submitted triangles / draws |
| --- | ---: | ---: |
| Start room, east, normal | 209,234 / 9 | 125,718 / 77 |
| Middle room, west, normal | 209,256 / 10 | 79,907 / 51 |
| Start room, east, fine | 588,710 / 9 | 342,359 / 85 |

These are renderer submissions across passes, not unique visible triangles.
The actual source mesh remains 104,616 triangles at normal and 294,354 at fine,
with zero source topology defects and unchanged clearance/shell witnesses.
Splitting yields 31 visual sections including the landing. Switching back to whole
returns GPU geometry resources from 36 to 5; restoring split returns them to 36.
Screenshots from matching views preserve the same surfaces without observed
new seams. Complete-triangle and attribute conservation also pass Rust tests.

Short captures showed roughly 11–14 Hz submission rates in both modes, with no
GPU timer query available (`completionOnly`). They do not establish a speedup;
the useful measured result is 40–62% fewer submitted triangles at the cost of
more draws. Fine source generation/resource/collision build took 1.549 s whole
and 1.624 s split in this run, not a GPU/frame benchmark.

A native 6.5-second forward hold crossed three corridors to local x=45.892 m,
grounded, without the old 32 m rebase. This confirms the threshold behavior, not
that every intermittent hitch has been eliminated. The harness was released;
the demo is left on Dungeon split/normal/seed11/start room.

[Comparison and evidence](evidence/dungeon-partition-7897/index.json) includes
raw snapshots, warmup observations, matching screenshots and native action
receipts. Preliminary captures used grid origin Y=0 and produced 57 sections;
moving the origin to Y=-1 avoids splitting the floor into another vertical layer.
Source restaging caused recoverable stale-binding/browser transitions. Fine
regenerations also caused recoverable browser reattachments and timing
observation drops, recorded under Engine #7894. Final diagnostics had 0 errors,
0 ring drops and no additional events after normal was restored. This is not a
warning-free full browser-console or timing capture.

Verification: generated bindings, attributed-triangle conservation and native
mesh ownership/recovery tests, focused Clippy, UI check, packaged Release build,
and Engine CI passed. The Engine pair was verified and published together.

### Worker delivery follow-up — Engine #7894

The current installed pair is `0.1.0-dev.538724836d65`. Timing observations now
wait behind their associated worker output, and reconnect history retains whole
publications so a later progress pulse cannot truncate a large mesh transfer.
The earlier captured warnings above remain historical evidence; the follow-up
and its limits are recorded in [worker delivery evidence](evidence/worker-delivery-7894/README.md).
