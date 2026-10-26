using Rusty.Engine;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Debugging;
using CraftSurvive.Game.Modules.Building;
using CraftSurvive.Game.Modules.Characters;
using CraftSurvive.Game.Modules.Chemistry;
using CraftSurvive.Game.Modules.Actions;
using CraftSurvive.Game.Modules.Audio;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Debugging;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Horizon;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Options;
using CraftSurvive.Game.Modules.Places;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.Towns;

namespace CraftSurvive.Game;

/// <summary>
/// The intentionally small product root. Gameplay domains join here while the
/// installed SDK supplies the generated CoreCLR and fidelity compositions.
/// </summary>
public sealed partial class CraftSurviveProduct : IEngineProduct, IDebugCommandModuleSource
{
    private readonly IEngineContext engine;
    private readonly ProductCreateContext context;
    private readonly WorldCatalog worlds;
    /// <summary>The world map (#9436, #9946), built when it first opens and kept for this world.</summary>
    private WorldMapVoxelView? mapView;
    /// <summary>The map's ground style (#9464), and one requested for the next update.</summary>
    private MapSurfaceStyle mapStyle = MapSurfaceStyle.Painted;
    private MapSurfaceStyle? requestedMapStyle;
    private bool mapOpen;

    /// <summary>
    /// Whether the world's owners exist. A fresh store boots before its first world has been
    /// simulated; until an update admits that world, no terrain, player or gameplay state exists.
    /// </summary>
    private bool worldBuilt;
    private string worldMessage = "";
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;

    /// <summary>The one world-to-local conversion; the player commits rebases through it.</summary>
    private WorldFrame frame = new();

    /// <summary>The product's one persistence store and one UI stream, shared by the owners that use them.</summary>
    private readonly ProductStore store;
    private readonly ProductUiPublisher ui;

    /// <summary>The game's own options (#9759), kept for the install; the Engine keeps the renderer's.</summary>
    private readonly GameOptionsStore optionsStore;
    private GameOptions gameOptions;
    private TerrainWorld terrain = null!;
    private PlayerController player = null!;
    private DayNightSky sky = null!;

    /// <summary>The world's time and difficulty: survival and encounters read them, the sky shows them.</summary>
    private WorldConditionsModule conditions = null!;

    /// <summary>The player's hunger and air, which give and take health through the player's vitals.</summary>
    private SurvivalModule survival = null!;
    private WeatherModule weather = null!;
    /// <summary>The snow lying on the ground (#10068).</summary>
    private SnowModule snow = null!;
    /// <summary>Breath and frost in the cold (#10068).</summary>
    private ColdModule cold = null!;
    /// <summary>Heat, moisture, smoke and fuel in the cells around the player: grass fire and the weather acting on it (#10293).</summary>
    private ChemistryModule chemistry = null!;
    /// <summary>Footprints in the snow (#10068).</summary>
    private SnowTracks tracks = null!;

    /// <summary>The rivers' stage from the weather and the seasons (#9878): fords, ice and dry beds.</summary>
    private Modules.Water.RiverStageModule riverStage = null!;

    /// <summary>What the player carries: drops and caches in, crafting and use out.</summary>
    private InventoryModule inventory = null!;
    private HomeMarkerStore home = null!;

    /// <summary>Going into dungeons and coming out: each its own finite space, loaded whole.</summary>
    private DungeonModule dungeons = null!;
    private readonly EntityStoreDebugModule entityDebug = new();
    private readonly CraftDebugModule productDebug;
    private CreatureModule creatures = null!;
    private readonly CreatureDebugModule creatureDebug;
    private DiscoveryModule discovery = null!;
    private BlastModule blast = null!;
    private BuildModule build = null!;
    private BlockEntityStore entityStore = null!;

    /// <summary>Building with pieces (#9729): what stands, its save, its drawing and its collision.</summary>
    private BuildPieceModule pieces = null!;

    /// <summary>The light placed lamps give, from a pool of Engine lights.</summary>
    private LampLights lamps = null!;

    /// <summary>The cues gameplay raises each update, and the one owner that plays them and the ambience.</summary>
    private Cues cues = new();
    private FeedbackModule feedback = null!;

    /// <summary>The player-facing UI's action claims, turned into blast and build requests.</summary>
    private PlayerActionModule actions = null!;

    /// <summary>The map on the skyline (#9779), in the Engine's backdrop.</summary>
    private HorizonBackdrop horizon = null!;

    /// <summary>The character style test's travellers (#10014): a test aid placed only by live debug.</summary>
    private CharacterStyleTest styleTest = null!;
    private PlaceStudyTest placeStudy = null!;

    /// <summary>One owner for block entities: placed by building, swept by every clearing edit.</summary>
    private BlockEntityIndex entities = new();

    /// <summary>
    /// The gameplay modules the player's update feeds, in the order they run: each reads what
    /// the ones before it decided this update.
    /// </summary>
    private IProductModule[] gameplay = null!;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
        engine = context.Engine;
        store = new ProductStore(context.Engine);
        ui = new ProductUiPublisher(context.Engine);
        townContent = TownContent.Read(context.Content);
        town = new TownModule(engine, townContent, ui.PublishTown);
        optionsStore = new GameOptionsStore(context.Engine);
        gameOptions = optionsStore.Load();
        ui.PublishOptions(gameOptions.Describe());
        worlds = new WorldCatalog(engine, store);
        if (worlds.HasWorld) CreateWorld();
        creatureDebug = new CreatureDebugModule(() => Owner(creatures));
        entityDebug.RegisterProjection(PlayerController.RuntimeComponent,
            static (in PlayerRuntimeComponent state) => FormattableString.Invariant(
                $"position={state.X:F3},{state.Y:F3},{state.Z:F3};yaw={state.YawDegrees:F2};pitch={state.PitchDegrees:F2};grounded={state.Grounded};crouched={state.Crouched}"));
        productDebug = new CraftDebugModule(() => Owner(player), () => Owner(creatures), () => Owner(terrain), context.Debugging);
    }

    /// <summary>Something solid within this far over the player's head shelters them from the weather (#9741).</summary>
    private const double ShelterReachMetres = 30;
    /// <summary>The recipe's continuous height is a voxel's base; the ground drawn is its top face, a metre up.</summary>
    private const double GroundTopFace = 1.0;

    private void CreateWorld()
    {
        terrain = new TerrainWorld(context.Engine, context.Content, worlds.Current.Map.Configuration, frame, store, ui, worlds.Current.Map);
        terrain.Edited += entities.ApplyTerrainEdits;
        // A continent's expedition starts at its sea port (#9895); a world without one at the spawn column.
        LocateTowns();
        System.Numerics.Vector2 start = StartColumn();
        player = new PlayerController(context.Engine, terrain, frame, store, ui, cues, start);
        pieces = new BuildPieceModule(context.Engine, context.Content, store, terrain, frame, cues);
        player.Pieces = pieces;
        BindBounds();
        sky = new DayNightSky(context.Engine, () => frame.ToLocal(player.WorldEyePosition));
        conditions = new WorldConditionsModule(context.Engine, store, terrain.SaveIdentity, sky, () => player.HeadSubmerged, ui);
        weather = new WeatherModule(worlds.Current.Map, sky, () => conditions.Time, () => player.WorldPosition,
            () => !player.InSeparateSpace && !player.HeadSubmerged,
            () => terrain.CoveredAbove(frame.ToLocal(player.WorldEyePosition), ShelterReachMetres));
        snow = new SnowModule(context.Engine, store, terrain.SaveIdentity, weather.Field, () => weather.Hours, () => player.WorldPosition,
            () => weather.Here, depth =>
            {
                terrain.SetSnow((float)depth);
                sky.Snow(SnowModule.CoverOf(depth));
            });
        weather.Freezing = () => snow.Air <= Modules.Water.Seasons.Freezing;
        riverStage = new Modules.Water.RiverStageModule(worlds.Current.Map, () => weather.Hours, terrain.RefreshWater,
            new Modules.Water.RiverStageStore(context.Engine, store, terrain.SaveIdentity));
        terrain.Water.Stages = riverStage;
        creatures = new CreatureModule(context.Engine, terrain, player, frame, () => conditions.IsNight, cues);
        survival = new SurvivalModule(context.Engine, store, terrain.SaveIdentity, player, conditions, ui,
            () => creatures.NearestAwakeHostileMetres(player.WorldFeetPosition), () => weather.Exposure, () => weather.Here.Dominant?.Front.Kind.Name ?? "");
        survival.Slept += hours => party?.Rest(hours);
        discovery = new DiscoveryModule(context.Engine, terrain, player, store, ui);
        home = new HomeMarkerStore(context.Engine, store, terrain.SaveIdentity, start);
        sled = new SledStore(context.Engine, context.Content, store, terrain.SaveIdentity, start);
        boat = new BoatStore(context.Engine, context.Content, store, terrain.SaveIdentity, frame, terrain.Water, terrain.WaveLift);
        player.Carrier = boat;
        blast = new BlastModule(terrain, frame, entities, cues);
        blast.ChargeAdmitted += (centre, radius) => pieces.Blast(centre, radius);
        build = new BuildModule(terrain, entities, player.Occupies);
        entityStore = new BlockEntityStore(context.Engine, store, terrain, entities);
        lamps = new LampLights(context.Engine, entities, player, frame);
        inventory = new InventoryModule(context.Engine, store, terrain.SaveIdentity, player, discovery, survival, ui, cues);
        dungeons = new DungeonModule(context.Engine, terrain, player, conditions, sky, ui);
        actions = new PlayerActionModule(player, blast, build, inventory, survival, conditions, dungeons, ui, cues, frame);
        feedback = new FeedbackModule(cues, new SoundPlayer(context.Engine), new BurstEmitter(context.Engine, () => terrain.AtlasSprite),
            () => new Surroundings(player.InSeparateSpace, player.HeadSubmerged, WorldClock.Daylight(conditions.Time.DayFraction)),
            player.LocalAhead);

        cold = new ColdModule(context.Engine, snow, frame, () => frame.ToLocal(player.WorldEyePosition), () => player.LocalAhead(1), () => player.Sprinting,
            () => !player.InSeparateSpace && !player.HeadSubmerged && !weather.Exposure.Sheltered,
            () => creatures.AwakeWithin(player.WorldPosition, ColdModule.CreatureBreathMetres), sky.Frost);

        chemistry = new ChemistryModule(context.Engine, context.Content, store, terrain, weather, frame, () => player.WorldPosition,
            () => player.InSeparateSpace, () => SnowModule.CoverOf(snow.Depth), sky.OutdoorFogVolumes);

        tracks = new SnowTracks(context.Engine, frame, snow.Cover, () => weather.Hours,
            () => (player.WorldFeetPosition, player.Grounded, !player.InSeparateSpace && !player.HeadSubmerged),
            () => creatures.AwakeWithin(player.WorldPosition, SnowTracks.CreatureMetres));

        // The entity store runs after the edits, so it saves what a charge swept or a build placed this
        // update; feedback runs last, so it presents everything raised this update.
        horizon = new HorizonBackdrop(context.Engine, worlds.Current.Map, () => terrain.Session, () => player.WorldPosition,
            () => player.ViewCamera, () => !player.InSeparateSpace, () => terrain.ViewDistanceChunks * (double)FarField.ChunkMetres, sky.Horizon)
        {
            Weather = new HorizonWeather(context.Engine, worlds.Current.Map, weather.Field, () => weather.Hours, () => player.WorldPosition,
                world => { System.Numerics.Vector3 local = frame.ToLocal(world.X, 0, world.Y); return new System.Numerics.Vector2(local.X, local.Z); }, sky.CloudRegions)
            {
                Daylight = () => double.IsNaN(sky.Daylight) ? 1 : sky.Daylight,
            },
            Landmarks = new HorizonLandmarks(context.Engine, context.Content, KnownPlacesNow, () => player.WorldPosition,
                (x, z) => terrain.Recipe.ContinuousHeightAt((long)Math.Floor(x), (long)Math.Floor(z)) + GroundTopFace,
                frame.ToLocal, () => terrain.NearEdgeMetres)
            {
                Daylight = () => double.IsNaN(sky.Daylight) ? 1 : sky.Daylight,
                PlaceStands = at => terrain.PlaceStands(at),
            },
        };
        gameplay = [conditions, weather, snow, riverStage, dungeons, survival, creatures, discovery, inventory, blast, build, pieces, entityStore, lamps, chemistry, feedback, cold, tracks, horizon];
        CreateTowns();
        styleTest = new CharacterStyleTest(context.Engine, context.Content, frame, (x, z) => terrain.GroundAt(x, z), () => player.WorldFeetPosition);
        placeStudy = new PlaceStudyTest(context.Engine, context.Content, frame, (x, z) => terrain.GroundAt(x, z), () => player.WorldFeetPosition, terrain.Moss);
        actions.ChooseOption = ChooseOption;
        worldBuilt = true;
        ApplyOptions();
        // Registering again replaces the earlier store (rusty-engine #9868): last write wins.
        entityDebug.RegisterStore("craft", player.EntityStore);
        entityDebug.RegisterStore("creatures", creatures.EntityStore);
    }

    /// <summary>
    /// Changes one of the game's own options from the options panel: keeps it for the install, applies
    /// it to the world at once (no reload), and publishes the options again.
    /// </summary>
    private string ChooseOption(string id, int value)
    {
        gameOptions = gameOptions.With(id, value);
        optionsStore.Save(gameOptions);
        ApplyOptions();
        ui.PublishOptions(gameOptions.Describe());
        GameOptionSpec spec = GameOptions.Specs.First(spec => spec.Id == id);
        return optionsStore.LastFailure is string failure
            ? $"{spec.Label} {value} {spec.Unit} for this session (not kept: {failure})"
            : $"{spec.Label} {value} {spec.Unit}";
    }

    /// <summary>Applies the game's options to the world's owners: the camera, how far the land is drawn, and the weather on screen.</summary>
    private void ApplyOptions()
    {
        if (!worldBuilt) return;
        player.FieldOfViewDegrees = gameOptions.FieldOfViewDegrees;
        terrain.ViewDistanceChunks = gameOptions.ViewDistanceChunks;
        terrain.GroundCoverMetres = gameOptions.GroundCoverMetres;
        player.FarDistance = Math.Max(PlayerConstants.CameraFarDistance, FarField.FarthestMetres(gameOptions.ViewDistanceChunks));
        sky.Options(gameOptions.WeatherEffectsPercent / 100f, (float)gameOptions.ViewDistanceChunks / FarField.DefaultRadiusChunks);
    }

    /// <summary>Start the world's owners and present it; disposes them all if any start fails.</summary>
    private void StartWorld()
    {
        try
        {
            // Vegetation occlusion (#10020) stays the Engine's opt-in default, off: since canopies stopped counting as
            // occluders it hid 1-10% of copies for no world GPU time and cost 0.6-0.85 ms of host render (#10033).
            // `craft.renderer.occlusion on` turns it on for a session.
            terrain.Start();
            player.Start();
            home.Start();
            sled.Start();
            boat.Start();
            foreach (IProductModule module in gameplay)
            {
                module.Start();
            }

            PublishAppearanceSnapshot();
        }
        catch
        {
            DisposeWorld();
            throw;
        }
    }

    private void DisposeWorld()
    {
        if (!worldBuilt) return;
        worldBuilt = false;
        ReleaseTowns();
        styleTest.Clear();
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        townPresence?.AfterAppearanceSnapshot();
        styleTest.Dispose();
        placeStudy.Dispose();
        foreach (IProductModule module in gameplay.Reverse())
        {
            module.Dispose();
        }

        DisposeTowns();
        sled.Save();
        sled.Dispose();
        boat.Save();
        boat.Dispose();
        sky.Dispose();
        player.Dispose();
        terrain.Dispose();
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        RequireRegistration(registrar.Register(new TravelDebugModule(() => worldBuilt ? TravelReadout() : "no world", kind => worldBuilt ? ForceTravelEvent(kind) : "no world",
            (x, z) => worldBuilt ? BoundReadout(x, z) : "no world", (x, z) => worldBuilt ? PlanReadout(x, z) : "no world",
            place => worldBuilt ? PlanToPlaceReadout(place) : "no world")));
        RequireRegistration(registrar.Register(new WorldMapDebugModule(() => worlds, () => Owner(terrain), () => mapView, style => requestedMapStyle = MapSurfaceStyle.Named(style),
            view => worldBuilt ? MapViewReadout(view) : "no world")));
        RequireRegistration(registrar.Register(new MenuDebugModule(PauseReadout)));
        RequireRegistration(registrar.Register(new TownDebugModule(key => EnterTown(key is "" or TownDoorstepKey ? null : key), LeaveTown, () => town.Readout() + ";" + (townPresence?.Readout ?? "placed=none"), TownMotion,
            () => worldBuilt ? TownSites() : "no world", ApproachTown, SelectTownService, ChooseTownService)));
        RequireRegistration(registrar.Register(new InteractionDebugModule(town.Interactions)));
        RequireRegistration(registrar.Register(entityDebug));
        RequireRegistration(registrar.Register(productDebug));
        RequireRegistration(registrar.Register(creatureDebug));
        RequireRegistration(registrar.Register(new CharacterStyleDebugModule(() => Owner(styleTest))));
        RequireRegistration(registrar.Register(new PlaceStudyDebugModule(() => Owner(placeStudy))));
        RequireRegistration(registrar.Register(new DiscoveryDebugModule(() => Owner(discovery))));
        RequireRegistration(registrar.Register(new BlastDebugModule(() => Owner(blast))));
        RequireRegistration(registrar.Register(new BuildDebugModule(() => Owner(build), () => Owner(entityStore), () => Owner(lamps))));
        RequireRegistration(registrar.Register(new SaveDebugModule(engine, store)));
        RequireRegistration(registrar.Register(new WorldConditionsDebugModule(() => Owner(conditions))));
        RequireRegistration(registrar.Register(new WeatherDebugModule(() => Owner(weather), () => Owner(snow), () => Owner(cold), () => Owner(tracks))));
        RequireRegistration(registrar.Register(new Modules.Travel.BoatDebugModule(() => worldBuilt ? BoatReadout() : "no world",
            kind => worldBuilt ? DebugBuildBoat(kind) : "no world",
            (forward, turn, seconds) => worldBuilt ? DebugPaddle(forward, turn, seconds) : "no world",
            () => worldBuilt ? DebugBoard() : "no world",
            (action, speed) => worldBuilt ? DebugTravel(action, speed) : "no world")));
        RequireRegistration(registrar.Register(new Modules.Water.RiverStageDebugModule(() => Owner(riverStage), () => Owner(terrain).Water,
            (x, z) => Owner(terrain).Recipe.GroundAt((long)Math.Floor(x), (long)Math.Floor(z)).Geography.RiverReach)));
        RequireRegistration(registrar.Register(new HorizonDebugModule(() => Owner(horizon), () => Owner(sky))));
        RequireRegistration(registrar.Register(new Modules.Debugging.RendererDebugModule(engine)));
        RequireRegistration(registrar.Register(new SurvivalDebugModule(() => Owner(survival))));
        RequireRegistration(registrar.Register(new InventoryDebugModule(() => Owner(inventory))));
        RequireRegistration(registrar.Register(new DungeonDebugModule(() => Owner(dungeons))));
        RequireRegistration(registrar.Register(new FeedbackDebugModule(() => Owner(feedback))));
        RequireRegistration(registrar.Register(new ChemistryDebugModule(() => Owner(chemistry),
            () => { System.Numerics.Vector3 at = Owner(player).WorldPosition; return (at.X, at.Z); })));
        RequireRegistration(registrar.Register(CraftPlaytest.Create(() => Owner(player))));
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        try
        {
            ui.OpenStream();
            PublishMenu();
            if (worldBuilt) StartWorld();
            else worldMessage = FirstWorldMessage;
            lifecycle = ProductLifecycleState.Running;
            PublishWorld();
            // Opt into observation updates so a watching page can resume a held world.
            engine.GameplayTime.RunRealtime();
        }
        catch
        {
            ui.Dispose();
            store.Dispose();
            optionsStore.Dispose();
            throw;
        }
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        town.Present(update.Facts.HostElapsedSeconds);
        if (worldBuilt) feedback.SceneGain(town.OutdoorGain);
        bool watching = engine.CameraView.ReadSurface().Watching;
        // The clock runs while someone watches and the game is not held under the menu (#9916).
        bool running = watching && !paused;
        if (!update.Facts.GameplayTimeSelected || (update.Facts.GameplayRate > 0) != running)
            engine.GameplayTime.SetRate(running ? 1 : 0);
        if (!watching)
        {
            if (worldBuilt) player.ClearInput();
            town.ClearInput();
            // The hold takes effect on the next observation. Finish any steps the Engine
            // already admitted so product rules and Engine presentation share that time;
            // then all product work, including streaming and saves, waits for a watcher.
            if (update.Facts.AdmittedStepCount == 0) return ProductUpdateResult.None;
        }
        if (AdmitPreparedWorld() || !worldBuilt) return ProductUpdateResult.None;
        if (MenuButtonPressed(update)) SetPaused(!paused);
        // Options apply in every state, the pause menu and the map included; play's own actions wait below.
        actions.UpdateOptions(update);
        if (HandleTownActions(update)) return ProductUpdateResult.None;
        if (HandleWorldActions(update)) return ProductUpdateResult.None;
        if (paused)
        {
            // As when nobody watches: the steps the Engine already admitted finish, then everything waits.
            player.ClearInput();
            if (update.Facts.AdmittedStepCount == 0) return ProductUpdateResult.None;
        }
        if (town.Open)
        {
            // A visit costs no expedition time: the clock, survival and creatures wait. The town's
            // presentation moves on, and the ground around the traveller keeps streaming in.
            ProductStep visit = ProductStep.From(update.Facts);
            town.Update(update.Input, visit.ElapsedSeconds);
            townPresence?.Advance(update.Facts.HostElapsedSeconds);
            styleTest.Advance(update.Facts.HostElapsedSeconds);
            terrain.AdvanceSea(visit.ElapsedSeconds, weather.Hours, sky.Look.Wind, sky.Look.Flow, player.WorldPosition);
            sky.Sway(visit.ElapsedSeconds);
            player.HoldResidency();
            PublishAppearanceSnapshot();
            return ProductUpdateResult.None;
        }
        if (mapOpen)
        {
            mapView?.Profile.Begin();
            ApplyRequestedMapStyle();
            if (mapView is not null && mapView.SteerWaypoint(update.Input))
            {
                try { PlanTravel(mapView.Waypoint, "the waypoint"); }
                catch (FormatException refused) { worldMessage = refused.Message; }
                PublishWorld();
            }
            AdvanceTravel(ProductStep.From(update.Facts).ElapsedSeconds);
            OfferAtParty();
            // The walking modules rest while the map is open; the river stage follows the clock regardless (#9878 review).
            riverStage.Follow();
            ShowWeatherOnMap();
            mapView?.Profile.Mark("travel");
            if (mapView is not null && (mapView.Steer(update.Input) | mapView.MarkersStale)) PublishAppearanceSnapshot();
            AdvanceMapView();
            mapView?.Profile.End();
            return ProductUpdateResult.None;
        }
        ProductStep step = ProductStep.From(update.Facts);

        // The waves move on first (#9902), so the boat and a swimmer ride, and the water draws, one sea this update.
        WeatherLook wind = sky.Look;
        terrain.AdvanceSea(step.ElapsedSeconds, weather.Hours, wind.Wind, wind.Flow, player.WorldPosition);
        // The scene wind's lulls and gusts move on too; the weather's update hands them to the Engine.
        sky.Sway(step.ElapsedSeconds);

        // The player moves first on this update's input, then the UI's requests are aimed from
        // where they now look; creatures, discovery and charges then read where the player is and
        // what they asked for this update.
        player.Update(update);
        NoteTurnBacks(step.ElapsedSeconds);
        FollowTowns(update.Facts.HostElapsedSeconds);
        AccrueWalkingFatigue();
        actions.Update(update);
        foreach (IProductModule module in gameplay)
        {
            module.Update(step);
        }

        terrain.Update(step);
        styleTest.Advance(update.Facts.HostElapsedSeconds);
        PublishSled();
        PublishAppearanceSnapshot();
        return ProductUpdateResult.None;
    }

    public void Pause()
    {
        RequireState(ProductLifecycleState.Running, nameof(Pause));
        lifecycle = ProductLifecycleState.Paused;
    }

    public void Resume()
    {
        RequireState(ProductLifecycleState.Paused, nameof(Resume));
        lifecycle = ProductLifecycleState.Running;
    }

    public void Restart()
    {
        if (lifecycle is not (ProductLifecycleState.Running or ProductLifecycleState.Paused))
        {
            throw new InvalidOperationException($"{nameof(Restart)} requires a running or paused product.");
        }

        if (!worldBuilt)
        {
            lifecycle = ProductLifecycleState.Running;
            engine.GameplayTime.RunRealtime();
            return;
        }

        LeaveTown();
        terrain.Restart();
        player.Restart();
        actions.Restart();
        foreach (IProductModule module in gameplay)
        {
            module.Restart();
        }

        PublishAppearanceSnapshot();
        lifecycle = ProductLifecycleState.Running;
        engine.GameplayTime.RunRealtime();
    }

    public void Shutdown()
    {
        if (lifecycle == ProductLifecycleState.Disposed)
        {
            throw new ObjectDisposedException(nameof(CraftSurviveProduct));
        }

        if (worldBuilt) { LeaveTown(); sky.Dispose(); }
        lifecycle = ProductLifecycleState.Shutdown;
    }

    public void Dispose()
    {
        if (lifecycle == ProductLifecycleState.Disposed)
        {
            return;
        }

        if (lifecycle != ProductLifecycleState.Shutdown)
        {
            Shutdown();
        }

        // The snapshot lets go of the map's appearances before they are disposed.
        town.Leave();
        townPresence?.Release();
        if (worldBuilt) styleTest.Clear();
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        town.Dispose();
        mapView?.Dispose();
        if (worldBuilt)
        {
            styleTest.Dispose();
            placeStudy.Dispose();
            foreach (IProductModule module in gameplay.Reverse())
            {
                module.Dispose();
            }

            DisposeTowns();

            player.Dispose();
            terrain.Dispose();
            worldBuilt = false;
        }

        ui.Dispose();
        store.Dispose();
        optionsStore.Dispose();
        lifecycle = ProductLifecycleState.Disposed;
    }

    private void RequireState(ProductLifecycleState expected, string operation)
    {
        if (lifecycle != expected)
        {
            throw new InvalidOperationException(
                $"{operation} requires {expected} but CraftSurvive is {lifecycle}.");
        }
    }

    /// <summary>A world owner for diagnostics, or a named pending answer before the first world exists.</summary>
    private T Owner<T>(T? owner) where T : class => WorldOwners.Require(worldBuilt, owner);

    private static void RequireRegistration(DebugCommandRegistrationResult registration)
    {
        if (!registration.Succeeded)
        {
            throw new InvalidOperationException(registration.Message);
        }
    }

    /// <summary>The overworld's water, studies, places and build pieces, unless the player is in a separate space (a dungeon or a study).</summary>
    private AppearanceFact[] OverworldFacts() => player.InSeparateSpace ? [] : [.. terrain.WaterFacts, .. terrain.StudyFacts, .. terrain.PlaceFacts, .. pieces.Facts()];

    /// <summary>The style test's travellers and the place study's variants, unless the player is in a separate space.</summary>
    private IEnumerable<AppearanceFact> StyleFacts() => player.InSeparateSpace ? [] : [.. styleTest.Facts, .. placeStudy.Facts];

    /// <summary>The product's one complete appearance snapshot: every object it publishes.</summary>
    private void PublishAppearanceSnapshot()
    {
        if (!worldBuilt)
        {
            engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            return;
        }

        AppearanceFact[] facts = town.Open || !mapOpen || mapView is null
            ? [.. creatures.AppearanceFacts, .. dungeons.AppearanceFacts, .. SledFacts(), .. BoatFacts(), .. OverworldFacts(), .. horizon.Facts, .. TownFacts(), .. StyleFacts()]
            : mapView.Facts;
        mapView?.Profile.Mark("snapshot.facts");
        engine.Graphics.PublishSnapshot(facts);
        mapView?.Profile.Mark("snapshot.publish");
        creatures.AfterAppearanceSnapshot();
        dungeons.AfterAppearanceSnapshot();
        terrain.AfterAppearanceSnapshot();
        townPresence?.AfterAppearanceSnapshot();
        styleTest.AfterAppearanceSnapshot();
        if (mapOpen) mapView?.AfterAppearanceSnapshot();
        mapView?.Profile.Mark("snapshot.after");
    }

    private enum ProductLifecycleState
    {
        Created,
        Running,
        Paused,
        Shutdown,
        Disposed,
    }
}
