namespace CraftSurvive.Game.Modules.Terrain;

internal enum TerrainEditKind
{
    Clear,
    Set,
}

internal sealed record TerrainEditRequest(VoxelAddress Center, TerrainEditKind Kind, ushort Material, int Radius)
{
    internal static TerrainEditRequest Clear(VoxelAddress center, int radius) =>
        new(center, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, radius);

    internal static TerrainEditRequest Set(VoxelAddress center, ushort material, int radius) =>
        new(center, TerrainEditKind.Set, material, radius);
}

internal readonly record struct TerrainVoxelEdit(VoxelAddress Address, ushort Material);

internal static class TerrainBrushPolicy
{
    internal static TerrainVoxelEdit[] Expand(TerrainEditRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
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
