using Rusty.Engine;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game;

/// <summary>
/// The intentionally small NativeAOT product root. Gameplay domains join here
/// as they are migrated from the retained Rust/TypeScript donor implementation.
/// </summary>
public sealed class CraftSurviveProduct : IEngineProduct
{
    private ProductLifecycleState lifecycle = ProductLifecycleState.Created;
    private readonly TerrainWorld terrain;

    public CraftSurviveProduct(ProductCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        terrain = new TerrainWorld(context.Engine, TerrainConfiguration.Default);
    }

    public void Start()
    {
        RequireState(ProductLifecycleState.Created, nameof(Start));
        terrain.Start();
        lifecycle = ProductLifecycleState.Running;
    }

    public ProductTurnRequest Update(ProductUpdate update)
    {
        RequireState(ProductLifecycleState.Running, nameof(Update));
        _ = update;
        terrain.Update();
        return ProductTurnRequest.None;
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

    private enum ProductLifecycleState
    {
        Created,
        Running,
        Paused,
        Shutdown,
        Disposed,
    }
}
