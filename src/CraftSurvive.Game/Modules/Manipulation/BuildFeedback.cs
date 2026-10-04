using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>Player-facing feedback stays separate from the detailed edit diagnostic.</summary>
internal readonly record struct BuildFeedback(bool Accepted, string Message)
{
    internal static BuildFeedback Refused(string message) => new(false, message);

    internal static BuildFeedback FromEdit(TerrainWorldEditResult result, string success) => result switch
    {
        TerrainWorldEditApplied or TerrainWorldEditNoChanges => new(true, success),
        TerrainWorldEditRejected { Rejection.Reason: TerrainEditRejectionReason.PlayerOverlap } => Refused("No room: you are standing there."),
        TerrainWorldEditRejected { Rejection.Reason: TerrainEditRejectionReason.WorldBounds } => Refused("That is outside the world."),
        TerrainWorldEditRejected { Rejection.Reason: TerrainEditRejectionReason.OverlayFull } => Refused("This world cannot hold any more changes."),
        TerrainWorldEditCastMiss or TerrainWorldEditPickMiss => Refused("Nothing within reach."),
        _ => Refused("Cannot build there."),
    };
}
