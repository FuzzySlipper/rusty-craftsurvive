# Survival direction: what a Minecraft-like game requires here

This is a design-level assessment, not a plan of record and not an
implementation task. It answers one question: **if CraftSurvive should become a
proper survival game rather than a procgen testbed, what has to exist, who owns
it, and where is the Engine boundary?**

## Summary

- **Where it is.** A capability testbed with a real game shell and a thin game:
  a 96 m bounded voxel arena in an opt-in mode, a procgen artifact bank, an art
  study scene as the default, and no inventory, survival stat, creature,
  crafting, time-of-day, or audio anywhere in the product. §1.
- **What that means.** The Engine SDK already ships the mechanism layer —
  entities/components, items/inventory/equipment/stats/effects, navigation,
  perception, a bare FSM, dynamics, lights, audio, animation, presentation,
  persistence, scheduling, deterministic RNG, and a variant of the world-spine
  work already specified in Den #6853. The work is therefore mostly product
  policy, content, and authority design, not systems engineering. §3.
- **What the product must build.** A real world model and block/content
  registry, budgeted streaming, the item/crafting loop, survival rules,
  creatures, a time/light policy, a versioned save envelope, a HUD, and
  performance gates. §4.
- **What must go upstream.** Eight named Engine gaps, of which per-voxel
  lighting (G1), per-voxel block state (G2), time-of-day sky (G3), and movement
  modes like swimming (G8) bound fidelity; multiplayer (G4) and the threading
  contract (G5) bound scope. §5.
- **What blocks the start.** The decisions in §6 — scope, world scale,
  dimensions, content floor, lighting, water, surface mode, save policy — plus
  one staged proof that the mechanism layer is actually usable from this product
  (§7 Slice 0). Until the threading contract exists, chunk generation stays
  step-budgeted on the update thread; a product-owned worker is a forbidden
  substitute, not a plan.
- **A variant raised later, and it is the cheaper one.** §9 records an
  exploration-first concept that drops mining and keeps blocky construction as a
  minor, rapid verb over smooth terrain. It is *potentially* less constrained by a
  blocky prototype than the main direction is: mesh-collision terrain and a voxel
  build session share one session handle — used exclusively per scene mode today,
  never simultaneously — and the concept reuses this repository's strongest
  existing lane. Its gate is two staged verifications, not a design breakthrough.
- **What was chosen.** §10 records the settled direction — a cubic-world
  adventurer RPG with exploration and encounters primary, crafting and survival
  secondary, slow/irregular bomb-driven manipulation, a finite ~100 km² streamed
  world, static water with swimming and drowning, vertical authored dungeons
  behind a load transition, and the old experiments retired to an authoring lane
  (procgen tasks included). Den campaign **#8595** (slices #8596–#8606) is the
  work record, with upstream requests #8607–#8612 in `rusty-engine`; #8609 (swim
  and climb) is active rather than parked.

Evidence base: the checked product at commit `a4ac8ad`, the installed SDK pair
`0.1.0-dev.b9c281937b26` (public surface inspected by reflection: 1,126 public
types), and the Den task ledger for `rusty-craftsurvive` (77 tasks: 66 done,
8 cancelled, 3 planned). Per `AGENTS.md`, an absent Engine mechanism is listed
as an upstream request in §5 rather than designed around downstream.

## 1. What the project is today

CraftSurvive is a **capability testbed with a game-shaped shell**. The shell is
real: a lifecycle product, a first-person controller, a revision-checked edit
path, persistence, UI projection, debug commands, CI, and an evidence culture
with receipts. The *game* is a set of bounded studies.

| Lane | What it owns today | State |
| --- | --- | --- |
| Default scene (`CRAFTSURVIVE_SCENE` unset or `courtyard`) | `CourtyardScene` implicit-mesh art studies: stoneworks, masonry layers, material boundaries, stepped and volume-carved caves | Not an editable voxel world. Whole-scene regeneration |
| Voxel lane (`CRAFTSURVIVE_SCENE=traversal`) | `TerrainWorld`, `TerrainRecipe`, residency, edits, overlay persistence | A 96×96 arena of height noise plus hand-placed traversal fixtures |
| Level generation | `ProcgenWorkbench`, `CaveLevelPlan`, `DungeonLevelPlan`, `CraftSurvive.Procgen`, artifact bank | Rooms/routes carved into a bounded rock solid: 6-room cave, 12-room dungeon, 36-room complex |
| Presentation studies | `GhostPlateActor` (GLB wizard), `MicrovoxelPresentation` (`.vox` shrine), `RopePlayground` | Art and mechanism demonstrations |
| UI (`src/ui/main.ts`) | Live-debug panel, Ghost Settings, procgen workbench, rope controls | A testbed console, not a game HUD |

Out of the box — `rusty dev` with no environment — the product boots into the
Courtyard scene, which is **not** an editable voxel world: in that mode voxel
residency, presentation and persistence are all disabled and terrain edits
cannot produce a voxel hit at all. The voxel numbers below describe the opt-in
`CRAFTSURVIVE_SCENE=traversal` lane. Any reader planning voxel work should assume
the arena is one environment variable away, not the default experience.

Concrete limits of the voxel lane, from
`src/CraftSurvive.Game/Modules/Terrain/TerrainConstants.cs`:

- World: `DefaultSize = 96` (max 128) with a footprint of ±Size/2 (±48 m at the
  default, ±64 m at the maximum), a vertical band of `-9..28`, and three
  materials (grass 1, dirt 2, stone 3) out of a 4,096-value material space
  (0 = empty, max 4,095).
- Chunks: 16³, requested radius 1 (3×3 horizontal × full vertical band),
  retained radius 2, at most 64 resident chunks, 16 residency operations/tick.
- Edits: reach 8 m, brush radius ≤ 2, sparse overlay capped at 65,536 entries
  and 8 MiB, schema v1, one blob key `terrain/overlay`.
- Generation in the voxel lane is **2.5D**: `TerrainRecipe.ColumnAt(x, z)` computes a
  surface and slope per column, with no biome or structure pass. The Engine's
  `Implicit`/sampled-volume path is a real 3D density mechanism, but it is used
  by the Courtyard mesh studies and produces meshes, not per-voxel materials
  (§3), so it is not the voxel world's generator today.
- Player: 120 Hz controller cadence, 60 Hz product step, 1 m voxels, world-origin
  rebase at 1,024 m, product envelope ±1,000,000 m.

Testbed signals worth naming, because they set the tone for any survival work:
61 `craft.*` debug commands are the primary interface to the product; the
procgen lane's output is an offline artifact bank with receipts and bounded
readouts; and the Den ledger has no survival task — the only open tasks
(#7911, #7912, #7916) are tentative procgen expansions.

Two pieces of housekeeping to fix independently of this direction, both
symptoms of a tree that has been reshaped repeatedly:

- `README.md` and `docs/csharp-migration-map.md` still name pair
  `pair-baf031173e19`, while `src/CraftSurvive.Game/CraftSurvive.Game.csproj`,
  `AGENTS.md`, `.den-serve.json`, and `tests/TerrainResidency` pin
  `b9c281937b26`. The drift is wider than those two files:
  `docs/controller-playtest.md`, `docs/procedural-dungeon.md`, and
  `docs/procgen-workbench.md` also name the old pair, and
  `docs/courtyard-layout.md` still pins revision `2e99efa5cbe9`. Meanwhile
  `.runtime/` retains roughly thirty `pair-*` directories and both packages.
  The documentation set also predates the rope-playground commit, so its
  "current state" narrative is roughly a month behind the tree.
- `tests/ArchitecturalRecipes/` exists on disk as build output only: no tracked
  source, no project file, and no CI lane references it. It should be removed
  rather than mistaken for a live check.
- Two vestigial surfaces are worth clearing before a survival lane builds on
  them: `TerrainWorld.Update()` (fixed-centre residency tick) has no call site —
  residency actually follows the player through `SynchronizeAround` — and the
  product publishes a structured UI projection on `craftsurvive.terrain` every
  update that no file under `src/ui/` consumes. Neither is urgent, but both
  mislead a reader about how the product works today.

### 1.1 This is not the first time the project reached for a world

The Den ledger is prior art and should be mined before anything is designed
again. Two examples matter for the target in §2:

- **#6853, "Extend bounded chunk streaming to unbounded deterministic terrain"
  (done, 2026-08-13)** already specified and accepted the world spine: versioned
  deterministic generation from a seed and signed chunk coordinates, biome and
  material semantics, structures/features, durable edit overlays, save/load and
  generation-version migration, requested-set/priority/prefetch/cancellation/
  cache policy, a documented numeric envelope, and long-traverse memory/frame
  budgets. That capability lived in the lane retired by the C# migration
  campaign (#7491), and the current tree has no trace of it: no biome, prefetch,
  streaming, or generated-chunk cache code exists in `Modules/`. Known
  limitations states the same positively ("no background generation worker,
  generated chunk disk cache, biome framework, or general procgen framework").
  Slice 1 in §7 is therefore a *re-achievement in the C# lane*, not a new design
  — and its acceptance criteria already exist.
- **#6935, "Emit bounded voxel debris when breaking blocks" (done)** adopted
  Engine particles for block break in the retired lane. The current product
  contains no debris or particle code at all, so the feedback half of the item
  loop is a re-adoption rather than a design.

Other ledger entries that remain directly relevant: #6849 (Engine FPS controller
adoption, which already names stamina/damage/audio as product-owned), #7659
(authored terrain atlas and sky on the C# path), #6844 (edit latency and atlas
face orientation), and #7731 (incremental residency that reuses generated
chunks). Registering the retirement precedent is the point: the C# migration
deliberately preserved "the current playable voxel/edit semantics … not every
historical rendering laboratory", so the survival direction is partly a
*restoration* list with a known cost, not a greenfield design.

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
(the on/off floor in §4.4 is in scope), a mod API, enchanting and brewing, and a
large content catalogue beyond the floor above. Those are scope decisions
(§6), not oversights.

## 3. The provisional finding: the mechanism layer already exists

The most important fact for this decision is that **Rusty.Engine already ships
most of the mechanism a survival game needs**. If that holds, the work is
predominantly *policy, content, and authority design* — the product's side of the
line — not systems engineering. The finding is provisional in one specific
sense: it rests on the SDK's public types and constructors, and the product has
not yet admitted a single `Mechanics`, `Entities`, `StateMachine`, or
`Interaction` object through a staged run (the only entity in the tree today is
the player's own one-component store). §7 therefore puts a minimal staged proof
in Slice 0 before Slices 2–4 are treated as conventional.

| Survival need | Engine family (evidence) | What remains product work |
| --- | --- | --- |
| Creatures, components, per-entity state | `Rusty.Engine.Entities`: `EntityStore` (typed components, revisions, containment, lifecycle/tombstones), `Actor`, `EntityGraphicsProjection`, `EntityCharacterController`, `EntityMotionResolver`, `EntityDynamicsAdapter`, `EntityTriggerProjection`, `EntityOriginRebaser` | Mob catalogue, per-mob components, spawn rules, simulation order |
| Items, inventory, equipment | `Rusty.Engine.Mechanics`: `ItemDefinition` (fungible/unique, capacity costs, classifications, equipment policy), `InventoryStore` (`Grant`/`Consume`/`SplitFungible`/`MergeFungible`/`TransferFungible`/`MaterializeUnique`), `InventoryState` with capacity limits, `EquipmentState` and slot definitions | Item catalogue, drop tables, stack rules, UI mapping |
| Health, hunger, stamina, XP | `Mechanics`: `StatsComponent` with `Stat` and `Track` (`Spend`/`Restore`, `MaximumChangePolicy`), contributions (add/multiply/min/max) with sources. These are generic vitals: the SDK contains no Health, Damage, Combat, Weapon, Armour, Death, Hunger, or Food type | Which tracks exist, drain/regen rates, damage model |
| Status effects (poison, regeneration) | `Mechanics`: `EffectDefinition`, `ActiveEffect`, stacking policies and groups, provenance | Effect catalogue and application rules |
| Mob pathfinding | `Rusty.Engine.Navigation*` plus `ISpatialService.RequestNavigationPath`, `EvaluateNavigationStep`, volumetric traversal configs; the voxel scene reports `NavigationRevision`/`NavigationCellCount`. Grid/volumetric search with explicit failure outcomes — no polygon mesh, no crowd avoidance, no path follower, so waypoint following and avoidance stay product-side | Goal selection and movement intent |
| Mob senses | `Rusty.Engine.Perception`: observer/target pair queries with occlusion and facing (`PerceptionPairKind`) | Aggro rules, awareness, memory, target scoring |
| Mob behaviour | `Rusty.Engine.StateMachine`: definitions, instances, transition requests and receipts — a bare FSM with no guards, entry/exit actions, timers, hierarchy, blackboard, or serialization | Behaviour graphs, conditions, timers, and all state payload |
| Physics for drops and props | `Rusty.Engine.Dynamics` via `IDynamicsService` (bodies, chains, tethers; a `DynamicsWorld` comes from `IDynamicsService.CreateWorld`, it is not constructed directly), plus `IKinematicService` and `IMotionService` | Spawn/despawn and pickup policy |
| Torches, sun, ambient | Light is **not** a service: `IGraphicsService.CreateLight`/`UpdateLight` with `LightDescriptor` — `Ambient`/`Directional`/`Point`/`Spot`, range/decay/penumbra, shadow intent; materials carry `Emissive`/`EmissionColor` | Time-of-day policy — and see gap G1 for per-voxel light |
| Sound | `Rusty.Engine.Audio`: clips from content, voices, buses, 3D emitter descriptors | Event → sound mapping |
| Player and mob animation | `Rusty.Engine.Animation`: clips, controllers, graphs, parameters, cues, triggers; animated voxel objects via `IVoxelContentService`. No skeletal skinning, IK, or root motion — animated meshes from content and animated `.vox` objects are the supported paths | Clip selection and state mapping |
| HUD, nameplates, damage numbers | `Rusty.Engine.Presentation`: billboards, structured billboards, meters, status cues, fonts, collision-aware particles; `IUiService` projections for DOM chrome | Layout and information design |
| Block appearance | `AuthoredMaterialInput` (colour, texture, roughness, **emissive**), `AuthoredVoxelSurfaceInput` (atlas region, tile scale, alpha mode/cutoff), `VoxelSceneMaterialBinding` and per-face bindings | Block catalog and atlas authoring |
| World persistence | `Rusty.Engine.Persistence` blobs with revision guards; `ProductStateStore<TState>` with `JsonProductStateCodec<TState>`/`IProductStateCodec<T>`; `VoxelHistoryPersistenceStore.Save`/`LoadAndRestore`; `IContentStoreService` snapshots. No world or entity snapshot format, no migration, no save cadence, no multi-key transaction | Save envelope, migration policy |
| Deterministic worldgen | `IRandomService` keyed/scoped draws (`DrawKeyed`, `CreateScoped`, `ForkScoped`) | Field design and draw discipline |
| 3D density fields (not the voxel path) | `IImplicitSurfacesService`: `CreateField` with box/sphere/ellipsoid/capsule/frustum/plane, union/intersection/difference/smooth-union/offset/transform/`DisplaceWaves`, `Sample`, `Generate` to a `MeshResource`, plus `CreateSampledVolume`/`RasterizeSampledVolume`/`SampleSampledVolume`/`ReadSampledVolume` and enclosure/join/mesh-integrity audits. This is the mechanism the Courtyard cave studies already use through `Rusty.Engine.Implicit`; it is **not** the voxel world's generator because it produces meshes and density lattices, not per-voxel material chunks | Whether to reuse it for structure/cave shaping that is later rasterized into voxel materials, or keep voxel generation separate |
| Scheduled world work | `Rusty.Engine.Application.SimulationScheduler` (the `ScheduleAt`/`ScheduleAfter`/`ScheduleRepeating*`/`WaitSteps`/`WaitUntil`/`ResumeNextStep` family) over `UpdatePipeline`/`UpdatePhase` with `ProductUpdateFacts` fixed-step facts. Both are product-composed — `new UpdatePipeline(engine, phases)`, `new SimulationScheduler()` — since the host injects neither | What is scheduled, and at which phase |
| Voxel world authority | `IVoxelService`: residency operations with material payloads, revisioned `ApplyEdits`, chunk leases, dirty-chunk reads, **undo/redo history with export/restore**, scene readout with collision *and navigation* revisions; `VoxelAnnotationKind` includes `SpawnArea`, `Hazard`, `NavigationHint` | Recipe, admission policy, streaming budget |
| Structure/prop content | `AuthoredPrefabRegistry`, `AuthoredScenePlan`, `IVoxelContentService` (`.vox` assets, animated objects), `StaticMeshInstance` | Templates and placement rules |
| Verification capture | `IRenderOutputService.CaptureImage`/`ExportSceneGlb`, `IDiagnosticsService.Publish`, `EntityStoreDiagnostics`, spatial map snapshots as ASCII/JSON | Evidence automation for the lanes below |

Two consequences follow. First, a survival slice should be designed as
**product policy over named Engine services**, exactly as the current terrain
and player lanes already are. Second, several "missing features" that look like
big engineering (inventory, stats, pathfinding, AI, audio, animation) are
already available and should not be rebuilt.

A caveat that changes Slice 4's cost: the entity, mechanics, and state-machine
types are **product-composed**, and every adapter needs its owning Engine
service and session handed to it. `EntityCharacterController(EntityStore,
ISpatialService)`, `EntityMotionResolver(EntityStore, IMotionService, collider
component)`, `EntityDynamicsAdapter(EntityStore, IDynamicsService,
DynamicsWorld, …)`, `EntityTriggerProjection(EntityStore, ISpatialService,
SpatialSession, …)`, `EntityGraphicsProjection(EntityStore, IGraphicsService)`,
and `EntityOriginRebaser(EntityStore, IWorldOriginService, SpatialSession, …)`.
`IEngineContext` exposes no Mechanics/Entities/StateMachine service — those are
managed types the product news up — and light goes through `IGraphicsService`.
So a mob is integration work against four or five services plus session
lifetime, not catalogue content alone, and Slice 4 should be estimated that way.

## 4. What the product must build

Proposed owners are new domains under `src/CraftSurvive.Game/Modules/`, each with
one mutable state family, following the existing Read → Decide → Apply → Publish
shape.

### 4.1 World and generation (`Modules/World`)

Replace the arena recipe with a world model that has these properties:

- **3D generation contract.** A chunk's contents are a pure function of
  `(seed, generator version, chunk address)`, with cross-chunk features (ores,
  structures, trees) resolved by a documented rule: either chunk-local
  deterministic decisions, or an explicit "write into neighbour" policy. The
  Engine already permits the second: `VoxelEditTransaction` carries a list of
  absolute-addressed `VoxelEdit`s with no single-chunk constraint, and
  `VoxelResidencyTransaction` admits several `VoxelChunkIdentity` addresses per
  revision. What is missing is the *recipe and world model* that decides
  placement, not an Engine capability — so this is product work with a design
  choice, not an upstream request.
- **Draw discipline.** Cross-chunk work must not depend on generation order.
  `IRandomService` keyed and scoped draws (`DrawKeyed`, `CreateScoped`,
  `ForkScoped`) are the intended mechanism: derive per-chunk and per-feature
  streams from `(seed, version, coordinate, feature id)` so two adjacent chunks
  generated in either order agree, and so a generator-version bump invalidates
  deterministically rather than silently shifting the world.
- **Vertical scale.** MC-like play needs a real vertical band (design target
  ~256 m, or an honest smaller number) and chunk addressing in Y, not the
  current `-9..28` slab.
- **Biome/climate field.** A separate low-frequency field selecting biome
  parameters, consumed by the terrain and decoration passes. The Engine's
  sampled-density mechanism (`IImplicitSurfacesService.CreateSampledVolume`,
  §3) is a candidate implementation for *shaping*, but biome selection is
  scalar policy with no mesh output, so plain product-side field evaluation is
  the default; if the shaping side reuses `Implicit`, that is a deliberate
  choice to record, not an omission.
- **Versioning.** `TerrainConstants.GenerationVersion` already exists; a survival
  world needs a written policy for what a version bump does to existing saves.
- **Generated-chunk cache.** Regeneration on every residency request is
  acceptable in a 96 m arena and not in a streamed world: an LRU of generated
  chunk payloads plus a disk cache. The disk cache store must be an Engine
  primitive — `IPersistenceService` blobs, or `ContentStore` for larger
  artifacts — not a product-authored file format; if neither fits generated
  chunk payloads at the required size, that is an upstream request, not a
  reason to write a downstream store. Prior art and acceptance criteria: Den
  #6853, with #7731 for the incremental-reuse detail.
- **Ore distribution** as a first-class generation table (depth band, rarity,
  biome gating, drop, and whether tool enchantments can alter it), owned beside
  the recipe rather than scattered through decoration code. Tool progression is
  ore gating, so this table *is* the progression curve.
- **Random ticks, growth, and renewal.** Sapling→tree, leaf decay, grass spread,
  crop growth, and any bonemeal-like acceleration need one product-owned growth
  policy: which blocks tick, at what rate and radius, on which
  `SimulationScheduler` phase, and — critically — what happens when the chunk
  unloads mid-growth. The scheduler is product-composed rather than injected
  (§3), so the product also owns the phase it drives growth from. Without this,
  wood and food are non-renewable and a long-lived world starves.
- **Dimension reservation.** If a Nether/End-like second dimension is ever
  wanted, the chunk address, seed/version identity, and save envelope should
  carry a dimension id from the first slice, because retrofitting one forks
  generation, persistence, and spawn logic. Decide in §6 whether V1 is
  Overworld-only (with the id reserved) or genuinely single-dimension.
- **Initial spawn search**: a deterministic spawn-point search (safe land,
  non-water, within the generation contract) rather than a hard-coded origin,
  plus the void/bedrock policy from §4.11.

The recipe's *carving* idiom (rooms, routes, ellipsoids) should be retained as
the cave/structure pass — it is the part of the procgen lane with real value.

### 4.2 Streaming budget (`Modules/World` residency policy)

Generalise `TerrainResidencyPolicy` from a fixed 3×3/5×5 window over one
vertical band to a budgeted 3D policy: requested/retained radii, vertical band
around the player, operations per tick, an explicit eviction order and priority
(nearest-first, dirty-before-clean, never evict a lease), and a resident-chunk
cap sized from a measured per-chunk cost. The current 64-chunk cap corresponds
to ~262k voxels; a 384 m-tall, 9×9-chunk window is ~1,900 chunks, so **the cap,
the per-tick remesh budget, and memory must be measured before the world scale
is promised**.

The measured numbers are obtainable without upstream changes:
`VoxelSceneReadout` reports `ResidentChunkCount`, `SolidVoxelCount`,
`DirtyChunkCount`, and `Rebuilt`/`Reused`/`RemovedMeshChunks`, and
`VoxelResidencyReceipt` reports the same per transaction. Memory per chunk is a
property of the product's own payload representation, so it is product
measurement.

**Threading is not a product decision.** The default design is step-budgeted
generation on the product update thread: generate at most N chunk payloads per
tick within the residency budget. **This was written while G5 was missing and is
now superseded**: the affinity contract landed at pair `c30c1ef18861`, so the
product may overlap projection building through Engine-owned
`Voxel.StartResidencyPreparation` / `PollResidencyPreparation` /
`CommitResidencyPreparation`, and may run pure generation on product-owned
workers using copied, product-owned values only. Engine services remain
callback-confined; a product-owned worker that calls them stays forbidden.

### 4.3 Block content model (`Modules/Content`)

- **Block registry**: stable `BlockId` → properties (material slot, hardness,
  tool class, drop table, solidity, replaceability, orientation requirement,
  light emission/attenuation, atlas binding, blast resistance, flammability,
  friction, footstep material, map colour). Some of this already has an
  upstream home: `AuthoredMaterialInput` carries `Solid`, `Collidable`,
  `Occludes`, and `StructuralClass`, so the registry should project onto those
  fields rather than invent a parallel material model.
- **Item registry** over `Mechanics.ItemDefinition` (fungible vs unique,
  capacity cost, equipment policy).
- **Atlas/material binding**: the existing 128² atlas carries four authored
  regions and materials backing three voxel material slots (grass uses a
  grass-side base plus a +Y grass-top override); a survival catalogue needs a
  real block atlas with per-face regions and alpha modes, admitted through
  `AuthoredMaterialInput`/`AuthoredVoxelSurfaceInput`.
- **Per-block state**: the SDK exposes exactly one 32-bit material slot per
  voxel (`VoxelReadout.MaterialSlot`, `VoxelResidencyOperation` material
  arrays) and no per-voxel metadata. Block entities (chests, furnaces, doors,
  crops) must therefore be **entities plus a voxel-address index**, with a rule
  for what happens when the block is broken or the chunk unloads. See G2: if
  blocky orientation (stairs, logs) matters visually, that is an upstream
  request, not a product workaround.
- **Gravity blocks** (sand, gravel): a per-block rule that schedules a fall, a
  voxel move, and any suffocation check. Instant versus animated fall, and what
  a stack does when it lands, are product rules (`Modules/Interaction`).
- **Transparency and shape classes.** Blocks need explicit classes for meshing
  and aim: opaque, cutout (leaves-like), translucent (glass, water-like), with
  the sort order between classes part of the definition (cutout draws in the
  opaque pass; translucent is sorted and non-occluding). Each non-cube shape
  carries three facts, not one: a collision box, a selection/aim box, and a
  climbable flag (ladders). Alpha mode and cutoff are already available through
  `AuthoredVoxelSurfaceInput`; the shape and sort classes are product policy that
  must be decided once, with §4.4, rather than discovered separately in meshing,
  collision, and targeting.

### 4.4 Interaction and the item loop (`Modules/Interaction`, `Modules/Inventory`)

- Break/place: extend `TerrainWorld.TryEditFromView` with hardness and tool
  gating, drop resolution, and an entity-or-inventory outcome; keep the existing
  revision-checked `VoxelEditTransaction` and stale-revision handling. Placement
  today always uses material 1 (grass) with no selection UI, so the hotbar is the
  first thing that makes placement meaningful rather than a fixed material.
- Mining speed: hardness alone is not a feel. Time-to-break needs a documented
  formula hook (hardness × tool-class multiplier × status modifiers such as
  underwater or airborne), even if V1 hard-codes most multipliers at 1.
- Reach/aim: `Rusty.Engine.Interaction` (`WorldInteraction`, `InteractionFocus`,
  `AimAssist`) is the intended path for targets, doors, and containers; the
  package README names it explicitly, and the shape/selection classes from §4.3
  determine what a target actually is.
- Inventory: `InventoryStore` transactions, hotbar selection, container
  transfer, equipment slots.
- Crafting: a product recipe catalogue (shaped/shapeless, station-gated) that
  compiles to `Consume`/`Grant` transactions, with station blocks as entities.
  Smelting is its own recipe type: duration, a fuel table with burn values and
  by-products, and progress persisted in the station's block entity.
- Armour: equipment slots exist upstream (`EquipmentComponent`,
  `EquipmentState`, slot definitions), but armour values, the damage-reduction
  rule, and durability loss on hit are product design and belong next to the
  survival damage table. As with mining speed, the rule needs a documented shape,
  not just a number: armour points feed a reduction curve, toughness and
  damage-type exceptions modify it, and each hit costs durability.
- Powered interaction floor: levers, buttons, pressure plates, doors, and
  trapdoors need a binary on/off intent that travels through `Interaction` and
  the existing spatial trigger registry. Redstone dust, repeaters, and
  comparators remain out of scope.
- Durability: item damage as unique-item state or a track, with the breaking
  rule product-owned.

### 4.5 Survival rules (`Modules/Survival`)

Health, hunger, stamina, and XP as `Mechanics` tracks; damage sources as a
product decision table (fall, drowning, fire, mob, starvation, suffocation,
void); regeneration and death policy; respawn point selection using
`VoxelAnnotationKind.SpawnArea` or a product spawn table. Status effects via
`EffectDefinition`/`ActiveEffect`. Three details are easy to defer and
expensive to retrofit:

- **Nutrition, not just a hunger bar**: saturation, per-food nutrition values,
  eating time and interruption, and the starvation/regeneration thresholds.
  Slice 3's farming and cooking work cannot be built from a bare track.
- **XP has a purpose**: orbs or direct awards, levels, and whether it survives
  death. If XP exists only to be collected, say so; otherwise it belongs with
  the enchanting decision in §6.
- **Difficulty** as a real setting (peaceful through hard) that scales damage,
  hunger, and spawn hostility, rather than a constant.

### 4.6 Creatures (`Modules/Creatures`)

A mob definition catalogue plus spawn/despawn rules (light level, time, biome,
distance, caps), per-mob components in the product's `EntityStore` (health,
hunger, target, home, growth), behaviour via `StateMachine` (the product owns
conditions, timers, blackboards and all state payload — the upstream FSM is
bare), movement by consuming `RequestNavigationPath`/`EvaluateNavigationStep`
waypoints through `EntityCharacterController.Step` (the product owns the path
follower and any avoidance), senses via `Perception` (the product owns
awareness, memory and aggro), combat via spatial casts plus Mechanics
stats/effects, drops via the item loop, and presentation/animation via
`EntityGraphicsProjection` and the Animation service. Animated `.vox` objects
(`IVoxelContentService.CreateObjectPlayer`) are the most MC-like art path for
blocky creatures; note that the Animation service has no skeletal skinning or
root motion, so mob motion is either baked in the clip or authored as a voxel
object.

Spawning needs more than the adjective list: per-category caps (hostile,
passive, ambient), despawn radii and timers, persistence exceptions (named or
tamed creatures do not despawn), a daylight-burn table for the undead, and the
light threshold that gates hostile spawns — which is the product half of G1 and
should be written down even while the Engine light channel is missing. Breeding
and taming are explicitly deferred in V1 (renewable food and materials come from
farming and sapling growth instead), so that decision should be stated rather
than assumed.

### 4.7 Time, light, and weather (`Modules/World` clock)

A product clock advanced by fixed steps (persisted with the save), mapping to
`LightDescriptor`s and to sky appearance. The product also owns the light
policy that gameplay depends on: the split between sun and block light, the
hostile-spawn threshold, and what a torch is worth. See G3 for the sky limit and
G1 for the missing per-voxel channel.

Weather is a decision, not a hedge: either it is presentation only in V1 (state
that plainly and let rain be a particle and audio effect), or it changes
gameplay (extinguishing fire, hydrating crops, affecting sleep or fishing) and
therefore needs a rule table and a persisted state in the save envelope.

### 4.8 Persistence and save envelope (`Modules/Persistence`)

The current design is one bounded overlay blob (`terrain/overlay`, schema v1,
65,536 entries). A survival world needs a versioned envelope:

- world identity: seed, generator version, world size/envelope, creation time,
  and (per §6) whether a dimension id belongs here from the first version;
- terrain: either the product overlay or `IVoxelService.ExportHistory` /
  `RestoreHistory` with `VoxelHistoryPersistenceStore.Save`/`LoadAndRestore`
  (an Engine-provided edit history with a codec — worth evaluating against the
  custom codec before extending the latter);
- block entities and creatures: entity snapshots with revisions. The SDK has no
  world or entity snapshot format and `EntityStore` has no serialization, so
  this codec is product-authored; `InventoryState.CaptureStacks` and
  `StatsComponentCapture` exist and should be consumed rather than reinvented.
  `IContentStoreService` artifact roles (`EntityStateSnapshot`, `VoxelAsset`,
  `VoxelObject`, `VoxelAnnotation`, `SceneDocument`, `PrefabRegistry`) are the
  natural home for larger side artifacts, keeping the blob small;
- player: pose, inventory, equipment, stats, effects. Pose should not be
  serialized by hand — `ISpatialService.CaptureCharacterContinuation` /
  `RestoreCharacterContinuation` already produce a checkpoint carrying config,
  motion, source session identity, generation, spatial-session fingerprint,
  content authority hash, and config fingerprint, which is exactly the
  "resume the same character in the same world" fact a save needs. Because that
  checkpoint is fingerprint-bound, the save policy must state what happens when
  the session configuration changes (surface mode, chunk size, voxel size):
  reject and respawn, migrate, or refuse to load. `§4.12` explains why the
  surface mode is one of those configurable facts;
- world state: time of day, weather, spawn state, RNG cursors;
- a schema version and an explicit policy for incompatible saves, since the
  project has precedent for "schema v2 requires regeneration". Survival worlds
  should inherit an explicit answer here, not an implied one: either a world
  survives a generator-version bump through migration, or the project states
  that worlds are disposable on a version bump the way v1 procgen samples were.

**What v1 of the envelope contains** (the rest grows with its slice, so the
Slice 1c claim "save/load equivalence" is provable): world identity, terrain
edits, and player continuation. Block entities and creatures enter the envelope
in the slices that create them (2 and 4), and each addition must keep reload
equivalence for the sections that already exist.

`ProductStateStore<TState>` with `JsonProductStateCodec<TState>` (or a bespoke
`IProductStateCodec<T>`) is the intended per-section mechanism; the envelope
above is the product's composition of those sections.

Save must be atomic from the player's perspective (revision-guarded writes at a
known simulation step), and reload must be *equivalent*: this is a test
property, not an aspiration.

### 4.9 UI and feedback (`src/ui` + `Modules/Ui`)

HUD (crosshair, hotbar, health/hunger), inventory/crafting/container screens,
death/respawn, pause/settings, and diagnostics behind a toggle. The existing
rule holds: DOM is UI only — no gameplay state, no transport, no loop — and
in-world elements (nameplates, damage cues, particles) belong to Engine
`Presentation`, not to the DOM. Third-person or shoulder views are cheap on the
camera side (`CameraDescriptor`, `CameraTarget`) but the camera service has no
spring arm or collision response, so any camera that must not clip terrain is
product-composed.

Two things this section must not lose: **damage and death feedback** (hurt
flash, knockback, death screen, respawn flow, and the distinction between
"died" and "crashed"), and **controls** (sensitivity, FOV, key remapping, and
inventory drag/rearrange — the Engine exposes `ProductInputMapping` and
`IInputService.ReplacePhysicalMappings` but no rebinding UI and no inventory
screen, so both are product work).

The plumbing is half-built in an unusual way: the product already publishes a
structured `UiProjection` on `craftsurvive.terrain` every update, and no DOM file
consumes it — the companion panel reads debug command receipts instead. A HUD
should either consume that projection or the projection should be retired;
publishing structured facts into a stream nobody reads is exactly the kind of
surface that makes the next reader overestimate what exists.

### 4.10 Performance and verification (`tests/`)

New gates: chunk generation throughput (chunks/s), tick time under a resident
cap, memory per resident chunk, save/load round-trip equivalence, golden chunk
hashes for generation determinism, spawn-rule invariants, inventory/crafting
transaction invariants. Evidence capture can lean on
`IRenderOutputService.CaptureImage`/`ExportSceneGlb`, `IDiagnosticsService`,
`EntityStoreDiagnostics`, and the spatial map snapshots, which keeps a
survival-lane proof in the same style as the current receipt-based evidence.
There is no profiler in the SDK, so throughput and tick-budget numbers must come
from product-owned counters plus those readouts. Budgets are incomplete without
**view distance**: render distance tiers, a simulation distance that may be
smaller than the render distance, and a frame-time target, all three separate
from the chunk-throughput and tick numbers above.

The existing lanes are not sufficient for the evidence this direction asks for,
and the gap should be stated rather than papered over:

- `tests/Procgen`, `tests/Workbench`, `tests/TerrainResidency` (which already
  hashes SHA-256 chunk material snapshots against fixed digests — the pattern
  "golden chunk hashes" extends), and `pnpm run check:ui` are the regression
  base. Note that `check:ui` is a `tsc --noEmit` typecheck: it proves the DOM
  companion compiles, nothing about behaviour.
- CI runs only managed lanes; `verify.yml` states that its tests "consume
  managed SDK values only; they do not launch the Linux runtime". The Release
  build, `pnpm run audit:textures`, NativeAOT verification, and every live check
  are manual today.
- The live-product harness pattern this direction needs already exists in
  unregistered form: `tests/rope-playground-smoke.js` drives a real browser
  session against `.den-playwright.json` and asserts readouts. A walk-out test,
  a save/reload equivalence test, and a HUD playtest should use that lane and be
  registered as an explicit check, not left as an ad-hoc script.

### 4.11 Rules a survival clone implies, scoped here rather than silently omitted

Each of these is a small design decision with a named owner **and a slice that
must land it**, so none is discovered late and none stays permanently deferred.
The slice tag is the exit obligation: the slice is not done until its item here
is decided in the product, not just in this document.

- **World creation and selection** — *Slices 1c and 5*: seed entry, world
  naming, **multiple world slots**, per-world options (difficulty,
  cheats/hardcore), and what "new world" means for a save envelope
  (`Modules/World`, `Modules/Ui`, §4.8).
- **World bounds, void, and spawn protection** — *Slice 1c*: the product
  envelope is ±1,000,000 m (`Modules/Terrain/VoxelAddress.cs`,
  `IsWithinWorldBounds` — product code, not SDK). Pick a real playable bound and
  state what happens at it — soft border, hard wall, or error — and note that
  the finite option also fixes save size and makes a border policy mandatory.
  That choice also needs a void and bedrock policy (kill plane, indestructible
  floor, void damage) and a spawn-protection rule if worlds are finite.
- **Tuning magnitudes are named, not invented** — *every slice that needs one*:
  ore bands (1a), break-time multipliers and fuel values (2), food, saturation,
  day length and hunger rate (3), spawn caps and light thresholds (4). This
  document deliberately does not guess numbers; each slice lands them as named
  product constants or tables with a `TODO(tuning)` marker rather than bare
  literals, per `AGENTS.md`.
- **Debug-command migration** — *Slice 5*: 61 `craft.*` commands are the current
  interface, and several (`craft.player.teleport`, `craft.courtyard.*`,
  `craft.procgen.*`) are testbed-only. Slice 5's "no debug-command dependence"
  needs a disposition per command group — promote to HUD/screen, keep as
  developer tooling, or retire with the scene it belongs to — instead of leaving
  61 commands as an accidental second interface.
- **Fluid scope, stated once** — *Slices 1c and 3, with G7/G8*: still versus
  flowing, swimmable or not, whether oceans and rivers are in the generation
  contract, whether buckets exist, and whether lava is lethal. Recommended V1
  default unless §6 decides otherwise: static generated water with no flow, no
  buckets, lava present and damaging on contact — with swimming now an Engine
  movement mode rather than a buoyancy approximation. **§10 adopts that default
  for the chosen direction**, so static water, swimming, and drowning are in
  scope while flowing water stays an open decision; the only remaining question is
  whether the static blend-material water voxel actually renders and passes the
  solver as the flags suggest (S0's staged proof, not an upstream request until
  it fails).
- **Explosions and fire, stated once** — *Slice 2*: which blocks resist blast,
  whether fire spreads between blocks, and whether TNT and creeper-style blasts
  are in V1 or explicitly out. Either answer is fine; silence is not, because
  both change the block registry and the edit path.
- **Backups and corruption recovery** — *Slice 1c*: at least one previous save
  generation, a retain-last-N policy, and behaviour on a truncated or unreadable
  blob (`Modules/Persistence`). The current blob uses scope `craftsurvive` and
  key `terrain/overlay`, so the envelope is new keys in the same store, not a new
  store.
- **Item entities and pickup** — *Slice 2*: drops exist in the world before
  pickup, with a despawn timer, a merge rule, and a decided behaviour for drops
  in unloaded chunks (`Modules/Inventory`).
- **Sleep and time skip** — *Slice 3*: bed placement, night skip, spawn point
  update, and whether sleeping requires a safe site (`Modules/Survival`, §4.7).
- **Death policy** — *Slice 3*: keep-inventory, death drops, respawn anchoring,
  and whether difficulty changes any of it (`Modules/Survival`).
- **Non-cube shape families** — *Slice 1a decision; Slice 6 content*: slab,
  stair, fence, and torch are either product microvoxel/`Implicit` geometry or an
  upstream need (see G2) — decided with the shape classes in §4.3, not
  discovered in art.
- **Deliberately out of scope for a first clone**: enchanting and brewing,
  breeding and taming, weather that changes gameplay, villager economies, and
  moddability. Each slice that would otherwise absorb one must name it as a
  non-goal, and the renewal story for wood and food rests on sapling growth and
  farming (§4.1).

### 4.12 Surface mode: cubes first, dual contouring as a later projection

The question "should the world be cube-rendered or dual-contoured" is narrower
than it looks in this Engine, and that changes the recommendation.

**The mode is a mesher, not a world model.** `SpatialSessionConfig` is
`(CollisionVoxelSize, CollisionChunkSize, VoxelSurfaceMode)` — the session *is* a
collision voxel grid, and `VoxelSurfaceMode` selects how a presentation mesh is
extracted from it (`GreedyCubes`, `MarchingCubes`, `DualContouring`). Collision,
edits, residency, and persistence stay on the canonical grid. The product has
already demonstrated the swap at the data level: Den #6844 kept all three meshes
as disposable projections of the same canonical material voxels, and #6777's
acceptance states that behaviour is identical across box/MC/DC precisely because
collision follows canonical voxels rather than the presentation mesh.

Navigation needs two explicit disciplines to stay mode-independent. First, keep it
voxel-derived: admit it through `ReplaceVoxelNavigation` (or read
`NavigationProjectionKind.VoxelDerived`) rather than `ReplaceCollisionNavigation`
or `NavigationProjectionKind.CollisionDerived`, which follow session collision
including copied meshes and would reintroduce exactly the coupling this section is
trying to avoid. Second, note that this is a rule to establish, not current
behaviour: the product issues no navigation calls at all today, so the invariant
has to be introduced deliberately with the first pathfinding slice.

**So Slice 1 should be cubes, for reasons that are mostly not about meshing:**

- **Visual and rule authority coincide.** Selection, support, face exposure,
  "may I place here", drops, and block entities all key off a voxel address and a
  face. With a smooth surface the rendered geometry is pulled off the lattice, so
  every one of those interactions needs a separate visual language and the player's
  mental model diverges from the grid. That is a permanent tax on UI and feel, paid
  during the years when the rules are still being invented.
- **Edits are the core verb and cubes are cheap to re-mesh.** Greedy cubes plus
  dirty-chunk tracking give small incremental rebuilds with receipts
  (`Rebuilt`/`Reused`/`RemovedMeshChunks`). The project's DC cost evidence comes
  from its authored-geometry studies — `docs/procedural-dungeon.md` reports
  0.461 s / 103,152 triangles at 0.28 m cells and 1.604 s / 300,450 triangles at
  0.16 m, within a 64 MiB admission and 256 MiB retained baseline, with
  documented undersampled-feature loss. Those figures are Implicit→DC extraction
  of cave and dungeon fields, **not** a remesh of a voxel session in
  `VoxelSurfaceMode.DualContouring`, for which no measurement exists anywhere in
  this repository; producing that measurement is the spike's job (§7). What is
  already fair to say is that the greedy-cube path has the cheap incremental
  story today and DC does not.
- **The content pipeline is face-based.** Per-face atlas regions, the +Y
  grass-top override, and alpha cutout/translucent classes are defined per cube
  face (`VoxelSceneFaceMaterialBinding` is literally `(slot, face, material)`).
  On a DC surface those become vertex-blended, and per-face overrides lose
  their meaning (documented: interpolated materials can miss thin or nonlinear
  regions, and separate closed layers need real reveal clearance).
- **Mechanical content is a grid concept.** Doors, stairs, crops, chests,
  trapdoors, and any on/off logic block are legible because the grid is uniform.
  DC smooths exactly the geometry those blocks depend on.
- **Voxel collision gives exact risers.** A voxel session's treads are the
  lattice itself, so step-up behaviour is defined by the grid rather than by
  extracted geometry. This is a mild advantage, not a defect in the mesh path:
  Engine #7831 ("implemented supported hard-riser stepping", landed 2026-09-08,
  shipped in SDK `0.1.0-dev.11eb8178488c`) fixed the hard-riser case, and the
  installed pair `b9c281937b26` postdates it. Note that
  `docs/known-limitations.md` and `docs/courtyard-layout.md` still describe that
  limitation and its 45° stair-nose mitigation as current; those two lines are
  stale, like the pair references in §1.

**Where the regret would actually come from.** Not from meshing — from the
*content model*. If "one item is one cube" hard-codes itself into the registry,
the inventory, and the recipes, then a later DC world cannot express less
constrained design without redoing the build verbs. Keep the escape hatch at the
content layer:

- registry entries carry a shape/state class from the first slice (§4.3), so
  non-cube and stateful pieces are content additions rather than engine changes;
- authored, non-cube architecture is Engine content — `Implicit` recipes,
  `AuthoredPrefabRegistry`, `IVoxelContentService` — presented as meshes with
  copied collision, exactly the pattern the Courtyard lane already uses. Such
  structures are *not* editable by the voxel edit path unless they are rasterized
  into material chunks, which is not an existing Engine path and would be product
  work; record that tradeoff rather than discovering it;
- gameplay invariants are stated as "the canonical grid is authority, the mesh is
  a projection", and tested: one accepted edit must move presentation and
  collision together at one revision, and re-creating the session in another
  surface mode over identical voxel data must not change collision or navigation
  results (the mode is fixed in `SpatialSessionConfig`, so "switching" always
  means a new session, not a live change).

**The hybrid worth keeping in mind.** Mesh instances and call-local obstacles ride
along in `CharacterStepRequest` (`CharacterMeshInstance`, `CharacterObstacle`),
and generated meshes supply copied Spatial collision, so "DC-authored set pieces
inside a cube world" is expressible within one session. Two *voxel sessions*
(one cubes, one DC) composing for collision is not demonstrated — a character
step takes one session — so do not design around that without an upstream answer.

**Recommendation.** Slice 1 on `GreedyCubes`, with the projection invariant as an
acceptance property; evaluate DC afterwards as a bounded spike for *natural
terrain presentation only*, with a **mandatory** mesh-budget criterion (not
advisory — it is the only voxel-session DC measurement the project would have) and
an answer for how selection and block outlines render on a smooth surface. If it
fails either, DC stays where it already earns its keep: authored architecture and
caves.

**How far "low-regret" actually goes.** The generator contract and terrain save
are mode-independent because both store materials, not meshes, so the world model
does not care which extractor runs. Two things do: the selection/feel work above,
and any *stored continuation*. `CharacterContinuationCheckpoint` carries a
`SpatialSessionFingerprint` and `ConfigFingerprint`, so a checkpoint taken under
one session configuration is not automatically valid under another — a save made
in cube mode cannot assume it resumes under a DC session. That belongs in the
§4.8 save policy as an explicit compatibility rule, not as an assumption.

## 5. What must be requested upstream

**Status after adopting pair `c30c1ef18861` (2026-09-26): every request below
except G7 has landed.** The entries are retained as the record of what was asked
and why; they are no longer a list of gaps. What landed, and where it differs
from the original request:

- **G1** → `Voxel.SampleDirectLighting(VoxelLightSampleRequest)`: a CPU
  direct-light proxy over the product's own retained `LightDescriptor`s, with
  optional collision occlusion, plus `RustyEngineProductDefaultWorldLights=disabled`
  for a dark unlit world. It is *not* a sky/block-light lattice. Sealed rooms go
  dark because the default rig is off and nothing lights them, not because light
  propagates; ambient is unoccluded, so ambient alone cannot darken a cave.
- **G2** → fifteen per-cell state bits (two quarter turns about +Y plus a
  0–8191 variant/stage) via `VoxelCellState.Encode`, readable and writable
  through `VoxelEdit.State`, `VoxelReadout.State`, and a parallel
  `VoxelResidencyTransaction.States` array, with variant face materials and
  history schema 4. Collision stays cube occupancy, and nonzero state requires
  `GreedyCubes` — this campaign's branch.
- **G3** → `CameraView.SetSkyBackgroundBlend(new(day, night, amount))`: two
  authored panoramas crossfaded by a product-owned clock value, surviving fresh
  host attachment, at two samples per visible sky pixel.
- **G5** → a callback-confined affinity contract (no Engine service call from
  `Task.Run`, timers, finalizers, or async continuations — read-only queries and
  `Dispose` included), plus Engine-owned `Voxel.StartResidencyPreparation`,
  `PollResidencyPreparation`, `CommitResidencyPreparation`, and
  `CancelResidencyPreparation`, where commit rechecks source, collider, rebase,
  and lease generations and rejects stale candidates rather than overwriting
  newer state.
- **G6** → measured residency, remesh, and geometry-diversity budgets.
- **G8** → first-class swim, climb, and fly modes selected through
  `CharacterControllerCommand.Movement`, with `CharacterStepReceipt.Movement`
  reporting accepted mode, immersion, and `HeadSubmerged`.
- **G7 (fluids) remains unrequested and unimplemented**, which matches the §10
  decision: static water needs no simulation, and flowing water is an open
  question rather than an assumed feature.

Two consequences for the slices above. §4.2's and §7's "threading is blocked
pending G5" is superseded: background residency preparation exists, so S2 may
overlap projection building while product generation stays restricted to copied,
product-owned data off the callback lane. And §4.11's water default changes
shape: swimming and submersion are Engine behaviour now, so the product supplies
the water volume and owns breath and drowning consequences, rather than
composing buoyancy itself.

The detailed entries below are the original requests, kept as the record.

- **G1 — Per-voxel lighting.** No type in the SDK carries a per-voxel light
  level: voxels are `(address, materialSlot)`, `VoxelSceneReadout` reports mesh
  and navigation revisions but no light, and scene lighting is limited to
  `LightDescriptor`s created through `IGraphicsService` plus emissive materials.
  A Minecraft-style cave darkening and torch gradient is therefore not
  expressible. Requested capability: a per-voxel light channel (sky and block
  light, or an equivalent mesher-produced irradiance/AO attribute on the cube
  surface) with read/write and persistence. Consumer: caves, torches, mob spawn
  light rules.
- **G2 — Per-voxel block state.** Orientation, variant, and growth stage have no
  representation; the material slot is the entire per-voxel payload. Requested
  capability: either a small generic per-voxel state payload with mesh/atlas
  selection and persistence, or a documented Engine position that block state is
  an entity concern (including budget guidance). Consumer: stairs, logs, doors,
  crops.
- **G3 — Time-of-day sky and atmosphere.** The sky is a single authored
  panorama (`SetSkyBackground`, `ClearSkyBackground`, `SetBackgroundColor`);
  there is no sun/moon direction, sky tint, fog, or star surface. A directional
  light can move; the sky cannot follow it. Requested capability: time-of-day sky
  parameters bound to the product clock. Consumer: day/night cycle.
- **G4 — Multiplayer/networking.** There is no transport, replication, or
  session surface anywhere in the SDK. The LAN host can serve browsers, but they
  attach to one host-owned product session (`IEngineProduct.Attach` republishes
  the retained world); there is no per-client player, world replication, or
  authority handoff. A multiplayer clone is an entire Engine service family.
  Consumer: only if multiplayer is in scope (§6).
- **G5 — Threading/affinity contract for streaming.** `SimulationScheduler`
  callbacks are step-bound (`ScheduledWorkContext` carries only `ProductUpdateFacts`
  and `SimulationStep`), and nothing in the SDK states which calls may be made
  off the product thread. Requested capability: a published affinity contract
  and/or an Engine-owned generation hook that lets generation overlap the frame.
  Consumer: chunk streaming at scale. **Until that contract exists, this
  direction does not authorize a product-owned generation worker** (§4.2); the
  default is step-budgeted generation inside the update callback, and Slice 1b
  is blocked rather than substituted.
- **G6 — Measured residency limits (mostly product measurement).** The SDK does
  expose the counters needed to measure: `VoxelSceneReadout` reports resident
  chunk and solid-voxel counts, dirty chunks, and rebuilt/reused/removed mesh
  chunks, and `VoxelResidencyReceipt` reports the same per transaction. What is
  missing is a *stated* Engine budget — a documented practical ceiling on
  resident chunks, per-tick remesh work, and mesh memory — so the product can
  size a world scale instead of discovering the wall in playtest. Memory per
  chunk of the product's own payload representation is product measurement, not
  an Engine gap, and should not be filed as one.
- **G7 — Fluids (optional).** No fluid or cellular-automata surface exists.
  Water/lava that spreads must be product simulation expressed as voxel edit
  transactions; the honest first target is static fluids with an explicit
  follow-up request if flowing fluids are required. **§10 settles this for the
  chosen direction**: static water only, flowing water unfiled and left as an
  open decision. The remaining uncertainty is not whether flow is needed but
  whether a static blend-material water voxel renders and passes the character
  solver as expected — an S0 staged proof, not an upstream request until it
  fails.
- **G8 — Movement modes: swim, climb, fly.** Originally requested because the
  character service provided walking, jumping, crouching, step-up, slopes,
  platforms, and tethering but no swimming, climbing, or flight. **Landed at pair
  `c30c1ef18861` as #8609**: `CharacterControllerCommand.Movement` selects
  walking, swimming, climbing, or flying; the product supplies the water volume
  (an environmental AABB with buoyancy, drag, and gravity scale) or a climb rail,
  and reads immersion and `HeadSubmerged` back from
  `CharacterStepReceipt.Movement`. Water is environmental input, not a fluid
  simulation, and the product keeps breath, stamina, and drowning consequences.
  Ladders and rails are therefore usable in the vertical dungeons, and no
  non-climbing fallback is needed as a schedule dependency.

## 6. Decisions this direction depends on

These change the plan materially and should be settled before Slice 1 work
starts. **Several are now settled for the chosen direction** — scope
(single-player), world scale (finite ~100 km²), dimensions (authored dungeon
loads), surface mode (cubic), and enchanting/brewing (out) — and are recorded in
§10 with the Den campaign that carries them. The content floor is re-weighted for
that branch but still unconfirmed. The rows below stay as the reasoning,
including for the decisions still open.

| Decision | Options | Consequence |
| --- | --- | --- |
| Scope | Single-player only vs multiplayer later | Multiplayer adds G4, a whole Engine family; single-player keeps this a product-side programme |
| World scale | Infinite-feeling (streamed, seed-only) vs finite bounded world (e.g. 8×8 km) | Finite worlds allow a simple save and no streaming budget; infinite worlds need §4.2 and G5/G6, plus a border/void policy (§4.11). The choice cannot be committed before §4.2's measurements exist — and #6853's documented envelope belongs to the retired lane, so whether it transfers to the C# lane is itself an open question |
| Dimensions | Overworld only, with a reserved dimension id vs genuinely single-dimension | Retrofitting a second dimension later forks generation, persistence, and spawn logic; a reserved id costs almost nothing in Slice 1 |
| Content floor | **Settled 2026-09-26**: 12–16 blocks, 6–10 items, 2 creatures, one biome family plus cave and dungeon tilesets ([S0 record](s0-decisions.md)) | Prices Slices 2, 4 and 6; the atlas is the binding constraint, not lighting |
| Fidelity target | MC-like *feel* (loop, mood, scale) vs mechanics-complete vs content-complete | Determines how many blocks/items/mobs are in scope, and whether lighting (G1) is blocking |
| Lighting | Request G1 vs accept flat lighting + point lights + emissive materials | G1 is the single biggest visual-fidelity gap, and it gates §4.6's spawn threshold |
| Per-block state | Entity-based block entities only vs request G2 | Entity-only means no oriented stairs/logs without extra geometry |
| Surface mode | **Settled**: Greedy cubes for Slice 1 with a bounded DC evaluation later (§4.12) vs DC-first; per-cell state requires GreedyCubes, which removes DC from the initial scope | The mode is selectable per session **at creation** (no live switch) and collision/navigation stay on the grid; per-cell orientation, rotation and growth stage are GreedyCubes-only, so cubes-first now has a hard dependency rather than a preference |
| Water and movement | **Settled and proven**: static water with Engine swim mode (`CharacterControllerCommand.Movement`); no water material needed for behaviour ([live proofs](live-proofs.md)) | Engine-owned swim/submersion means the product supplies the water volume and owns breath; only the water *appearance* needs authored tiles |
| Enchanting and brewing | Explicit V1 non-goal vs in scope | Decides whether XP needs a spend sink, which changes §4.5 and the content floor |
| Save policy | **Settled 2026-09-26**: worlds are disposable; version the envelope and discard on mismatch, keeping one backup ([S0 record](s0-decisions.md)) | The live lane proved a catalog change currently fails the whole product rather than reporting a stale blob, so S2 must make the reset deliberate |
| Procgen testbed fate | Retire to an authoring lane vs keep as a playable mode | Affects the default boot path and doc surface |

## 7. Slice sequence (design level)

**Superseded in numbering by §10 and campaign #8595**, which re-cut these slices
for the adopted direction (S0–S10). The sequence below is retained as the design
rationale, with two facts corrected: G5 landed, so streamed residency may use
Engine-owned background preparation, and G8 landed, so swimming and climbing are
Engine movement modes rather than product approximations.

Each slice is a bounded, provable increment in the existing style: named product
owner, named Engine mechanisms, explicit evidence, explicit limits.

| Slice | Content | Exit evidence |
| --- | --- | --- |
| 0. Decisions | Settle §6; file G1–G8 as owning tasks with consumers; run a minimal staged proof that the mechanism layer is usable (one `InventoryStore` + `StatsComponent` + `StateMachine` instance, one entity with an `EntityGraphicsProjection`, one `RequestNavigationPath` against a real `SpatialSession`) | Filed requests; direction doc updated; a staged harness result that either confirms or corrects §3 before Slices 2–4 are priced |
| 1. World spine | Split deliberately, because this is the largest slice: **1a** block registry with its shape and transparency classes, the first 2–3 biomes, 3D generation contract with its ore table, **1b** streamed residency with cache, prefetch, cancellation and a measured budget, **1c** save envelope v1 (world identity + terrain + player continuation) with its generation-version, backup, and corruption-recovery policy, spawn search, the border/void policy, and the fluid scope in generation | Walk-out test through the registered live lane (§4.10), golden chunk hashes, save/load equivalence, throughput and tick-budget numbers, and the 1a tables landed as named product data; #6853's acceptance list is the starting contract. **1b's threading may use Engine-owned background residency preparation (G5 landed at `c30c1ef18861`), and product-owned workers are allowed for copied, product-owned payloads only** |
| 2. Item loop | Break/place with drops and tool gating, inventory/hotbar, crafting table, chests, durability, armour slots, item entities and pickup, and the explosion/fire verdict — with their break-time and fuel tables | Mine → craft → place → persist → reload, with transaction invariants tested and the tuning tables present |
| 3. Survival pressure | Health/hunger/damage/death/respawn, nutrition, XP, day/night clock, light policy, growth ticks, farming and cooking, sleep, and the water/swimming policy — with the food, day-length, and hunger tuning landed | Survive a scripted cycle, starve/die/respawn, renewable wood and food across reload |
| 4. Creatures | One passive and one hostile mob, spawn caps, despawn rules, daylight burning, and light thresholds, navigation, senses, FSM behaviour, combat, drops, animation | Spawn/despawn invariants, pathing witness, kill/drop loop, live-mob save/load, with the cap and threshold tables present |
| 5. Feedback | HUD and screens (including world creation/selection), audio, particles, third-person/animation, lighting polish, debug-command disposition | A browser playtest through the `.den-playwright.json` lane covering the core loop without `craft.*` commands, plus the registered live check; `pnpm run check:ui` only guards that the DOM still compiles |
| 6. Content scale | More biomes on top of the 1a set, larger block/item/mob catalogue including extra shape families, structures fed from the procgen lane | Bounded content review; generator version policy exercised |

Slices 1 and 2 are the ones that decide whether the rest is cheap or expensive.
Slice 0's staged proof is what makes that sentence more than a hope: if the
Mechanics/Entities integration turns out to carry hidden costs, Slices 2–4 are
integration projects, and this table should be re-priced rather than trusted.

One optional step is not a slice of its own: after Slice 2's item loop works,
run a bounded spike on dual-contoured *natural terrain presentation* under the
existing canonical grid. Its mesh budget is a **pass/fail criterion**, not a
note — this spike would produce the only voxel-session DC measurement the project
has (§4.12) — and it is judged on how selection and block outlines read on a
smooth surface. It answers the surface-mode decision with evidence instead of
taste, and it must not delay Slice 3.

## 8. Keep, retire, refuse

**Keep.** The product/Engine boundary and its vocabulary; Read → Decide → Apply
→ Publish; revision-checked edits and stale-revision handling; receipts and
evidence; the procgen artifact bank as a *content source* (ruins, dungeons,
structures) rather than the game mode; the rule that a missing capability is a
valid result.

**Retire from the default path.** The Courtyard/Stoneworks studies and the
`CRAFTSURVIVE_SCENE` switch should stop being the boot default once a survival
world exists; they remain Engine-capability studies behind explicit selection.
The arena traversal recipe (`TerrainRecipe`'s route, pillars, clearing, gaps)
is testbed furniture and should not survive into the world model.

**Refuse.** A second renderer, a custom transport, product-side P/Invoke, UI-held
gameplay state, per-voxel state smuggled through material slots, and any
downstream substitute for a missing Engine capability. Of the original G1–G8
requests, only flowing fluids remains unimplemented; if flowing water or
multiplayer matter, the request is upstream, not a workaround.

## 9. Variant: exploration-first, construction-second

**Status: not the chosen path.** §10 selected the cubic branch and deferred
mesh-terrain mixing to future work, so this section is retained as the design
record for that option — including its two staged gates — rather than as a plan.
It becomes live again only if the cubic branch proves too constraining or if
someone wants smooth terrain enough to pay for the gate.

This section records a second, narrower concept raised after the main direction
was written: an MC-ish prototype that **drops mining and large-scale world
manipulation** as the centre of the game. The world is explored rather than mined
away; natural terrain is smooth (DC-ish); the player still builds with blocky
voxels, but as a minor, deliberately *rapid* verb — home bases rather than
sculpture; and the fiddly high-detail end (iso volumes, microvoxels, authored
meshes) is deferred to later content rather than being the construction model.

**Why this is *potentially* less constrained than the main direction.** In this
Engine the two halves are separate subsystems that share one session handle, and
the structural shape for coexistence exists — but the coexistence itself is not
demonstrated anywhere in this repository, so read this as a design opening with a
gate, not a settled capability:

- `SpatialProjectionReadout` reports `ResidentChunkCount`/`ColliderChunkCount`
  *and* `StaticMeshRevision`/`StaticMeshAssetCount`/`StaticMeshInstanceCount`
  under one `AuthorityHash` and `CollisionRevision` — the readout is shaped as if
  a session composes voxel chunks and static-mesh collision rather than choosing
  between them;
- mesh collision has two entry points and they are not equivalent:
  `ApplyCollisionResidency` is additive with an explicit add/remove lifecycle
  (parallel in form to `VoxelResidencyTransaction`) but the product has **never
  called it**; `ReplaceCollision`, which the Courtyard scene does use, replaces
  the whole mesh set and reports only asset/instance counts. Whether
  `ReplaceCollision` disturbs voxel colliders is the specific unknown;
- the product passes the *same* `SpatialSession` to the Courtyard mesh scene that
  it creates for the voxel world (`courtyard?.Start(Session)`), but the two scene
  modes are used exclusively today: in courtyard mode `Synchronize` returns
  immediately, overlay restore and voxel presentation are skipped, and no receipt
  in the tree shows voxel and mesh counts nonzero at the same time.

No doc or signature states that the two are mutually exclusive, so the opening is
real; the evidence is suggestive, not proof. What follows is therefore a plan to
test, not a claim that "smooth world, blocky bases" already works: terrain can be
`Implicit`/DC meshes with copied collision while player construction is a
canonical voxel session rendered with `GreedyCubes`, in one session and one
character step — precisely the hybrid §4.12 describes as expressible, promoted
from set pieces to the world itself. Gate (1) below is what turns that into fact.

**The fork that decides everything: is the terrain editable?**

- **Voxel terrain presented with `VoxelSurfaceMode.DualContouring`.** The world
  stays the canonical grid, so it remains diggable and saveable exactly as §4.1
  describes; `CollisionVoxelSize` can be lowered (0.5 m, 0.25 m) for a finer,
  smoother read at a real cost in chunk count, memory, and remesh work. Mining is
  de-emphasised by *game design*, not by capability.
- **`Implicit`/SDF terrain with copied mesh collision.** True sculpted geometry
  and the cheapest path to the look this repo has already spent months on
  (stoneworks, volume-carved, sampled-volume, weathered, disrupted). It is **not
  editable and not voxel-saveable at all**: digging into a cliff is impossible
  without rasterizing SDF into material chunks, which is not an existing Engine
  path (§4.12). Choosing this is choosing "the world is a set, not a material".

Both are legitimate for this concept, but they cannot both be assumed. The first
keeps every later door open at the cost of a lattice-bound surface; the second
buys the best terrain *now* and makes "I want to dig here" an upstream request
later.

**What this variant changes in the requirement list.** Sections §4.1–§4.11 stay
as the branch where the world is voxel material. For the exploration-first
concept:

- **Shrinks sharply**: ore distribution and progression, mining-speed and tool
  gating tables, drop economy, and the "edit-heavy, cheap remesh" pressure on the
  surface mode — §4.12's cost objection largely evaporates because edits become
  rare and large. The edit-overlay budget (65,536 entries / 8 MiB) is *expected*
  to be comfortable for bases rather than proven so: confirm it once bases have a
  stated size.
- **Moves to the centre**: generation richness (biomes, verticality, landmarks,
  point-of-interest placement), streaming of *few large meshes* rather than many
  small edits — so the budget question becomes view distance, memory, and load
  latency instead of per-tick remesh — plus traversal, lighting and mood,
  encounters, and discovery state (what you have found, and how the game
  remembers it, is product state with no home in the current tree).
- **Stays, with a different owner**: survival pressure (food, light, warmth,
  danger) and the item loop become expedition supply rather than mining economy;
  fast construction wants multi-block placement, and `VoxelEditTransaction`
  already takes a list of edits, so blueprint-style stamping is a product policy
  rather than a new mechanism.
- **Navigation flips discipline**: for mixed mesh and voxel collision,
  `ReplaceCollisionNavigation` (collision-derived, with `AgentRadius`,
  `AgentHeight`, `MaximumSlopeDegrees`) is the fit, because mobs must path across
  a mesh cliff and a voxel base in one world — the opposite of §4.12's
  voxel-derived rule for a pure voxel world. Quality and cost of that projection
  over large meshes is unverified and belongs in the same staged proof.

**Two staged verifications gate this variant**, both small and both before any
game code:

1. **One session holding both collision sides.** Admit voxel chunks through
   `ApplyResidency`, then mesh collision through *each* entry point —
   `ApplyCollisionResidency` (never used by the product so far) and
   `ReplaceCollision` (the one the Courtyard uses) — and read the projection with
   both voxel and mesh counts nonzero. The specific unknown is whether
   `ReplaceCollision` disturbs voxel colliders. Then: an accepted `ApplyEdits`
   moves a voxel collider without disturbing mesh instances, a character step
   crosses the seam between a mesh cliff and a voxel base, the `AuthorityHash`
   stays coherent, and one frame presents *both* the voxel scene presentation and
   the mesh appearance facts together, since no product path has ever activated
   both channels at once.
2. **Collision-derived navigation over that mix**, with acceptable walkable cell
   counts, memory, and path quality (`ReplaceCollisionNavigation` with
   `AgentRadius`, `AgentHeight`, `MaximumSlopeDegrees`).

If (1) fails, the concept needs an upstream request rather than a workaround — do
not split it into two sessions.

**The honest trade.** This variant needs *less* Engine work than the full
survival clone and reuses the strongest lane this repository already has (the
DC/Implicit art studies, microvoxel props, authored set pieces). What it needs
*more* of is game design: with mining gone, the reasons to explore, the shape of
survival pressure, and what discovery means must come from somewhere else. That
is a design cost, not an engineering one — which is exactly why a blocky
construction prototype does not constrain it.

## 10. Chosen direction: adventurer RPG on a cubic world

§1–§8 assess the MC-clone question; §9 records the exploration-first variant;
this section records the direction actually chosen and how it re-weights them.
The durable work record is the Den campaign **#8595** with slices #8596–#8606
and upstream requests #8607–#8612.

**The decision.** Cubic world only (`GreedyCubes`); DC terrain and the mixed
mesh+voxel session gate stay future work under §4.12/§9. The game is an RPG
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
water, pair `c30c1ef18861` made swimming first-class: the product selects the
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
voxel-bound material. Those are S0's staged checks, not upstream requests.
that the GreedyCubes path renders a blend voxel surface with correct sorting,
that a non-solid non-occluding voxel still emits a visible surface while the
character passes through it, and that double-sidedness is reachable for a
voxel-bound material. S0 proves those three before S2 relies on them, and
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
consequences follow. First, the strongest objection to smooth terrain in §4.12 —
that edits are the core verb and DC remesh is expensive — is **mitigated by
design, not dissolved**: blasts are rare, large, and FX-covered, so the frequency
pressure goes away, but each blast still pays per-dirty-chunk remesh cost, and
§4.12 records that no voxel-session DC remesh measurement exists in this
repository. What keeps DC deferred is therefore the content/selection work and
the deferred session gate, not a claim about edit cost. Second, the threading
pressure in G5/§4.2 drops: step-budgeted generation with a disk cache and
tolerable pop-in is a defensible first answer, so #8611 is a contract request
rather than a blocker.

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

**What this re-weights from §4.** Shrinks or drops: ore distribution and
progression, mining-speed and tool gating, drop economy, *flowing* fluids, and
the large edit overlay (bases fit comfortably in the existing 65,536-entry
budget — to be confirmed once bases have a stated size). Static water and the
swim/climb request are back in the initial scope, so §4.11's static-water default
is now a settled decision rather than a fallback, and #8609 is active rather than
parked. Moves to the centre: point-of-interest
generation and discovery state (new product state with no home in the tree
today), encounters and combat, the dungeon authoring and load path — now with
verticality as its defining shape — and
presentation that makes an unknown world readable. Stays with a different owner:
survival pressure becomes expedition supply; creatures and progression become the
core rather than one slice among many; lighting becomes the most valuable
upstream gap because dungeons and caves are the product's set pieces (#8607).

## Appendix: primary evidence

Paths marked `Modules/…` are relative to `src/CraftSurvive.Game/`.

- Product constants and limits: `src/CraftSurvive.Game/Modules/Terrain/TerrainConstants.cs`,
  `Modules/Terrain/TerrainRecipe.cs` (column-based 2.5D generation),
  `Modules/Terrain/TerrainResidencyPolicy.cs` (3×3/5×5 window, 64-chunk cap),
  `Modules/Terrain/TerrainOverlayState.cs` (65,536-entry overlay).
- Edit path: `Modules/Terrain/TerrainWorld.cs` (`TryEditFromView`, residency
  synchronisation, overlay save/restore).
- Player policy: `Modules/Player/PlayerConstants.cs` (120 Hz step, rebase at
  1,024 m), `Modules/Player/PlayerController.cs`.
- Product composition and lifecycle: `src/CraftSurvive.Game/CraftSurviveProduct.cs`.
- Engine mechanism evidence: public SDK surface of `Rusty.Engine.dll`, pair
  `0.1.0-dev.b9c281937b26` (`Rusty.Engine.Mechanics`, `.Entities`, `.StateMachine`,
  `Navigation*`, `Perception*`, `Audio*`, `Animation*`, `Presentation*`,
  `Persistence*`, `Application.*`, `IVoxelService`, and lights through
  `IGraphicsService`/`LightDescriptor`).
- Package guidance for interaction: `PACKAGE_README.md` in the installed SDK.
- Current limits narrative: `docs/known-limitations.md`,
  `docs/csharp-migration-map.md`, `docs/procedural-levels.md`,
  `docs/voxel-foundation.md`.
- Den ledger for `rusty-craftsurvive` (77 tasks: 66 done, 8 cancelled, 3
  planned), notably #6853 (streaming/world acceptance), #6935 (block debris),
  #6849 (controller adoption), #6844 and #7731 (edit latency, residency reuse),
  #7659 (atlas and sky), #7491 (C# migration campaign that retired the prior
  lane), and planned #7911/#7912/#7916 (procgen only).
