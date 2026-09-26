# Survival direction: what a Minecraft-like game requires here

This is the durable design record for turning CraftSurvive from a procgen testbed
into an adventurer RPG on a cubic world. It states the target, what the product must
build, the settled decisions, and the boundary with the Engine.

Work state does not live here. Den campaign **#8595** (slices #8596-#8606) is the
work record of record, with upstream requests in the `rusty-engine` project; live
evidence is in [live-proofs.md](live-proofs.md). The assessment-era sections of this
document — the current-state survey, the capability finding, the upstream request
list, the first slice sequence, the unchosen exploration-first variant, and the
evidence appendix — are in Den as `history/survival-direction-campaign-record`.

- **Direction.** A cubic-world adventurer RPG: exploration and encounters primary,
  crafting and survival secondary, slow and irregular bomb-driven manipulation rather
  than per-block mining, a finite ~100 km² streamed world, static water with swimming
  and drowning, vertical authored dungeons behind a load transition, and the old
  experiments retired to an authoring lane. §10.
- **Target.** §2 states the design acceptance; §4 the module-level work.
- **Decisions.** §6 records the settled content floor, manipulation model, world
  extent, save policy, surface mode and water path, plus the constraints the live
  proofs established.
- **Boundary.** §8 records what stays, what retires and what is refused.

Section numbering is kept from the full record so citations in Den tasks stay valid;
numbers missing here live in the Den record above.


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
now superseded**: the affinity contract landed at pair `afbe891e1d34`, so the
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
  installed pair postdates it. The two lines this paragraph originally flagged
  as stale — the stair-nose mitigation notes in `docs/known-limitations.md` and
  `docs/courtyard-layout.md` — were corrected in campaign #8595 slice S1, and the
  same slice corrected the pair references in `README.md` and
  `docs/csharp-migration-map.md`; both now point at `eng/EnginePair.props`.

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


## 6. Decisions this direction depends on

Every decision this direction owed is settled. This section is the durable record of
what was decided and what bounds it.

| Decision | Settled answer | Consequence |
| --- | --- | --- |
| Content floor | 12-16 blocks, 6-10 items, 2 creatures, one biome family plus cave and dungeon tilesets | The atlas is the binding constraint, not lighting: every block type costs a region in one atlas image |
| Manipulation | Adventurer manipulation: place blocks and detonate charges; no general break-and-collect | Charges and blasts stay; any general break-time table goes; block-breaking survives only where a slice names a target |
| World extent | Finite ~100 km² (10 km x 10 km at one-metre voxels, 625 x 625 chunks per layer) with an authored hard border | Residency is on demand; navigation is published per box rather than for the whole extent |
| Save policy | Worlds are disposable: version the envelope, detect a mismatch explicitly, discard and regenerate, keep one previous backup | The live lane showed that changing the authored catalog currently fails the whole product instead of reporting a stale blob |
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
- **Background residency preparation is the supported overlap path**, and a commit
  rejecting a stale candidate is a normal outcome rather than an error.
- **A refused appearance snapshot stops the update loop.** Anything that must run
  after a snapshot publish belongs before it.


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
water, pair `afbe891e1d34` made swimming first-class: the product selects the
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


