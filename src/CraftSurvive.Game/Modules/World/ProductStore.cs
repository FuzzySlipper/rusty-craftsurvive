using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The product's one persistence store, in the product's own scope. Each owner of saved state -
/// the terrain overlay, the discovery journal - reads and writes its own key through it, so the
/// scope is opened once and closed once.
/// </summary>
internal sealed class ProductStore : IDisposable
{
    internal const string Scope = "craftsurvive";

    private readonly IEngineContext engine;
    private PersistenceStore? store;

    internal ProductStore(IEngineContext engine) => this.engine = engine ?? throw new ArgumentNullException(nameof(engine));

    internal PersistenceStore Store => store ??= engine.Persistence.OpenStore(new PersistenceOpenRequest(Scope));

    public void Dispose()
    {
        store?.Dispose();
        store = null;
    }
}
