using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Debugging;
using CraftSurvive.Game.Modules.Microvoxels;
using CraftSurvive.Game.Modules.GhostPlate;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game;

/// <summary>
/// The intentionally small NativeAOT product root. Gameplay domains join here
/// as they are migrated from the retained Rust/TypeScript donor implementation.
/// </summary>
public sealed class CraftSurviveProduct : IEngineProduct, IDebugCommandModuleSource
{
    private readonly IEngineContext engine;
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly MicrovoxelPresentation microvoxels;
    private readonly GhostPlateActor ghost;
    private readonly EntityWorldDebugModule entityDebug = new();
    private readonly CraftDebugModule productDebug;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        engine = context.Engine;
        terrain = new TerrainWorld(context.Engine, TerrainConfiguration.Default);
        player = new PlayerController(context.Engine, terrain);
        microvoxels = new MicrovoxelPresentation(
            context.Engine,
            context.Content,
            MicrovoxelConfiguration.WoodlandShrine);
        ghost = new GhostPlateActor(
            context.Engine,
            GhostPlateConfiguration.Default);
        entityDebug.RegisterWorld("craft", player.EntityWorld);
        entityDebug.RegisterProjection(PlayerController.RuntimeComponent,
            static (in PlayerRuntimeComponent state) => FormattableString.Invariant(
                $"position={state.X:F3},{state.Y:F3},{state.Z:F3};yaw={state.YawDegrees:F2};pitch={state.PitchDegrees:F2};grounded={state.Grounded};crouched={state.Crouched}"));
        productDebug = new CraftDebugModule(player, terrain, context.Debugging);
    }

    public void RegisterDebugCommands(IDebugCommandModuleRegistrar registrar)
    {
        RequireRegistration(registrar.Register(entityDebug));
        RequireRegistration(registrar.Register(productDebug));
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        bool ghostSourcePublished = false;
        try
        {
            terrain.Start();
            player.Start();
            PublishAppearanceSnapshot();
            ghostSourcePublished = true;
            ghost.Start();
            microvoxels.Start();
            lifecycle = ProductLifecycleState.Running;
        }
        catch
        {
            ghost.DisposePresentation();
            if (ghostSourcePublished)
            {
                PublishAppearanceSnapshot(includeGhostSource: false);
            }
            ghost.DisposeSourceAppearance();
            if (ghostSourcePublished)
            {
                engine.Appearance.PublishSnapshot(ReadOnlySpan<AppearanceFact>.Empty);
            }
            player.Dispose();
            microvoxels.Dispose();
            terrain.Dispose();
            throw;
        }
    }

    /// <summary>Republishes the retained world for an Engine-attached browser without resetting product state.</summary>
    public void Attach()
    {
        if (lifecycle is not (ProductLifecycleState.Running or ProductLifecycleState.Paused))
        {
            throw new InvalidOperationException("CraftSurvive can only attach while running or paused.");
        }

        terrain.Attach();
        player.Attach();
        PublishAppearanceSnapshot();
        ghost.Attach();
        microvoxels.Attach();
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        player.Update(update);
        ghost.Update(ghost.Placement);
        microvoxels.Update();
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
        ghost.Recapture();
        microvoxels.Restart();
        lifecycle = ProductLifecycleState.Running;
    }

    public void Shutdown()
    {
        if (lifecycle == ProductLifecycleState.Disposed)
        {
            throw new ObjectDisposedException(nameof(CraftSurviveProduct));
        }

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

        ghost.Dispose();
        player.Dispose();
        microvoxels.Dispose();
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

    private void PublishAppearanceSnapshot(bool includeGhostSource = true)
    {
        if (includeGhostSource)
        {
            engine.Appearance.PublishSnapshot(
            [
                player.PlatformAppearanceFact,
                ghost.SourceAppearanceFact,
            ]);
            return;
        }

        engine.Appearance.PublishSnapshot([player.PlatformAppearanceFact]);
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
