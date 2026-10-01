using Rusty.Engine;
using Rusty.Engine.Debugging;
using CraftSurvive.Game.Modules.Actions;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Debugging;
using CraftSurvive.Game.Modules.Discovery;
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
public sealed class CraftSurviveProduct : IEngineProduct, IDebugCommandModuleSource
{
    private readonly IEngineContext engine;
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;

    /// <summary>The one world-to-local conversion; the player commits rebases through it.</summary>
    private readonly WorldFrame frame = new();

    /// <summary>The product's one persistence store and one UI stream, shared by the owners that use them.</summary>
    private readonly ProductStore store;
    private readonly ProductUiPublisher ui;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly DayNightSky sky;

    /// <summary>The world's time and difficulty: survival and encounters read them, the sky shows them.</summary>
    private readonly WorldConditionsModule conditions;

    /// <summary>The player's hunger and air, which give and take health through the player's vitals.</summary>
    private readonly SurvivalModule survival;
    private readonly EntityStoreDebugModule entityDebug = new();
    private readonly CraftDebugModule productDebug;
    private readonly CreatureModule creatures;
    private readonly CreatureDebugModule creatureDebug;
    private readonly DiscoveryModule discovery;
    private readonly BlastModule blast;
    private readonly BuildModule build;
    private readonly BlockEntityStore entityStore;

    /// <summary>The player-facing UI's action claims, turned into blast and build requests.</summary>
    private readonly PlayerActionModule actions;

    /// <summary>One owner for block entities: placed by building, swept by a charge.</summary>
    private readonly BlockEntityIndex entities = new();

    /// <summary>
    /// The gameplay modules the player's update feeds, in the order they run: each reads what
    /// the ones before it decided this update.
    /// </summary>
    private readonly IProductModule[] gameplay;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        engine = context.Engine;
        store = new ProductStore(context.Engine);
        ui = new ProductUiPublisher(context.Engine);
        terrain = new TerrainWorld(context.Engine, context.Content, TerrainConfiguration.Default, frame, store, ui);
        player = new PlayerController(context.Engine, terrain, frame, store, ui);
        sky = new DayNightSky(context.Engine);
        conditions = new WorldConditionsModule(context.Engine, store, terrain.SaveIdentity, sky, ui);
        survival = new SurvivalModule(context.Engine, store, terrain.SaveIdentity, player, conditions, ui);
        creatures = new CreatureModule(context.Engine, terrain, player, frame);
        creatureDebug = new CreatureDebugModule(creatures);
        discovery = new DiscoveryModule(context.Engine, terrain, player, store, ui);
        blast = new BlastModule(context.Engine, terrain, frame, entities);
        build = new BuildModule(terrain, entities, player.Occupies);
        entityStore = new BlockEntityStore(context.Engine, store, terrain, entities);
        actions = new PlayerActionModule(player, blast, build, ui);

        // The entity store runs last, so it saves what a charge swept or a build placed this update.
        gameplay = [conditions, survival, creatures, discovery, blast, build, entityStore];
        entityDebug.RegisterStore("craft", player.EntityStore);
        entityDebug.RegisterStore("creatures", creatures.EntityStore);
        entityDebug.RegisterProjection(PlayerController.RuntimeComponent,
            static (in PlayerRuntimeComponent state) => FormattableString.Invariant(
                $"position={state.X:F3},{state.Y:F3},{state.Z:F3};yaw={state.YawDegrees:F2};pitch={state.PitchDegrees:F2};grounded={state.Grounded};crouched={state.Crouched}"));
        productDebug = new CraftDebugModule(player, creatures, terrain, context.Debugging);
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        RequireRegistration(registrar.Register(entityDebug));
        RequireRegistration(registrar.Register(productDebug));
        RequireRegistration(registrar.Register(creatureDebug));
        RequireRegistration(registrar.Register(new DiscoveryDebugModule(discovery)));
        RequireRegistration(registrar.Register(new BlastDebugModule(blast)));
        RequireRegistration(registrar.Register(new BuildDebugModule(build, entityStore)));
        RequireRegistration(registrar.Register(new SaveDebugModule(engine, store)));
        RequireRegistration(registrar.Register(new WorldConditionsDebugModule(conditions)));
        RequireRegistration(registrar.Register(new SurvivalDebugModule(survival)));
        RequireRegistration(registrar.Register(CraftPlaytest.Create(player)));
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        try
        {
            terrain.Start();
            player.Start();
            foreach (IProductModule module in gameplay)
            {
                module.Start();
            }

            PublishAppearanceSnapshot();
            lifecycle = ProductLifecycleState.Running;
        }
        catch
        {
            sky.Dispose();
            engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            foreach (IProductModule module in gameplay)
            {
                module.Dispose();
            }

            player.Dispose();
            terrain.Dispose();
            ui.Dispose();
            store.Dispose();
            throw;
        }
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
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

        terrain.Restart();
        player.Restart();
        actions.Restart();
        foreach (IProductModule module in gameplay)
        {
            module.Restart();
        }

        PublishAppearanceSnapshot();
        lifecycle = ProductLifecycleState.Running;
    }

    public void Shutdown()
    {
        if (lifecycle == ProductLifecycleState.Disposed)
        {
            throw new ObjectDisposedException(nameof(CraftSurviveProduct));
        }

        sky.Dispose();
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

        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        foreach (IProductModule module in gameplay.Reverse())
        {
            module.Dispose();
        }

        player.Dispose();
        terrain.Dispose();
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
        engine.Graphics.PublishSnapshot([.. creatures.AppearanceFacts]);
        creatures.AfterAppearanceSnapshot();
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
