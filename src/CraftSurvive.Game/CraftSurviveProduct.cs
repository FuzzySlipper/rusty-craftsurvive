using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Debugging;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game;

/// <summary>
/// The intentionally small NativeAOT product root. Gameplay domains join here
/// as they are migrated from the retained Rust/TypeScript donor implementation.
/// </summary>
public sealed class CraftSurviveProduct : IEngineProduct, IDebugCommandModuleSource
{
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;
    private readonly TerrainWorld terrain;
    private readonly PlayerController player;
    private readonly EntityWorldDebugModule entityDebug = new();
    private readonly CraftDebugModule productDebug;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        terrain = new TerrainWorld(context.Engine, TerrainConfiguration.Default);
        player = new PlayerController(context.Engine, terrain);
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
        try
        {
            terrain.Start();
            player.Start();
            lifecycle = ProductLifecycleState.Running;
        }
        catch
        {
            player.Dispose();
            terrain.Dispose();
            throw;
        }
    }

    public ProductUpdateResult Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        player.Update(update);
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

        player.Dispose();
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

    private enum ProductLifecycleState
    {
        Created,
        Running,
        Paused,
        Shutdown,
        Disposed,
    }
}
