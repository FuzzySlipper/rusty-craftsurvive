# Known limitations

What the product does not do today, and the limits a change has to respect. Each limit names
the constant that sets it or the task that owns it; the value lives there, not here. Study-era
records and superseded limits are in Den, project `rusty-craftsurvive`, under `history/` slugs.

## World and generation

- **Overworld ground is a reconstructed height field.** `TerrainDensity` supplies
  continuous samples to the Engine's DC path; `TerrainSurfaces` keeps construction,
  vegetation, bedrock, and water on the grid. This establishes smooth ground,
  without natural volumetric caves or overhangs.
  Natural ground uses the generated maps in `content/game/textures/terrain-studies`
  through Engine triplanar projection. Three authored landscape studies are reachable
  through Menu → Landscape study → Visit landscape; they are small material/topography
  experiments loaded as separate spaces.
- **The world map is simulated geography, not yet a travel layer.** `MapSimulation`
  generates relief, stream-power erosion, terrain-driven climate and biomes once per new
  world; local terrain samples it continuously and representative environments are reachable
  from **World**. The map is an inspection view that pauses local gameplay; site visits do
  not implement travel, weather, logistics or route costs, and there is no guaranteed route
  network between destinations.
  - Erosion is detachment-limited with creep, talus relaxation and filled basins. It does
    not transport or deposit sediment explicitly, so there are no alluvial fans or deltas.
    Filled basins drain rather than holding lakes; the only standing inland water is
    at sea level.
  - River courses follow 8-direction drainage on the 32 m lattice. Smoothing and meanders
    break up the lattice, but long reaches across filled plains can still read as straight.
    Voxel river water steps a metre at a time on steep reaches; it does not flow.
  - Climate is rank-normalised, so every world contains dry and wet country; latitude spans
    the whole map whatever its size. Local terrain still uses three blended relief families
    and three surface treatments (soil, sand, snow), so several biomes share a look;
    biome-specific ground, vegetation and art remain to be done.
  - New worlds take a few seconds to simulate (about 1 s at 10 km in an optimised build, several more at 16 km or in a debug build).
    Generation always runs off the update thread, including the first world of a fresh
    store, so startup never waits on it. Until that first world is admitted, the page shows
    only the map view's generating message; there is no progress estimate.
  - The bedrock perimeter rises a fixed height above local ground; it is not final
    geographic edge art. Far terrain has no overview LOD in the first-person view.
  - The map view's **Faceted relief** toggle is a prototype (#9436, #9437). It voxelizes the
    map as dual-contoured terrain: one voxel per 32 m for the whole map, and an 8 m patch with
    regional relief about 2 km across around the party, where the coarse ground is sunk out of
    sight. A camera locked to the party zooms (wheel, or Z/X) and orbits (right-drag, Q/E;
    R/F tilt); there is no free pan. The ground uses generated painterly map textures (meadow,
    forest floor, sand, snow, rock; provenance in `content/map-painted-textures.sources.json`)
    on four Engine terrain layers that blend across cell edges; rock and water are drawn
    unblended. `craft.world.mapstyle flat|ground|painted` switches styles for comparison (#9464);
    there is no player-facing style choice. A layer set takes at most 16 slots, so textured
    styles use one material per environment. Rivers are
    painted onto cells, not carved. Trees, cacti and rocks on the patch are generated low-poly
    static meshes (vertex-coloured, provenance in `content/map-models.sources.json`) scattered
    by environment, shown only at closer zoom; beyond the patch there is no clutter. Relief
    fades out toward the patch edge so it meets the coarse map flush, but the change in
    surface detail still shows at mid zoom. The view is rebuilt, not moved, when the party has
    moved (about 1,100 chunks and 1,800 instances, around 3 s). Wheel and right-drag delivery
    could not be exercised by the playtest service; keyboard orbit and zoom were verified live.
  - **Overland travel is a first slice (#9467).** On the faceted map the party token plans
    A* routes over the 32 m lattice, priced by slope, environment, fords and the sea, and
    travels them while the clock fast-forwards (`TravelCostModel.JourneyScale`). Night travel
    is three times slower. Destinations are the representative sites or a waypoint moved with
    W/A/S/D and planned with T; click-to-travel waits for rusty-engine #9466. "Explore here"
    drops into first person at the token. There are no rations, fatigue, camping, events, map
    knowledge or sled yet (slices 3–7). The detail patch does not follow a moving token (slice
    2). Progress made while the map stays open is lost if the session ends before exploring.
  - See [the sampling contract](csharp-migration-map.md#world-map-contract).
- **The generator is versioned, and the version is the save contract.**
  `TerrainGeneratorContract.CurrentVersion` identifies the world a seed produces. Changing any
  generation rule or tuning moves the generator's fingerprint; the managed goldens in
  `tests/TerrainResidency` and `TerrainGenerationGoldens.Live` then require a version bump, and a
  bump discards every save written for the old version (see Persistence).
- **Generated chunks are cached on disk, keyed on the generator.** `TerrainChunkCache` keeps at
  most `TerrainChunkCacheIndex.MaximumChunks`, oldest first, keyed on
  `TerrainGenerationFingerprint.CacheIdentity`: the live output fingerprint mixed with a
  build-time stamp of the generator's sources (`TerrainGeneratorSource.targets`). Any source
  change to generation therefore empties the cache on the next start.
- **Cached materials do not store density.** The chunk generator rebuilds scalar
  samples from the same versioned height recipe on cache hits. Saved material
  edits preserve their generated density magnitude when replayed, matching the
  Engine's material-edit path. Freeform density-brush edits are not persisted by
  the overworld's material overlay.
- **Residency follows the player only.** The request window is
  `TerrainConstants.RequestedChunkRadius`, the retained ring `RetainedChunkRadius`, capped at
  `MaximumResidentChunks`, admitting at most `MaximumResidencyOperationsPerTick` per update
  (chosen by measurement, #8895). Nothing is resident, and nothing has collision, outside that
  window.
- There is no biome framework, general procgen framework or product generation worker.

## Edits

- A view-aimed brush edit has radius at most `TerrainConstants.MaximumBrushRadius` within
  `TerrainConstants.EditReach`; a decided edit (a charge, a stamp) carries at most
  `TerrainBrushPolicy.MaximumTransactionCells` cells, a charge at most `BlastPolicy.MaximumCells`,
  and a stamp at most `BuildStamp.MaximumStampCells`.
- The player's edits are an overlay of at most `TerrainConstants.MaximumOverlayEntries` cells.
  An edit that would exceed it is refused at admission (`OverlayFull`) before the Engine is asked.
- Nothing is placed into the player's body: brush placement, stamps and block entities are
  refused on cells the player occupies. Teleport and respawn check standing-body clearance.
  If changing collision leaves the body deeply embedded, `PlayerRecovery` tries the last clear
  position and a bounded search above it; this is recovery, not permission to place into a body.
- **Building is cubic and separate from the terrain's materials.** The UI builds floors and walls
  from `BuildPalette` blocks, laid relative to where the player faces, and only over replaceable
  cells (air, water), so a floor across a slope fills the gaps and leaves the hill. Undo takes back
  the last floor or wall only, and leaves empty what it clears rather than restoring it.
- **A charge resolves on the update after it is fired**, as one transaction, with its cue raised
  first, so its smoke and debris (`Bursts.BlastSmoke`, `BlastDebris`, seeded from the charge's
  `BlastDust.ChargeIdentity`) are already in flight. A charge past `BlastPolicy.MaximumCells` is
  refused, not truncated; the debris cubes are untextured.
- Construction permissions, networking and multiplayer merge policy are not implemented.

## Presentation

- **Natural ground uses repeating triplanar maps.** `TerrainGroundMaterials` binds generated
  sage ground, ochre rock, dune sand and frost stone at the scale in `materials.json`.
  Grass and dirt share the sage material, with no directional top/side override.
  Construction, vegetation and water still use the provisional `terrain-atlas.*`, validated
  against `BlockRegistry` by `TerrainAtlasLayout`. Normal maps and animated tiles are not
  authored. Streamed ground and loaded landscape studies share four Engine terrain
  layers: grass/dirt → sage, stone → ochre, sand → dune, snow/gravel → frost.
  Physical slots stay distinct for gameplay, collision, picking and edits.
  `blending.json` sets transition reach (1–4 cells), weight contrast (at least 1),
  a texture-scale multiplier and a projection-sharpness multiplier independently.
  The Engine derives weights from nearby solid physical samples, including aliases;
  product geography and slope decide the samples. The base layer's triplanar sharpness
  applies to the whole blend; each map retains its own tile width. This is an opaque
  four-texture blend, not a general arbitrary-weight or unlimited-layer material.
  Dungeons keep their separate, unblended ground bindings. The provisional asset
  arrangement is not a constraint on replacement art.
- The C# runtime draws no shadow maps; the product has no shadow control.
- **Day and night are a sky blend and two lights.** `DayNightSky` crossfades two authored panoramas
  and sets one directional light (sun, then moon) and one ambient light from `WorldClock`; the
  Engine's neutral rig is disabled. Ambient light is unoccluded, so a cave is no darker than the
  open ground beside it, and the sun and moon do not move across the panoramas.
- **Under water the view closes into murk.** While the player's eyes are under water,
  `DayNightSky.Submerged` swaps the sky for one blue-green colour and fades distance into the same
  colour (exponential squared), in the open and underground alike. Water, glass and leaves are
  declared non-occluding to every voxel session (`VoxelMaterialRules`), so the bed and banks under
  a river draw from above and below and the surface is seen from beneath.

## Creatures and navigation

- **Creatures sleep outside the resident world.** A creature whose ground is not resident is
  dormant - not drawn, sensing or moving - and wakes as the player comes near; creatures spawn
  beyond hostile sight (`CreatureSpawnPlan`), so a fresh session's creatures start dormant.
- **Pursuit follows Engine navigation, published around the player.** `CreatureNavigation`
  publishes collision-derived navigation over a box around the player (its extents and cell
  budget are its constants) only while something pursues, and again after the player moves
  `RepublishDistanceMetres`, a rebase, or an edit. The Engine re-derives only the columns new to
  the box or near changed collision, as long as the box's vertical range is unchanged, so the
  range holds while the player's feet stay within `VerticalSlackMetres`; leaving that band, or
  the first publication, rebuilds the whole box, which is a visible hitch. A pursuer outside
  the box, or with no route, waits; creature movement is planar and follows the ground, so the
  grid's step height is what keeps pursuers out of pits.

## Survival

- **Hunger and air are `SurvivalRules`, tuned by `Difficulty`.** Food drains with time (faster
  while sprinting); a fed player regains health once calm, and pays for it in food; an empty
  stomach drains health but never below one point. Air runs out while the head is under water,
  and drowning can kill. Gentle turns off both kinds of harm. There is no temperature, thirst or
  disease.
- **Night is the creatures'.** Hostiles see `CreatureModule.NightSightFactor` further by night. A
  player rests only at night with no awake hostile within `SurvivalRules.RestSafetyMetres`; the
  night passes at once to `WorldClock.WakingFraction`, and health comes back as food pays for it.
- **Placed lights are lamps with real light**, but only the `LampLights.MaximumLitLamps` nearest
  the player are lit at once, and a lamp's light is unoccluded by walls (no shadows).

- **Supplies come from creatures and places, not from the ground.** `ItemCatalog` holds the items,
  `Recipes` the three recipes, and `SupplyCache` what a place holds on the first reach (a return
  holds nothing). What the player carries sits in slots (`InventorySlots`): 9 hotbar slots and 27
  pack slots in one numbering, each one stack of one kind up to `ItemCatalog.StackMaximum`, all
  within `ItemCatalog.CarryLimit`. The hotbar is slots of its own, not shortcuts to the pack: a
  thing lives in one slot. A pickup tops up its kind, then takes the first empty slot, hotbar first;
  spending takes from the pack before the hotbar; a move (dragged in the UI) goes into an empty
  slot, merges onto its kind, or swaps a whole stack. What does not fit is left behind and counted.
  One hotbar slot is selected (`InventoryModule.Selected`, not saved: a session starts at the
  first): 1-9 pick it, the wheel (`PlayerConstants.WheelStep` per step) and the controller's bumpers
  step it, wrapping, and a click selects it while the pointer is free. R, or the D-pad's up, uses
  what it holds: food is eaten and a bandage applied from that slot, a torch is placed as a light
  where the player aims, and a material is refused. The terrain brush's size moved from 1-3 to B,
  which cycles it. Building floors and walls is free; a
  light burns a torch. There are no tools, stations, equipment or containers that hold items; the
  pack screen's equipment slots are placeholders that refuse anything dropped on them.

## Dungeons

- **A dungeon is its own finite space**, a second spatial session filled from a `DungeonLayout`
  `DungeonModule.ChunksPerUpdate` chunks per update behind the loading screen, never streamed or
  rebased, and placed at `DungeonSpace.Origin` far below the open world so the two never meet in
  view. Each dungeon entrance has its own generated dungeon, drawn again until `DungeonWalk` finds
  it walkable as its flow intends. Three approaches are in comparison (#8604): A, `CarveAndStamp`
  (all cubic: a chasm, a ledge down its wall, a building it has torn open); C, `SculptedCave`
  (A's structure with its rock meshed smooth from a density, the building still cubic); and B,
  `ModularDungeon` (authored 3D pieces from `DungeonModules` joined at their sockets on a lattice
  of one storey per layer, rock sculpted as in C), chosen by `craft.dungeon.approach`. B's set
  pieces (the chasm with its ledge stair, the abyss, the cavern, the great hall) appear at most once
  per dungeon, each with a flat landing held open inside its cave ways on. Ledge stairs (the
  shafts, the chasm's stair) have masonry treads, which keep to the grid where reconstructed rock
  would round a step past the step height. `craft.dungeon.seed <n>` loads a chosen seed as the
  bank numbers them. Sculpting
  never changes what can be walked: standing places and their headroom stay open. Sculpted ground shares the world's
  triplanar materials; construction and non-ground surfaces still use the provisional atlas.
  Nothing inside a dungeon is saved: a session that ends inside one continues at its
  entrance, and the open world's creatures and journal wait while the player is in.
- **Dungeons are accepted by the Engine's navigation.** An entrance's dungeons are tried in a
  fixed order (`DungeonCandidates`, a pure function of its seed). Each one is generated until the
  product's own walk over its voxel data passes, loaded behind the loading screen, then checked by
  `DungeonRoutes`: collision navigation for the player's own character configuration (drops as far
  as `DungeonWalk.MaximumDrop`, steps as high as the player can jump) must walk every route the flow promises, or the next candidate is
  loaded. An entrance with no walkable dungeon in `DungeonCandidates.MaximumCandidates` is given up
  and the player stays outside. The bank enters entrances the same way headless through
  `EngineTestHost`. A and C are accepted on their first candidate; about a quarter of B's need a
  later one, mostly a step up under a cave chamber's curving ceiling or a smoothed riser a little
  over the player's step height, and each refused candidate adds about a second of loading.
  Dungeons load faceted by default; `craft.dungeon.surface cubes|dc|faceted|ruined|mc` chooses the
  next one's look. Every look but cubes loads the dungeon as voxels
  throughout (`DungeonSurfaces`): its sculpted rock becomes voxel densities the Engine reconstructs
  (dual contoured, smooth or flat-faceted, or marched), building blocks keep the grid (Blocky dual
  contouring, or cubes beside marched rock), all textured with the world's block materials, and
  collision and navigation follow the drawn surface. The rock is stone throughout; its strata and
  the cave-rock texture are the separate-mesh look's. Rock floors and ceilings sit on cell faces
  and only walls take the sculpted field. Reconstructed one-block steps still come out a few
  centimetres over a block, so the route check accepts a step up to what the player can jump
  (`NavigationProfile.JumpableStepMetres`, the jump's peak less a margin) while the player's own
  controller keeps its step height: such a step is a jump in play, and navigation has no jump
  edges to say so or to check the arc's headroom (Engine #9123). Roughness, which jostles every
  vertex, refuses more candidates: it is off in `dc` and small in `faceted`. `ruined` weathers
  brick only - sharp-featured, slightly rough, its sideways-open blocks worn back by density - since
  a sharp-featured floor or stair block has chamfered edges the body cannot step; planks,
  cobblestone and timber stay on the grid. Geometry is no finer than the one-metre voxel: the bank's
  `fine` probe measures a half-metre grid at about seven times the load and four to five times the
  navigation publication, with navigation cells kept at a metre (a cell must be the body's width).
  `craft.dungeon.blast <radius>` carves a sphere where the player aims through the Engine's density
  brush; the dungeon's navigation is not republished after it. Sculpted rock holds a body-width clearance around every standing place, and a step's lift of
  room above it, so a smoothed wall or ceiling never bulges into the body. Dungeons have no
  creatures.
- **Climbs are not routes.** A module's climb lanes (`ModuleCanvas.Climb`) are held open by
  sculpting like a standing body, so the face beside them stays on its cell boundary where
  `PlayerClimb`'s rail runs; any other sculpted face may bulge and stop a climb partway. The route
  check knows only walking (steps up to a jump's height, drops), so a climb or a drop deeper than
  `DungeonWalk.MaximumDrop` is never part of a promised route: a route that needs one is refused.
  `craft.dungeon.approach v` loads `VerticalSampler`, a hand-placed sketch of a bridged chasm with a
  route crossing beneath the bridge, and `s` its shaft sketch: a ledge winding five storeys down an
  open shaft.
- **Underground is dark by design.** `DayNightSky.Underground` drops the ambient fill to a trace and
  fades distance into near-black fog (exponential squared), so a dungeon is lit by its own lights
  and a drop reads as depth. The fog is a camera-view setting, turned off on the way back up.

## Persistence

- Every saved key is listed in `SaveManifest`, written through `ProductSaveSlot` over one store,
  and guarded by the revision this session last read or wrote: a key changed outside the session
  is reported, not overwritten.
- **Development worlds are ephemeral.** Preserving them does not justify migration work or
  constrain terrain, content or schema changes. A save written for another seed or generator version is discarded,
  kept as the key's one backup, and the world regenerates. Within a world, a key whose schema moves
  is discarded the same way unless its codec reads the old schema: the inventory reads its schema 1
  (one count per kind) and lays it out into slots.
- Player continuation restores position, look, vitals and progress, not controller motion: the
  player starts at rest. A position where a standing body no longer fits restores at home.
- The discovery journal holds at most `PoiConstants.MaximumDiscoveryEntries` places and refuses
  new ones when full.

## Player and input

- The character controller steps at `PlayerConstants.ControllerStepSeconds`; the world origin is
  rebased under the player past `PlayerConstants.RebaseThreshold`. Cross-origin multiplayer policy
  is not certified.
- **A body found deep in collision is moved, not stuck.** The controller pushes the player out of
  shallow overlaps itself; deeper, the Engine refuses the step, and `PlayerRecovery` stands the
  player at the first place their body fits - where they last stood clear, then straight above
  them within `PlayerRecovery.SearchHeightMetres`. With nowhere clear in reach the player stays
  put and it is tried again next update; it never searches sideways. `craft.player.readout`
  reports the count and the last recovery.
- **Keyboard input from a page reaches the product only after the Engine canvas has gameplay
  focus.** Keys pressed with the page body or a product UI control focused are not delivered (see
  [live-proofs](live-proofs.md)).
- **Climbing is deliberate and costs stamina.** The climb action (E, controller Y) takes hold of
  a face reached within `PlayerConstants.TakeHoldWindowSeconds` of pressing it; pushing into a wall
  never climbs. `PlayerClimb` decides the face: its block is `Climbable` in `BlockRegistry` (earth,
  rock, masonry, timber), it rises past a single step, and it is within `HoldReachMetres`. Forward
  climbs, back climbs down, the climb action again, a jump or crouch lets go, and the top lets go
  where the feet clear the ledge. `PlayerStamina` drains while holding on (faster while moving),
  comes back only on the ground, and lets go when it runs out; taking hold needs a quarter of the
  bar. A full bar's reach, `PlayerStamina.ClimbReachMetres`, is a storey and a half for a starting
  character: a layout puts a place out of reach with a taller face. Stamina is not saved, and
  sprinting does not spend it. There are no ladders or mantling animation.
- Controller mapping is product policy in `PlayerInputState` and the project's input intents.

## UI

- `src/ui` is a DOM companion over the product's UI projection. The game's HUD (`overlay.ts`)
  draws a crosshair, the vitals as segmented bars (stamina and air only while below full, climbing
  or under water), the product's prompt for where the player stands, short notices when something
  is found or done, and a red flash when health is lost (`hitsTaken` rising). The hotbar's slots sit
  at the foot of the view and a minimap of the journal's places (200 m, north up, an arrow for the
  facing) in the corner (`screens.ts`, `map.ts`). Two screens open over it (I and M, the buttons
  under the minimap, or a click on the minimap for the journal; Esc closes): the pack - the
  equipment placeholders, the pack and hotbar slots, and the recipe book with what each needs - and
  the journal of places, a north-up map around the player (north is -Z) and a list by distance.
  Stacks are dragged between slots while the pointer is free (`slots.ts`: Shift or right-drag for
  half, double-click to eat or apply); each drop claims a move and the product publishes the slots
  again. Items are drawn as a coloured block by use and an initial: there is no item art. The journal lists
  the 48 places most recently learned (`DiscoveryModule.JournalShown`), not every place found.
  Everything else - the published facts, the building actions, rest, difficulty, the controls - is
  the Menu drawer, closed by default. The UI holds no game state, only what it last drew and which
  screen is open; the screens' keys are read by the page, so a controller cannot open them yet. The
  projection is the product's one UI channel, stream `craftsurvive.game` and contract
  `craftsurvive.game.v1` (`ProductUiPublisher.StreamName`); kind and stage enums travel as
  numbers and may be appended to but never renumbered.
- Developer tools in the panel (renderer metrics, the live-debug panel) work only on a host
  started with `--live-debug`.

## Feedback: sound and particles

- **One cue, heard and seen.** Gameplay owners raise a `Cue` where they decide something happened
  (`Cues`, with where it happened in the local frame); `FeedbackModule` runs after them and has
  each cue heard (`SoundPlayer`) and seen (`BurstEmitter`). A refused clip, emission or burst is
  counted in `craft.feedback.readout` and play goes on; `craft.feedback.cue <name>` presents one
  cue in front of the player.
- **Bursts are a small vocabulary.** `Bursts` holds each burst's style - dust puffs on the authored
  `dust-puff` sprite sized in metres, or settling chips in the terrain atlas - and `Bursts.ByCue`
  which cues have one: a blast's smoke and debris, landing dust (more for a hard landing), a
  splash's drops and spray, a puff on a struck creature, a cloud when one goes down, and dust where
  a block is placed. Footsteps, jumps and interface cues have none. Bursts draw only through the
  Engine's one-shot emission and share its particle budget.
- **Every sound is generated.** `scripts/generate-sounds.mjs` synthesises the effects and four
  ambience loops (square waves, filtered noise, envelopes; seeded, so the files are reproducible)
  into `content/game/audio/`; rerun it after changing a sound. There is no music.
- **One owner plays them.** `SoundPlayer` alone plays cues through the Engine's `Audio` service
  and keeps the wind, night, cave and water beds looping, faded to the player's surroundings
  (`SoundCatalog.Level`). Only a blast is heard from where it happened; every other cue plays at
  the listener. The player's body cues come from `PlayerFootfalls`.
- **Sound plays in the page that watches.** With streamed frames the Engine mixes on the host and
  every watching page plays the mix once it has had a click or a key press (a browser plays sound
  only after a gesture); a native window plays on the runtime's own device.
- There are no volume settings in the UI, and creatures make no sound of their own beyond their
  swings.

## Lane, process and Engine gaps

- CI's managed lanes do not launch the runtime; the `product` job serves it, checks it keeps
  updating with a matching generator fingerprint, and walks the player on input.
- A product fault, or an exception from product `Dispose`, prints one line to the host's stderr;
  the full record is in a `--diagnostics-log` file, and `craft.runtime` reports `state=Faulted`.
- **The world runs while nobody watches.** The clock, hunger and creatures advance on Engine step
  time whether or not a page is attached, so a host left running starves its player. The product
  cannot yet tell that no page watches (rusty-engine #9360), so it cannot pause.
- The generated C# API exposes named Engine service families, not every Rust source-level API. A
  slice that needs an absent mechanism files or links the upstream capability request and stops
  its downstream substitute work.
