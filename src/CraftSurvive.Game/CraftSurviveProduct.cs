using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Debugging;
using CraftSurvive.Game.Modules.Microvoxels;
using CraftSurvive.Game.Modules.GhostPlate;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.LevelGeneration;
using CraftSurvive.Game.Modules.Proofing;
using CraftSurvive.Game.Modules.Discovery;
using CraftSurvive.Game.Modules.Manipulation;
using CraftSurvive.Game.Modules.Creatures;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Studies;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine.Debugging;

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
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly MicrovoxelPresentation? microvoxels;
    private readonly GhostPlateActor? ghost;
    private readonly SkyBackground sky;
    private readonly EntityStoreDebugModule entityDebug = new();
    private readonly CraftDebugModule productDebug;
    private readonly ProcgenWorkbench? workbench;
    private readonly ProcgenDebugModule? procgenDebug;
    private readonly CreatureModule creatures;
    private readonly CreatureDebugModule creatureDebug;
    private readonly DiscoveryModule discovery;
    private readonly BlastModule blast;
    private readonly BuildModule build;

    /// <summary>One owner for block entities: placed by building, swept by a charge.</summary>
    private readonly BlockEntityIndex entities = new();

    /// <summary>
    /// The gameplay modules the player's update feeds, in the order they run: each reads what
    /// the ones before it decided this update.
    /// </summary>
    private readonly IProductModule[] gameplay;
    private readonly LiveSubstrateProof? substrateProof;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        engine = context.Engine;
        terrain = new TerrainWorld(context.Engine, context.Content, TerrainConfiguration.Default, frame);
        player = new PlayerController(context.Engine, terrain, frame);
        // The workbench, the microvoxel shrine and the ghost plate are development
        // instruments from the slices that built them, not parts of the survival
        // world: they are constructed only when the studies are asked for, so the
        // ordinary runtime scene carries the game and nothing else.
        if (ProductStudies.Enabled)
        {
            workbench = new ProcgenWorkbench(engine, context.Content, terrain, player);
            procgenDebug = new ProcgenDebugModule(workbench);
            microvoxels = new MicrovoxelPresentation(
                context.Engine,
                context.Content,
                MicrovoxelConfiguration.WoodlandShrine);
            ghost = new GhostPlateActor(
                context.Engine,
                GhostPlateConfiguration.Default);
        }
        sky = new SkyBackground(context.Engine);
        creatures = new CreatureModule(context.Engine, terrain, player, frame);
        creatureDebug = new CreatureDebugModule(creatures);
        discovery = new DiscoveryModule(context.Engine, terrain, player);
        blast = new BlastModule(context.Engine, terrain, frame, entities);
        build = new BuildModule(terrain, entities);
        gameplay = [creatures, discovery, blast, build];
        entityDebug.RegisterStore("craft", player.EntityStore);
        entityDebug.RegisterStore("creatures", creatures.EntityStore);
        entityDebug.RegisterProjection(PlayerController.RuntimeComponent,
            static (in PlayerRuntimeComponent state) => FormattableString.Invariant(
                $"position={state.X:F3},{state.Y:F3},{state.Z:F3};yaw={state.YawDegrees:F2};pitch={state.PitchDegrees:F2};grounded={state.Grounded};crouched={state.Crouched}"));
        productDebug = new CraftDebugModule(
            player,
            creatures,
            terrain,
            ghost,
            microvoxels,
            context.Debugging);
        if (LiveSubstrateProof.Requested)
        {
            substrateProof = new LiveSubstrateProof(context.Engine, terrain, player);
        }
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        RequireRegistration(registrar.Register(entityDebug));
        RequireRegistration(registrar.Register(productDebug));
        RequireRegistration(registrar.Register(creatureDebug));
        RequireRegistration(registrar.Register(discovery));
        RequireRegistration(registrar.Register(blast));
        RequireRegistration(registrar.Register(build));
        if (procgenDebug is not null)
        {
            RequireRegistration(registrar.Register(procgenDebug));
        }
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        bool ghostSourcePublished = false;
        try
        {
            terrain.Start();
            player.Start();
            foreach (IProductModule module in gameplay)
            {
                module.Start();
            }

            sky.Start();
            PublishAppearanceSnapshot();
            ghostSourcePublished = true;
            ghost?.Start();
            if (terrain.IsCourtyard)
            {
                microvoxels?.SetVisible(false);
                ghost?.QueueVisibility(false);
            }

            microvoxels?.Start();
            lifecycle = ProductLifecycleState.Running;
        }
        catch
        {
            sky.Dispose();
            ghost?.DisposePresentation();
            if (ghostSourcePublished)
            {
                PublishAppearanceSnapshot(includeGhostSource: false);
            }
            ghost?.DisposeSourceAppearance();
            if (ghostSourcePublished)
            {
                engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            }
            foreach (IProductModule module in gameplay)
            {
                module.Dispose();
            }

            player.Dispose();
            microvoxels?.Dispose();
            terrain.Dispose();
            throw;
        }
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        ProductStep step = ProductStep.From(update.Facts);
        terrain.UpdateCourtyard();
        workbench?.Update(update);

        // The player moves first on this update's input; creatures, discovery and charges then
        // read where the player is now and what they asked for this update.
        player.Update(update);
        foreach (IProductModule module in gameplay)
        {
            module.Update(step);
        }

        // Publish the complete source fact at its queued transform before the
        // retained ghost operation observes the same desired placement.
        PublishAppearanceSnapshot(useDesiredGhostSource: true);
        terrain.ReleaseRetiredCourtyard();
        ghost?.Update();
        microvoxels?.Update();
        substrateProof?.Update();
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
        sky.Restart();
        player.Restart();
        foreach (IProductModule module in gameplay)
        {
            module.Restart();
        }

        PublishAppearanceSnapshot(useDesiredGhostSource: true);
        ghost?.Recapture();
        microvoxels?.Restart();
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

        ghost?.DisposePresentation();
        engine.Graphics.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
        ghost?.Dispose();
        foreach (IProductModule module in gameplay.Reverse())
        {
            module.Dispose();
        }

        player.Dispose();
        microvoxels?.Dispose();
        terrain.Dispose();
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

    private void PublishAppearanceSnapshot(
        bool includeGhostSource = true,
        bool useDesiredGhostSource = false)
    {
        // Without the studies there is no ghost source, and the gameplay facts
        // are the whole snapshot.
        AppearanceFact[] gameplay =
        [
            .. terrain.CourtyardFacts,
            .. player.Ropes.Facts,
            player.PlatformAppearanceFact,
            .. creatures.AppearanceFacts,
        ];
        if (!includeGhostSource || ghost is null)
        {
            engine.Graphics.PublishSnapshot(gameplay);
        }
        else
        {
            engine.Graphics.PublishSnapshot(
            [
                .. gameplay,
                useDesiredGhostSource ? ghost.DesiredSourceAppearanceFact : ghost.SourceAppearanceFact,
            ]);
        }

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
