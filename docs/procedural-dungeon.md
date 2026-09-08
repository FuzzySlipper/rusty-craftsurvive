# Enclosed dungeon test

Open the running CraftSurvive host at http://192.168.1.22:37300/ and select
**Courtyard → Dungeon**. **Dungeon layout** removes the stone courses for a
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
needed. CraftSurvive now uses paired SDK/runtime `0.1.0-dev.623cc45f2b48`, including
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
first row while grounded. The longer run crossed the existing 32 m world-origin
rebase without losing the scene/collision. Middle and far room screenshots use
explicit inspection placement, not claimed walkthroughs of all fourteen routes.
A first unfocused controller action produced no movement; clicking the canvas
made subsequent actions reach the player. The GPU lease was released afterward;
Den Serve remains running on Dungeon/normal/seed11 at the start room.

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
