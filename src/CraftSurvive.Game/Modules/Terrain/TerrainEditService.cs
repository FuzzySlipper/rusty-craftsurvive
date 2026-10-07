using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Where each part of the last accepted edit's time went, as raw timestamps. It is recorded only
/// while edit timing is switched on for diagnostics, and its text is built only when read.
/// </summary>
internal readonly record struct TerrainEditTiming(
    int Cells, long Admit, long Scene, long Read, long Projection, long Apply, long Applied,
    long Residency, long Chunks, long Present, long Ui, long Done)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"cells={Cells} admit={Ms(Admit, Scene):F1} scene={Ms(Scene, Read):F1} read={Ms(Read, Projection):F1} projection={Ms(Projection, Apply):F1} apply={Ms(Apply, Applied):F1} ")
        + string.Create(CultureInfo.InvariantCulture,
        $"overlay={Ms(Applied, Residency):F1} residency={Ms(Residency, Chunks):F1} chunks={Ms(Chunks, Present):F1} present={Ms(Present, Ui):F1} ui={Ms(Ui, Done):F1} total={Ms(Admit, Done):F1}");

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}

internal enum TerrainPickOutcome
{
    Picked,
    CastMiss,
    PickMiss,
}

/// <summary>What the view is aimed at: the block, the face it is seen by, and the open cell on that face.</summary>
internal readonly record struct TerrainPick(TerrainPickOutcome Outcome, VoxelAddress Target, SpatialFace Face, VoxelAddress Adjacent)
{
    internal static TerrainPick Missed(TerrainPickOutcome outcome) => new(outcome, default, default, default);
}

/// <summary>
/// The world's one edit path. <see cref="TerrainEditTransaction"/> decides the order - admission,
/// then one Engine transaction, then the overlay - and this service supplies the Engine step and
/// refreshes the resident chunks, presentation and UI after a recorded edit. Both the view-aimed
/// brush and a caller-decided volume come through here, so receipt semantics live in one place.
/// </summary>
internal sealed class TerrainEditService(
    IEngineContext engine,
    TerrainOverlayStore overlayStore,
    TerrainResidencyPolicy residencyPolicy,
    TerrainResidencyStreamer streamer,
    TerrainPresentation presentation,
    Action publishUi,
    Action<IReadOnlyList<TerrainVoxelEdit>> committed,
    Func<IReadOnlyList<TerrainVoxelEdit>, IReadOnlyList<TerrainVoxelEdit>>? dependents = null)
{
    private TerrainEditTiming? lastTiming;

    /// <summary>Whether edits record where their time went. Off unless diagnostics ask for it.</summary>
    internal bool TimingEnabled { get; set; }

    /// <summary>The last timed edit's cost by part; diagnostics, not game state.</summary>
    internal string LastTiming => TimingEnabled ? lastTiming?.ToString() ?? "none" : "off";

    internal TerrainWorldEditResult Apply(SpatialSession session, TerrainEditRequest request, VoxelAddress target,
        VoxelAddress center, SpatialFace face, Func<VoxelAddress, bool>? playerOverlaps, long step)
    {
        bool timed = TimingEnabled;
        long Mark() => timed ? Stopwatch.GetTimestamp() : 0;
        long admit = Mark(), scene = 0, read = 0, projection = 0, apply = 0, applied = 0;
        VoxelSceneReadout? sceneBefore = null;
        VoxelReadout? targetBefore = null;
        SpatialProjectionReadout? spatialBefore = null;
        VoxelEditReceipt? receipt = null;
        TerrainOverlayState overlay = overlayStore.Overlay;
        TerrainEditTransactionOutcome outcome = TerrainEditTransaction.Run(request, playerOverlaps, overlay, accepted =>
        {
            scene = Mark();
            sceneBefore = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
            read = Mark();
            targetBefore = engine.Voxel.Read(new VoxelReadRequest(session, ToEngine(center)));
            projection = Mark();
            spatialBefore = engine.Spatial.ReadProjection(new SpatialProjectionReadRequest(session));
            apply = Mark();
            VoxelEditReceipt applyReceipt = engine.Voxel.ApplyEdits(new VoxelEditTransaction(
                session,
                accepted.Edits.Select(ToEngineEdit).ToArray()));
            applied = Mark();
            receipt = applyReceipt;
            return applyReceipt.Status switch
            {
                VoxelEditStatus.Accepted => true,
                VoxelEditStatus.NoChanges => false,
                _ => throw new InvalidOperationException($"Engine returned unsupported voxel edit status '{applyReceipt.Status}'."),
            };
        }, committed, dependents);

        switch (outcome)
        {
            case TerrainEditRefused refused:
                return new TerrainWorldEditRejected(center, refused.Rejection);

            case TerrainEditUnchanged:
                return new TerrainWorldEditNoChanges(request.Kind, target, face, center,
                    targetBefore!.Value, spatialBefore!.Value, sceneBefore!.Value, receipt!.Value);

            case TerrainEditRecorded recorded:
            {
                overlayStore.MarkChanged(step);
                long residency = Mark();
                residencyPolicy.RefreshAfterOverlayChange(overlay, recorded.Receipt);
                long chunks = Mark();
                streamer.Refresh(session, recorded.Receipt.AppliedEdits.Select(edit => edit.Address.Chunk));
                long present = Mark();
                VoxelScenePresentationReadout refreshed = presentation.Refresh();
                long ui = Mark();
                publishUi();
                if (timed)
                {
                    lastTiming = new TerrainEditTiming(recorded.Receipt.AppliedEdits.Count, admit, scene, read, projection, apply, applied,
                        residency, chunks, present, ui, Mark());
                }

                return new TerrainWorldEditApplied(center, receipt!.Value, refreshed);
            }

            default:
                throw new InvalidOperationException($"Unknown edit outcome {outcome}.");
        }
    }

    /// <summary>Casts along the view, picks the voxel it meets, and edits there or on its open face.</summary>
    internal TerrainWorldEditResult ApplyFromView(SpatialSession session, Vector3 origin, Vector3 direction,
        TerrainEditKind kind, ushort material, int radius, Func<VoxelAddress, bool>? playerOverlaps, long step)
    {
        TerrainPick pick = Pick(session, origin, direction);
        if (pick.Outcome != TerrainPickOutcome.Picked)
        {
            return pick.Outcome == TerrainPickOutcome.CastMiss ? TerrainWorldEditResult.CastMiss : TerrainWorldEditResult.PickMiss;
        }

        VoxelAddress center = kind == TerrainEditKind.Set ? pick.Adjacent : pick.Target;
        TerrainEditRequest request = kind == TerrainEditKind.Set
            ? TerrainEditRequest.Set(center, material, radius)
            : TerrainEditRequest.Clear(center, radius);
        return Apply(session, request, pick.Target, center, pick.Face, playerOverlaps, step);
    }

    /// <summary>The voxel the view meets within reach, the face it meets it on, and the open cell in front of that face.</summary>
    internal TerrainPick Pick(SpatialSession session, Vector3 origin, Vector3 direction)
    {
        SpatialHit cast = engine.Spatial.CastRay(new SpatialRaycastRequest(
            session,
            origin,
            direction,
            TerrainConstants.EditReach,
            new SpatialQueryFilter(TerrainConstants.CollisionGroupAll, TerrainConstants.CollisionMaskAll),
            ReadOnlyMemory<SpatialEntityCollider>.Empty,
            ReadOnlyMemory<ulong>.Empty,
            ReadOnlyMemory<SpatialEntityCollider>.Empty));
        if (!cast.Present || cast.Kind != SpatialHitKind.Voxel)
        {
            return TerrainPick.Missed(TerrainPickOutcome.CastMiss);
        }

        SpatialHit picked = engine.Spatial.PickVoxel(new SpatialPickRequest(
            session, origin, direction, TerrainConstants.EditReach, cast.VoxelX, cast.VoxelY, cast.VoxelZ, cast.Face));
        if (!picked.Present || picked.Kind != SpatialHitKind.Voxel)
        {
            return TerrainPick.Missed(TerrainPickOutcome.PickMiss);
        }

        VoxelAddress target = new(picked.VoxelX, picked.VoxelY, picked.VoxelZ);
        return new TerrainPick(TerrainPickOutcome.Picked, target, picked.Face, Adjacent(target, picked.Face));
    }

    private static VoxelEdit ToEngineEdit(TerrainVoxelEdit edit) => edit.Material == TerrainConstants.EmptyMaterial
        ? new VoxelEdit(VoxelEditKind.Clear, ToEngine(edit.Address), 0)
        : new VoxelEdit(VoxelEditKind.Set, ToEngine(edit.Address), edit.Material);

    private static VoxelAddress Adjacent(VoxelAddress target, SpatialFace face) => face switch
    {
        SpatialFace.PosX => target with { X = target.X + 1 },
        SpatialFace.NegX => target with { X = target.X - 1 },
        SpatialFace.PosY => target with { Y = target.Y + 1 },
        SpatialFace.NegY => target with { Y = target.Y - 1 },
        SpatialFace.PosZ => target with { Z = target.Z + 1 },
        SpatialFace.NegZ => target with { Z = target.Z - 1 },
        _ => throw new InvalidOperationException("Engine voxel pick did not include a placement face."),
    };

    private static EngineVoxelAddress ToEngine(VoxelAddress address) => new(address.X, address.Y, address.Z);
}
