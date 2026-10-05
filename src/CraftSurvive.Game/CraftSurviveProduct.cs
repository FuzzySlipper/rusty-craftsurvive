using Rusty.Engine;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine.Debugging;
using CraftSurvive.Game.Modules.Actions;
using CraftSurvive.Game.Modules.Audio;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Debugging;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Inventory;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;

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
    private WorldMapPresentation? overview;

    /// <summary>The prototype faceted map view (#9436), built on first request and kept for this world.</summary>
    private WorldMapVoxelView? facetedMap;
    private bool facetedMapShown;
    private bool mapOpen;

    /// <summary>
    /// Whether the world's owners exist. A fresh store boots before its first world has been
    /// simulated; until an update admits that world, no terrain, player or gameplay state exists.
    /// </summary>
    private bool worldBuilt;
    private bool worldStoresRegistered;
    private string worldMessage = "";
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;

    /// <summary>The one world-to-local conversion; the player commits rebases through it.</summary>
    private WorldFrame frame = new();

    /// <summary>The product's one persistence store and one UI stream, shared by the owners that use them.</summary>
    private readonly ProductStore store;
    private readonly ProductUiPublisher ui;
    private TerrainWorld terrain = null!;
    private PlayerController player = null!;
    private DayNightSky sky = null!;

    /// <summary>The world's time and difficulty: survival and encounters read them, the sky shows them.</summary>
    private WorldConditionsModule conditions = null!;

    /// <summary>The player's hunger and air, which give and take health through the player's vitals.</summary>
    private SurvivalModule survival = null!;

    /// <summary>What the player carries: drops and caches in, crafting and use out.</summary>
    private InventoryModule inventory = null!;

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

    /// <summary>The light placed lamps give, from a pool of Engine lights.</summary>
    private LampLights lamps = null!;

    /// <summary>The cues gameplay raises each update, and the one owner that plays them and the ambience.</summary>
    private Cues cues = new();
    private FeedbackModule feedback = null!;

    /// <summary>The player-facing UI's action claims, turned into blast and build requests.</summary>
    private PlayerActionModule actions = null!;

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
        worlds = new WorldCatalog(engine, store);
        if (worlds.HasWorld) CreateWorld();
        creatureDebug = new CreatureDebugModule(() => Owner(creatures));
        entityDebug.RegisterProjection(PlayerController.RuntimeComponent,
            static (in PlayerRuntimeComponent state) => FormattableString.Invariant(
                $"position={state.X:F3},{state.Y:F3},{state.Z:F3};yaw={state.YawDegrees:F2};pitch={state.PitchDegrees:F2};grounded={state.Grounded};crouched={state.Crouched}"));
        productDebug = new CraftDebugModule(() => Owner(player), () => Owner(creatures), () => Owner(terrain), context.Debugging);
    }

    private void CreateWorld()
    {
        terrain = new TerrainWorld(context.Engine, context.Content, worlds.Current.Map.Configuration, frame, store, ui, worlds.Current.Map);
        terrain.Edited += entities.ApplyTerrainEdits;
        player = new PlayerController(context.Engine, terrain, frame, store, ui, cues);
        sky = new DayNightSky(context.Engine);
        conditions = new WorldConditionsModule(context.Engine, store, terrain.SaveIdentity, sky, () => player.HeadSubmerged, ui);
        creatures = new CreatureModule(context.Engine, terrain, player, frame, () => conditions.IsNight, cues);
        survival = new SurvivalModule(context.Engine, store, terrain.SaveIdentity, player, conditions, ui,
            () => creatures.NearestAwakeHostileMetres(player.WorldFeetPosition));
        discovery = new DiscoveryModule(context.Engine, terrain, player, store, ui);
        blast = new BlastModule(terrain, frame, entities, cues);
        build = new BuildModule(terrain, entities, player.Occupies);
        entityStore = new BlockEntityStore(context.Engine, store, terrain, entities);
        lamps = new LampLights(context.Engine, entities, player, frame);
        inventory = new InventoryModule(context.Engine, store, terrain.SaveIdentity, player, discovery, survival, ui, cues);
        dungeons = new DungeonModule(context.Engine, terrain, player, conditions, sky, ui);
        actions = new PlayerActionModule(player, blast, build, inventory, survival, conditions, dungeons, ui, cues, frame);
        feedback = new FeedbackModule(cues, new SoundPlayer(context.Engine), new BurstEmitter(context.Engine, () => terrain.AtlasSprite),
            () => new Surroundings(player.InSeparateSpace, player.HeadSubmerged, WorldClock.Daylight(conditions.Time.DayFraction)),
            player.LocalAhead);

        // The entity store runs after the edits, so it saves what a charge swept or a build placed this
        // update; feedback runs last, so it presents everything raised this update.
        gameplay = [conditions, dungeons, survival, creatures, discovery, inventory, blast, build, entityStore, lamps, feedback];
        worldBuilt = true;
        if (worldStoresRegistered)
        {
            entityDebug.ReplaceStore("craft", player.EntityStore);
            entityDebug.ReplaceStore("creatures", creatures.EntityStore);
        }
        else
        {
            entityDebug.RegisterStore("craft", player.EntityStore);
            entityDebug.RegisterStore("creatures", creatures.EntityStore);
            worldStoresRegistered = true;
        }
    }

    /// <summary>Start the world's owners and present it; disposes them all if any start fails.</summary>
    private void StartWorld()
    {
        try
        {
            terrain.Start();
            player.Start();
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
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        foreach (IProductModule module in gameplay.Reverse())
        {
            module.Dispose();
        }

        sky.Dispose();
        player.Dispose();
        terrain.Dispose();
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        RequireRegistration(registrar.Register(new WorldMapDebugModule(() => worlds, () => Owner(terrain), () => facetedMap)));
        RequireRegistration(registrar.Register(entityDebug));
        RequireRegistration(registrar.Register(productDebug));
        RequireRegistration(registrar.Register(creatureDebug));
        RequireRegistration(registrar.Register(new DiscoveryDebugModule(() => Owner(discovery))));
        RequireRegistration(registrar.Register(new BlastDebugModule(() => Owner(blast))));
        RequireRegistration(registrar.Register(new BuildDebugModule(() => Owner(build), () => Owner(entityStore))));
        RequireRegistration(registrar.Register(new SaveDebugModule(engine, store)));
        RequireRegistration(registrar.Register(new WorldConditionsDebugModule(() => Owner(conditions))));
        RequireRegistration(registrar.Register(new SurvivalDebugModule(() => Owner(survival))));
        RequireRegistration(registrar.Register(new InventoryDebugModule(() => Owner(inventory))));
        RequireRegistration(registrar.Register(new DungeonDebugModule(() => Owner(dungeons))));
        RequireRegistration(registrar.Register(new FeedbackDebugModule(() => Owner(feedback))));
        RequireRegistration(registrar.Register(CraftPlaytest.Create(() => Owner(player))));
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        try
        {
            ui.OpenStream();
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
            throw;
        }
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        bool watching = engine.CameraView.ReadSurface().Watching;
        if (!update.Facts.GameplayTimeSelected || (update.Facts.GameplayRate > 0) != watching)
            engine.GameplayTime.SetRate(watching ? 1 : 0);
        if (!watching)
        {
            if (worldBuilt) player.ClearInput();
            // The hold takes effect on the next observation. Finish any steps the Engine
            // already admitted so product rules and Engine presentation share that time;
            // then all product work, including streaming and saves, waits for a watcher.
            if (update.Facts.AdmittedStepCount == 0) return ProductUpdateResult.None;
        }
        if (AdmitPreparedWorld() || !worldBuilt) return ProductUpdateResult.None;
        if (HandleWorldActions(update)) return ProductUpdateResult.None;
        if (mapOpen)
        {
            if (facetedMapShown && facetedMap is not null && (facetedMap.Steer(update.Input) | facetedMap.MarkersStale)) PublishAppearanceSnapshot();
            AdvanceFacetedMap();
            return ProductUpdateResult.None;
        }
        ProductStep step = ProductStep.From(update.Facts);

        // The player moves first on this update's input, then the UI's requests are aimed from
        // where they now look; creatures, discovery and charges then read where the player is and
        // what they asked for this update.
        player.Update(update);
        actions.Update(update);
        foreach (IProductModule module in gameplay)
        {
            module.Update(step);
        }

        terrain.Update(step);
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

        if (worldBuilt) sky.Dispose();
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

        overview?.Dispose();
        facetedMap?.Dispose();
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        if (worldBuilt)
        {
            foreach (IProductModule module in gameplay.Reverse())
            {
                module.Dispose();
            }

            player.Dispose();
            terrain.Dispose();
            worldBuilt = false;
        }

        ui.Dispose();
        store.Dispose();
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

    /// <summary>The product's one complete appearance snapshot: every object it publishes.</summary>
    private void PublishAppearanceSnapshot()
    {
        if (!worldBuilt)
        {
            engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            return;
        }

        engine.Graphics.PublishSnapshot(!mapOpen || overview is null ? [.. creatures.AppearanceFacts, .. dungeons.AppearanceFacts]
            : facetedMapShown && facetedMap is not null ? facetedMap.Facts : overview.Facts);
        creatures.AfterAppearanceSnapshot();
        dungeons.AfterAppearanceSnapshot();
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
