namespace CraftSurvive.Game.Modules.Terrain;

internal enum TerrainEditKind
{
    Clear,
    Set,
}

internal sealed record TerrainEditRequest(VoxelAddress Center, TerrainEditKind Kind, ushort Material, int Radius,
    IReadOnlyList<VoxelAddress>? Cells = null)
{
    internal static TerrainEditRequest Clear(VoxelAddress center, int radius) =>
        new(center, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, radius);

    internal static TerrainEditRequest Set(VoxelAddress center, ushort material, int radius) =>
        new(center, TerrainEditKind.Set, material, radius);

    /// <summary>
    /// An edit whose cells the caller has already decided on - the volume of a charge, the shape
    /// of a stamp, or the sphere a proof wants to undo exactly.
    ///
    /// A crosshair and a radius cannot express any of those: the brush is aimed, and re-aiming at
    /// a volume you have just filled picks a different centre, which is how an "undo" leaves a
    /// rim. Here the cells are the request.
    /// </summary>
    internal static TerrainEditRequest FromCells(IReadOnlyList<VoxelAddress> cells, TerrainEditKind kind,
        ushort material) =>
        new(cells.Count == 0 ? default : cells[0], kind, material, 0, cells);
}

internal readonly record struct TerrainVoxelEdit(VoxelAddress Address, ushort Material);

internal static class TerrainBrushPolicy
{
    /// <summary>
    /// The most cells one transaction may carry. A blast is bounded on purpose: the budget the
    /// manipulation slice publishes is a promise about how long an update can be held, and an
    /// unbounded transaction is how that promise is broken.
    /// </summary>
    internal const int MaximumTransactionCells = 4096;

    internal static TerrainVoxelEdit[] Expand(TerrainEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Cells is IReadOnlyList<VoxelAddress> decided)
        {
            if (decided.Count == 0)
            {
                throw new ArgumentException("A decided edit needs at least one cell.", nameof(request));
            }

            if (decided.Count > MaximumTransactionCells)
            {
                throw new ArgumentOutOfRangeException(nameof(request),
                    $"A decided edit may carry at most {MaximumTransactionCells} cells, not {decided.Count}.");
            }

            if (request.Kind == TerrainEditKind.Set
                && (request.Material == TerrainConstants.EmptyMaterial || request.Material > TerrainConstants.MaximumMaterial))
            {
                throw new ArgumentOutOfRangeException(nameof(request),
                    $"Placed terrain material must be within 1..={TerrainConstants.MaximumMaterial}.");
            }

            List<TerrainVoxelEdit> decidedEdits = new(decided.Count);
            foreach (VoxelAddress address in decided)
            {
                decidedEdits.Add(new TerrainVoxelEdit(address,
                    request.Kind == TerrainEditKind.Clear ? TerrainConstants.EmptyMaterial : request.Material));
            }

            return decidedEdits.ToArray();
        }

        if (request.Radius < 0 || request.Radius > TerrainConstants.MaximumBrushRadius)
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"Terrain brush radius must be within 0..={TerrainConstants.MaximumBrushRadius}.");
        }

        if (request.Kind == TerrainEditKind.Set
            && (request.Material == TerrainConstants.EmptyMaterial || request.Material > TerrainConstants.MaximumMaterial))
        {
            throw new ArgumentOutOfRangeException(nameof(request),
                $"Placed terrain material must be within 1..={TerrainConstants.MaximumMaterial}.");
        }

        long radius = request.Radius;
        long radiusSquared = radius * radius;
        List<TerrainVoxelEdit> edits = [];
        for (long x = -radius; x <= radius; x++)
        {
            for (long y = -radius; y <= radius; y++)
            {
                for (long z = -radius; z <= radius; z++)
                {
                    if ((x * x) + (y * y) + (z * z) > radiusSquared)
                    {
                        continue;
                    }

                    VoxelAddress address = new(request.Center.X + x, request.Center.Y + y, request.Center.Z + z);
                    edits.Add(new TerrainVoxelEdit(address,
                        request.Kind == TerrainEditKind.Clear ? TerrainConstants.EmptyMaterial : request.Material));
                }
            }
        }

        return edits.ToArray();
    }
}

/// <summary>
/// Admits an all-or-nothing product edit batch before the later Engine adapter
/// validates and commits the matching spatial operation.
/// </summary>
internal static class TerrainEditAdmission
{
    internal static TerrainEditAdmissionResult Admit(TerrainEditRequest request,
        Func<VoxelAddress, bool>? playerOverlaps = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        TerrainVoxelEdit[] edits = TerrainBrushPolicy.Expand(request);
        foreach (TerrainVoxelEdit edit in edits)
        {
            if (!edit.Address.IsWithinWorldBounds)
            {
                return new TerrainEditRejected(TerrainEditRejectionReason.WorldBounds, edit.Address);
            }
        }

        if (request.Kind == TerrainEditKind.Set && playerOverlaps is not null)
        {
            foreach (TerrainVoxelEdit edit in edits)
            {
                if (playerOverlaps(edit.Address))
                {
                    return new TerrainEditRejected(TerrainEditRejectionReason.PlayerOverlap, edit.Address);
                }
            }
        }

        return new TerrainEditAccepted(edits);
    }
}

internal abstract record TerrainEditAdmissionResult;

internal sealed record TerrainEditAccepted(IReadOnlyList<TerrainVoxelEdit> Edits) : TerrainEditAdmissionResult;

internal sealed record TerrainEditRejected(TerrainEditRejectionReason Reason, VoxelAddress Address)
    : TerrainEditAdmissionResult;

internal enum TerrainEditRejectionReason
{
    WorldBounds,
    PlayerOverlap,
}
