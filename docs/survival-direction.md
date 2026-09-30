# Survival direction: what a Minecraft-like game requires here

This is the durable design record for CraftSurvive as an adventurer RPG on a cubic world.
It states the target, the settled decisions, and the boundary with the Engine; what the
product has built is in [csharp-migration-map.md](csharp-migration-map.md).

Work state does not live here. Den campaign **#8595** (slices #8596-#8606) is the
work record of record, with upstream requests in the `rusty-engine` project; live
evidence is in [live-proofs.md](live-proofs.md). The assessment-era sections of this
document — the current-state survey, the capability finding, the upstream request
list, the first slice sequence, the unchosen exploration-first variant, and the
evidence appendix — are in Den as `history/survival-direction-campaign-record`, and the
module-level survey that was §4 is `history/survival-direction-module-survey`.

- **Direction.** A cubic-world adventurer RPG: exploration and encounters primary,
  crafting and survival secondary, slow and irregular bomb-driven manipulation rather
  than per-block mining, a finite ~100 km² streamed world, static water with swimming
  and drowning, vertical authored dungeons behind a load transition, and the old
  experiments retired to an authoring lane. §10.
- **Target.** §2 states the design acceptance.
- **Decisions.** §6 records the settled content floor, manipulation model, world
  extent, save policy, surface mode and water path, plus the constraints the live
  proofs established.
- **Boundary.** §8 records what stays, what retires and what is refused.

Section numbering is kept from the full record so citations in Den tasks stay valid;
numbers missing here live in the Den records above.


## 2. The target, stated as design acceptance

"More proper survival MC clone" needs to be testable, so this is the design
target in acceptance terms. Each line is a property of the shipped game, not a
task.

1. **Unbounded-feeling world** (conditional on the §6 world-scale decision; the
   finite option satisfies this by *scale and border policy* instead). A player
   can walk in any horizontal direction for hours without reaching a boundary,
   with terrain variety (biomes, 3D caves, ores, water) rather than an arena
   recipe.
2. **A block/content model.** A registry of block types carrying properties
   (solidity, hardness, tool class, drops, blast resistance, flammability,
   friction, footstep material, map colour, light emission/attenuation,
   orientation, replaceability) and a world representation that can hold
   per-block state (door open/closed, chest contents, crop growth, furnace
   progress).
3. **The gather → craft → build → survive loop.** Break/place with correct drops
   and tool gating, inventory and hotbar, crafting recipes with and without a
   station, tool durability, storage containers.
4. **Survival pressure.** Health, hunger, damage sources, regeneration,
   day/night, hostile spawns, death and respawn.
5. **Creatures.** Passive and hostile mobs with spawn/despawn rules, navigation,
   senses, simple behaviour, combat, drops, and animation.
6. **Continuity.** A single world save that restores terrain edits, block
   entities, creatures, the player (pose, inventory, stats, effects), and world
   time, with a stated schema/version policy.
7. **Readable feedback.** HUD, inventory/crafting screens, audio, particles, and
   lighting that make state legible without debug commands.
8. **Budgeted performance.** A stated chunk-throughput, tick-time, memory, and
   view-distance budget that the streaming design is verified against.

**The V1 content floor.** "Proper" needs a number, otherwise Slices 2, 4 and 6
cannot be priced and the lighting decision (§6) cannot be judged. Proposed
floor, to be confirmed as a decision rather than assumed: roughly **30–40 block
types** spanning wood, stone, ores, glass, and the functional set (crafting
table, furnace, chest, door, bed, torch), **30–50 items** including tools in
three or four tiers, food, and fuel, **2–3 biomes**, **3–5 mobs** with at least
one hostile and one passive, and one dimension. Anything beyond that floor is
content scale (Slice 6), not a different design.

Explicit non-goals at this level: multiplayer, redstone-grade simulation
(a binary on/off floor for doors and levers is in scope), a mod API, enchanting and brewing, and a
large content catalogue beyond the floor above. Those are scope decisions
(§6), not oversights.


## 6. Decisions this direction depends on

Every decision this direction owed is settled. This section is the durable record of
what was decided and what bounds it.

| Decision | Settled answer | Consequence |
| --- | --- | --- |
| Content floor | 12-16 blocks, 6-10 items, 2 creatures, one biome family plus cave and dungeon tilesets | The atlas is the binding constraint, not lighting: every block type costs a region in one atlas image |
| Manipulation | Adventurer manipulation: place blocks and detonate charges; no general break-and-collect | Charges and blasts stay; any general break-time table goes; block-breaking survives only where a slice names a target |
| World extent | Finite ~100 km² (10 km x 10 km at one-metre voxels, 625 x 625 chunks per layer) with an authored hard border | Residency is on demand; navigation is published per box rather than for the whole extent |
| Save policy | Worlds are disposable: version the envelope, detect a mismatch explicitly, discard and regenerate, keep one previous backup | Every key is in `SaveManifest`; a save for another seed or generator version is discarded and kept as the key's backup (`SaveRestore`) |
| Surface mode | Greedy cubes now, dual contouring as a later projection | Per-cell orientation, rotation and growth stage are GreedyCubes-only, so cubes-first is a dependency rather than a preference |
| Water and movement | Static water with Engine swim mode; no water material needed for behaviour | The product supplies the volume and owns breath and drowning; only the appearance needs authored tiles |

Multiplayer stays outside the initial scope, and enchanting and brewing are explicit
V1 non-goals.

### 6.1 Constraints the live proofs established

These are settled too: they bound the decisions above rather than remaining open
questions. Evidence is in [live-proofs.md](live-proofs.md).

- **One atlas per voxel scene.** Every block material needs its own region in that
  scene's atlas image, and a material whose surface resolves through a second atlas
  fails the directional projection.
- **Water behaviour is separable from water appearance.** Swim mode, immersion and
  `HeadSubmerged` work from a product-supplied volume with no water material at all.
- **Navigation must be published.** A world without a collision-derived navigation
  projection answers every path query with `ProjectionUnavailable`, and query cells
  are relative to the published box.
- **A dimension is a second session.** One can be created, filled, read and disposed
  inside the running product without disturbing the loaded world, so a dimension load
  is a product concern rather than an Engine request.
- **Residency admission is applied in place.** The product composes chunk payloads and
  the Engine admits them within the update; there is no background preparation to poll
  or commit, so the residency budget is the product's per-update operation count.
- **A body inside a solid cell stops the character controller.** The controller refuses
  to step it and the product faults, so the product only ever places the player where a
  standing body fits.


## 8. Keep, retire, refuse

**Keep.** The product/Engine boundary and its vocabulary; Read → Decide → Apply
→ Publish; edits admitted in full before the Engine is asked; receipts and
evidence; the rule that a missing capability is a valid result.

**Retired.** The Courtyard/Stoneworks studies, the procgen workbench and artifact bank,
the scene switch and the arena traversal recipe left the product repository (#8901);
Den's `history/authoring-lane` records where each went. The procgen artifact bank
remains a possible *content source* for authored interiors, from its own repository.

**Refuse.** A second renderer, a custom transport, product-side P/Invoke, UI-held
gameplay state, per-voxel state smuggled through material slots, and any
downstream substitute for a missing Engine capability. Of the original G1–G8
requests, only flowing fluids remains unimplemented; if flowing water or
multiplayer matter, the request is upstream, not a workaround.


## 10. Chosen direction: adventurer RPG on a cubic world

The assessment sections, the exploration-first variant (§9) and the module survey (§4)
are in Den; this section records the direction actually chosen and how it re-weights them.
The durable work record is the Den campaign **#8595** with slices #8596–#8606
and upstream requests #8607–#8612.

**The decision.** Cubic world only (`GreedyCubes`); DC terrain and the mixed
mesh+voxel session gate stay future work (§4.12 and §9 in the Den records). The game is an RPG
first: the player is an adventurer, not a miner; exploration, encounters, and
progression are primary; crafting and survival are secondary activities; world
manipulation still exists but is deliberately slow and irregular. The world is
finite but very large — order 100 km² — so streaming, residency budgets, and a
generated-chunk cache are still required, while an infinite-world generator
contract is not. Dimensions are in scope as *authored dungeon interiors reached
from the world through a load transition*, not seamless portals, and those
interiors are **vertical by design**: stacked levels joined by shafts, ladders,
bridges, stairs, and drops, in the spirit of the original Daggerfall donor levels.
Water is in scope as a common adventuring feature — lakes, rivers, and coastal
water as a static, non-solid, translucent material with swimming and drowning —
while *flowing* water is not assumed. Multiplayer stays deferred as long as
possible. Enchanting and brewing are out. The existing Courtyard/procgen
experiments retire to an authoring lane rather than the boot experience, and the
three tentative procgen expansions are folded into that lane as authoring work.

**What water needs, and what it does not.** Two separate things. For **looking**
like water, the flags exist at two layers: `AuthoredMaterialInput` separates
`Solid`, `Collidable`, and `Occludes`, the authored voxel-surface input carries
an alpha mode (`Opaque`/`Mask`/`Blend`) with `AlphaCutoff`, and voxel slots bind
to a render material — the layer where `DoubleSided` lives. For **behaving** like
water, the Engine made swimming first-class (#8609): the product selects the
swim mode on `CharacterControllerCommand.Movement`, supplies the water volume as
an environmental AABB with buoyancy and drag, and reads immersion and
`HeadSubmerged` back from `CharacterStepReceipt.Movement`. The Engine owns the
solver; the product owns the volume, the breath timer, and the drowning
consequence. That replaces the earlier plan of detecting submersion by reading
the voxel under the player and composing buoyancy by hand.

What remains unproven is the *rendering and passthrough* half — that a voxel
marked non-solid, non-collidable, and non-occluding still emits a visible surface
while the character passes through it, that a blend voxel surface sorts
correctly in the GreedyCubes path, and that double-sidedness is reachable for a
voxel-bound material. Those are S0's (#8596) staged checks, not upstream requests;
whatever fails becomes the upstream request instead of a downstream workaround.

*Flowing* water — currents, spread, source blocks, buckets — remains a genuinely
new Engine capability and is deliberately unfiled, because static water is the
working assumption. Underwater tint or fog still has no dedicated mechanism: the
lighting work provides a CPU direct-light readout rather than a rendered
underwater effect, so it stays a known gap rather than an assumed feature.
Vertical dungeons no longer depend on unlanded work — the climb half of #8609
shipped — so ladders and rails are usable now; keep a non-climbing fallback only
as level-design redundancy, not as a schedule dependency.

**How manipulation works, and why it matters here.** Blasts and similar
infrequent, high-impact actions arrive as one bounded revisioned edit
transaction, with dust, smoke, and debris presentation deliberately covering the
remesh and presentation update; base building stays rapid and blocky. Two
consequences follow. First, the strongest objection to smooth terrain in the module survey —
that edits are the core verb and DC remesh is expensive — is **mitigated by
design, not dissolved**: blasts are rare, large, and FX-covered, so the frequency
pressure goes away, but each blast still pays per-dirty-chunk remesh cost, and no
voxel-session DC remesh measurement exists. What keeps DC deferred is therefore the content/selection work and
the deferred session gate, not a claim about edit cost. Second, the threading
pressure drops: step-budgeted generation with a disk cache and tolerable pop-in is
the answer the product took (`TerrainChunkCache`, `MaximumResidencyOperationsPerTick`).

**Per-world authored materials.** A finite world makes an old technique viable
again: instead of one global atlas trying to cover every world, a world can carry
its own bounded material set, baked once and reused. In this Engine that is
authored content — hash- and version-pinned material, texture, atlas-region, and
voxel-surface entries (`AuthoredVoxelSurfaceInput`) — published through the
content store as an artifact. The store's artifact roles already name voxel
assets, voxel objects, scene documents, and prefab registries; whether a material
set needs a shape of its own is part of the S0 proof rather than assumed here. It
would remove any runtime material blending or "which grass is this world's grass"
problem, and make a world's look a stable, inspectable artifact. The costs are a
per-world asset to generate and store, and regeneration when the material set
changes. It is an option to validate, not a promise: S0 proves one world-scoped
material set end to end, and S2 consumes it only if that proof is clean. The
current implementation remains one global hash-pinned atlas.

**What this re-weights from the module survey.** Shrinks or drops: ore distribution and
progression, mining-speed and tool gating, drop economy, *flowing* fluids, and
the large edit overlay (bases fit comfortably in the existing 65,536-entry
budget — to be confirmed once bases have a stated size). Static water and the
swim/climb request are back in the initial scope, so the static-water default is a
settled decision rather than a fallback. Moves to the centre: point-of-interest
generation and discovery state (new product state with no home in the tree
today), encounters and combat, the dungeon authoring and load path — now with
verticality as its defining shape — and
presentation that makes an unknown world readable. Stays with a different owner:
survival pressure becomes expedition supply; creatures and progression become the
core rather than one slice among many; lighting becomes the most valuable
upstream gap because dungeons and caves are the product's set pieces (#8607).


