using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// Moves the session's world origin under the player once they wander far enough from it that
/// local coordinates would lose precision. The player is the one root that moves with the origin;
/// everything else holds world coordinates and hears about the move through the
/// <see cref="WorldFrame"/>.
/// </summary>
internal sealed class WorldOriginRebaser(IEngineContext engine, WorldFrame frame)
{
    /// <summary>Whether the player's local position is far enough out to rebase.</summary>
    internal static bool IsNeeded(Vector3 local) =>
        MathF.Abs(local.X) >= PlayerConstants.RebaseThreshold || MathF.Abs(local.Z) >= PlayerConstants.RebaseThreshold;

    /// <summary>
    /// Rebases to the player's cell and returns the player's new local position. The origin keeps
    /// its height, so only the horizontal frame moves.
    /// </summary>
    internal Vector3 Rebase(SpatialSession session, PlayerWorldPosition world, Vector3 local)
    {
        WorldOriginReadout origin = engine.WorldOrigin.Read(new WorldOriginReadRequest(session));
        WorldOriginEntityRow[] roots =
        [
            new WorldOriginEntityRow(PlayerConstants.PlayerEntityId, new Transform(local, Quaternion.Identity, Vector3.One), world.ToEngine()),
        ];
        using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            session,
            world.CellX,
            origin.CellY,
            world.CellZ,
            roots));

        // Prepare returns one affected transform per root, in request order.
        Vector3 rebased = engine.WorldOrigin.ReadPrepared(new WorldOriginPreparedReadRequest(prepared))
            .Affected.Span[0].LocalTransform.Translation;
        engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        frame.Commit(world.CellX, origin.CellY, world.CellZ);
        return rebased;
    }
}
