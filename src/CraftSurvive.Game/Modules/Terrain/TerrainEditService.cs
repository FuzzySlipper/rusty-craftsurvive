using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Rusty.Engine;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Where each part of the last accepted edit's time went, as raw timestamps. Recording costs a
/// handful of counter reads; the text is built only when a readout asks for it.
/// </summary>
internal readonly record struct TerrainEditTiming(
    int Cells, long Admit, long Scene, long Read, long Projection, long Apply, long Applied,
    long Overlay, long Residency, long Chunks, long Present, long Ui, long Done)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"cells={Cells} admit={Ms(Admit, Scene):F1} scene={Ms(Scene, Read):F1} read={Ms(Read, Projection):F1} projection={Ms(Projection, Apply):F1} apply={Ms(Apply, Applied):F1} ")
        + string.Create(CultureInfo.InvariantCulture,
        $"overlay={Ms(Overlay, Residency):F1} residency={Ms(Residency, Chunks):F1} chunks={Ms(Chunks, Present):F1} present={Ms(Present, Ui):F1} ui={Ms(Ui, Done):F1} total={Ms(Admit, Done):F1}");

    private static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// The world's one edit path. A request is admitted - bounds, the player's body and the overlay's
/// capacity, all before the Engine is asked - then applied as one Engine transaction, recorded in
/// the overlay, and the resident chunks, presentation and UI refreshed. Both the view-aimed brush
/// and a caller-decided volume come through here, so receipt semantics live in one place.
/// </summary>
internal sealed class TerrainEditService(
    IEngineContext engine,
    TerrainOverlayStore overlayStore,
    TerrainResidencyPolicy residencyPolicy,
    TerrainResidencyStreamer streamer,
    TerrainPresentation presentation,
    Action publishUi)
{
    private TerrainEditTiming? lastTiming;

    /// <summary>The last accepted edit's cost by part; diagnostics, not game state.</summary>
    internal string LastTiming => lastTiming?.ToString() ?? "none";

    internal TerrainWorldEditResult Apply(SpatialSession session, TerrainEditRequest request, VoxelAddress target,
        VoxelAddress center, SpatialFace face, Func<VoxelAddress, bool>? playerOverlaps, long step)
    {
        long admit = Stopwatch.GetTimestamp();
        TerrainOverlayState overlay = overlayStore.Overlay;
        TerrainEditAdmissionResult admission = TerrainEditAdmission.Admit(request, playerOverlaps, overlay);
        if (admission is TerrainEditRejected rejected)
        {
            return new TerrainWorldEditRejected(center, rejected);
        }

        TerrainEditAccepted accepted = (TerrainEditAccepted)admission;
        long scene = Stopwatch.GetTimestamp();
        VoxelSceneReadout sceneBefore = engine.Voxel.ReadScene(new VoxelSceneReadRequest(session));
        long read = Stopwatch.GetTimestamp();
        VoxelReadout targetBefore = engine.Voxel.Read(new VoxelReadRequest(session, ToEngine(center)));
        long projection = Stopwatch.GetTimestamp();
        SpatialProjectionReadout spatialBefore = engine.Spatial.ReadProjection(new SpatialProjectionReadRequest(session));
        long apply = Stopwatch.GetTimestamp();
        VoxelEditReceipt receipt = engine.Voxel.ApplyEdits(new VoxelEditTransaction(
            session,
            accepted.Edits.Select(ToEngineEdit).ToArray()));
        long applied = Stopwatch.GetTimestamp();
        switch (receipt.Status)
        {
            case VoxelEditStatus.NoChanges:
                return new TerrainWorldEditNoChanges(request.Kind, target, face, center, targetBefore, spatialBefore, sceneBefore, receipt);

            case VoxelEditStatus.Accepted:
            {
                long overlayStart = Stopwatch.GetTimestamp();
                TerrainOverlayReceipt overlayReceipt = overlay.Apply(accepted);
                overlayStore.MarkChanged(step);
                long residency = Stopwatch.GetTimestamp();
                residencyPolicy.RefreshAfterOverlayChange(overlay, overlayReceipt);
                long chunks = Stopwatch.GetTimestamp();
                streamer.Refresh(session, overlayReceipt.AppliedEdits.Select(edit => edit.Address.Chunk));
                long present = Stopwatch.GetTimestamp();
                VoxelScenePresentationReadout refreshed = presentation.Refresh();
                long ui = Stopwatch.GetTimestamp();
                publishUi();
                lastTiming = new TerrainEditTiming(accepted.Edits.Count, admit, scene, read, projection, apply, applied,
                    overlayStart, residency, chunks, present, ui, Stopwatch.GetTimestamp());
                return new TerrainWorldEditApplied(center, receipt, refreshed);
            }

            default:
                throw new InvalidOperationException($"Engine returned unsupported voxel edit status '{receipt.Status}'.");
        }
    }

    /// <summary>Casts along the view, picks the voxel it meets, and edits there or on its open face.</summary>
    internal TerrainWorldEditResult ApplyFromView(SpatialSession session, Vector3 origin, Vector3 direction,
        TerrainEditKind kind, ushort material, int radius, Func<VoxelAddress, bool>? playerOverlaps, long step)
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
            return TerrainWorldEditResult.CastMiss;
        }

        SpatialHit picked = engine.Spatial.PickVoxel(new SpatialPickRequest(
            session, origin, direction, TerrainConstants.EditReach, cast.VoxelX, cast.VoxelY, cast.VoxelZ, cast.Face));
        if (!picked.Present || picked.Kind != SpatialHitKind.Voxel)
        {
            return TerrainWorldEditResult.PickMiss;
        }

        VoxelAddress target = new(picked.VoxelX, picked.VoxelY, picked.VoxelZ);
        VoxelAddress center = kind == TerrainEditKind.Set ? Adjacent(target, picked.Face) : target;
        TerrainEditRequest request = kind == TerrainEditKind.Set
            ? TerrainEditRequest.Set(center, material, radius)
            : TerrainEditRequest.Clear(center, radius);
        return Apply(session, request, target, center, picked.Face, playerOverlaps, step);
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
