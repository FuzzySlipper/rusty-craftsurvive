namespace CraftSurvive.Game.Modules.World;

/// <summary>Raised by owner-dependent diagnostics while a fresh store's first world is still being simulated.</summary>
internal sealed class WorldPendingException() : InvalidOperationException(WorldOwners.PendingMessage);

/// <summary>
/// The one gate between diagnostics and the world's owners. Before the first world is admitted
/// there are no terrain, player or gameplay owners; a command reaching for one gets a stable,
/// named answer instead of a null dereference.
/// </summary>
internal static class WorldOwners
{
    internal const string PendingMessage = "world=generating: the first world is still being generated; try again once it is admitted";

    internal static T Require<T>(bool built, T? owner) where T : class =>
        built && owner is not null ? owner : throw new WorldPendingException();
}
