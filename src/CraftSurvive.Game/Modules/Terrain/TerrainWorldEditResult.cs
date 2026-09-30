using System.Globalization;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

internal abstract record TerrainWorldEditResult
{
    internal static TerrainWorldEditResult CastMiss { get; } = new TerrainWorldEditCastMiss();

    internal static TerrainWorldEditResult PickMiss { get; } = new TerrainWorldEditPickMiss();

    /// <summary>Formats a player-consumed edit result for the narrow live-debug surface.</summary>
    internal static string Format(TerrainWorldEditResult? result) => result switch
    {
        null => "outcome=none",
        TerrainWorldEditCastMiss => "outcome=cast-miss",
        TerrainWorldEditPickMiss => "outcome=pick-miss",
        TerrainWorldEditRejected rejected => string.Create(CultureInfo.InvariantCulture,
            $"outcome=rejected;target={Voxel(rejected.Target)};reason={rejected.Rejection.Reason};rejected={Voxel(rejected.Rejection.Address)}"),
        TerrainWorldEditNoChanges noChanges => string.Create(CultureInfo.InvariantCulture,
            $"outcome=no-changes;kind={noChanges.Kind};picked={Voxel(noChanges.Picked)};face={noChanges.Face};target={Voxel(noChanges.Target)};beforePresent={noChanges.TargetBefore.Present};beforeMaterial={noChanges.TargetBefore.MaterialSlot};sceneRevision={noChanges.SceneBefore.SourceRevision};spatialRevision={noChanges.SpatialBefore.SourceRevision};authorityMatch={noChanges.SceneBefore.AuthorityHash == noChanges.SpatialBefore.AuthorityHash};currentRevision={noChanges.Receipt.AcceptedRevision}"),
        TerrainWorldEditApplied applied => string.Create(CultureInfo.InvariantCulture,
            $"outcome=accepted;target={Voxel(applied.Target)};changed={applied.Receipt.ChangedVoxels};sceneRevision={applied.Receipt.AcceptedRevision};meshRevision={applied.Receipt.MeshRevision};presentationSourceRevision={applied.Presentation.SourceRevision};presentationMeshRevision={applied.Presentation.MeshRevision}"),
        _ => throw new InvalidOperationException($"Unsupported terrain edit result '{result.GetType().Name}'."),
    };

    private static string Voxel(VoxelAddress address) => string.Create(CultureInfo.InvariantCulture,
        $"{address.X},{address.Y},{address.Z}");
}

internal sealed record TerrainWorldEditCastMiss : TerrainWorldEditResult;

internal sealed record TerrainWorldEditPickMiss : TerrainWorldEditResult;

internal sealed record TerrainWorldEditRejected(VoxelAddress Target, TerrainEditRejected Rejection) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditNoChanges(
    TerrainEditKind Kind,
    VoxelAddress Picked,
    SpatialFace Face,
    VoxelAddress Target,
    VoxelReadout TargetBefore,
    SpatialProjectionReadout SpatialBefore,
    VoxelSceneReadout SceneBefore,
    VoxelEditReceipt Receipt) : TerrainWorldEditResult;

internal sealed record TerrainWorldEditApplied(
    VoxelAddress Target,
    VoxelEditReceipt Receipt,
    VoxelScenePresentationReadout Presentation) : TerrainWorldEditResult;
