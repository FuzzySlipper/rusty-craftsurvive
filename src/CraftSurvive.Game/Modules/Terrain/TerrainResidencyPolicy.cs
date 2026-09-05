namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product policy for which deterministic chunks should be requested or kept.
/// It creates no Engine residency operation; a later adapter owns that bridge.
/// </summary>
internal sealed class TerrainResidencyPolicy
{
    private readonly TerrainRecipe recipe;
    private readonly TerrainChunkGenerator generator;
    private readonly Dictionary<TerrainChunkAddress, TerrainChunk> cachedChunks = [];
    private TerrainResidencyPlan? cachedPlan;
    private TerrainOverlaySnapshot? cachedOverlay;
    private ulong cachedOverlayRevision;

    internal TerrainResidencyPolicy(TerrainRecipe recipe, TerrainChunkGenerator generator)
    {
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        this.generator = generator ?? throw new ArgumentNullException(nameof(generator));
    }

    internal TerrainResidencyPlan PlanFor(TerrainChunkAddress center, TerrainOverlayState overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (cachedPlan is not null && cachedPlan.Center == center && cachedOverlayRevision == overlay.Revision)
        {
            return cachedPlan;
        }

        bool overlayChanged = cachedOverlay is null || cachedOverlayRevision != overlay.Revision;
        TerrainOverlaySnapshot snapshot = overlayChanged ? overlay.Snapshot() : cachedOverlay!;
        // A restore or an edit not reported through RefreshAfterOverlayChange
        // invalidates the payloads. Ordinary boundary crossings reuse overlap.
        if (overlayChanged) cachedChunks.Clear();
        HashSet<TerrainChunkAddress> candidates = CandidateChunks(center, TerrainConstants.RetainedChunkRadius).ToHashSet();
        foreach (TerrainChunkAddress address in cachedChunks.Keys.Where(address => !candidates.Contains(address)).ToArray())
            cachedChunks.Remove(address);
        foreach (TerrainChunkAddress address in candidates)
            if (!cachedChunks.ContainsKey(address))
                cachedChunks.Add(address, generator.Generate(address, snapshot));

        cachedOverlay = snapshot;
        cachedOverlayRevision = overlay.Revision;
        cachedPlan = BuildPlan(center);
        return cachedPlan;
    }

    /// <summary>Refreshes only edited candidate chunks in the current global plan.</summary>
    internal void RefreshAfterOverlayChange(TerrainOverlayState overlay, TerrainOverlayReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(receipt.AppliedEdits);
        if (cachedPlan is null)
        {
            return;
        }

        TerrainOverlaySnapshot snapshot = overlay.Snapshot();
        foreach (TerrainChunkAddress address in receipt.AppliedEdits.Select(edit => edit.Address.Chunk).Distinct())
        {
            if (!cachedChunks.ContainsKey(address))
            {
                continue;
            }

            cachedChunks[address] = generator.Generate(address, snapshot);
        }

        cachedOverlay = snapshot;
        cachedOverlayRevision = overlay.Revision;
        cachedPlan = BuildPlan(cachedPlan.Center);
    }

    private TerrainResidencyPlan BuildPlan(TerrainChunkAddress center)
    {
        TerrainChunkAddress[] requested = cachedChunks
            .Where(pair => IsWithinHorizontalRadius(pair.Key, center, TerrainConstants.RequestedChunkRadius) && pair.Value.SolidVoxelCount > 0)
            .Select(static pair => pair.Key)
            .OrderBy(address => DistancePriority(address, center))
            .ToArray();
        TerrainChunkAddress[] retained = cachedChunks
            .Where(static pair => pair.Value.SolidVoxelCount > 0)
            .Select(static pair => pair.Key)
            .OrderBy(address => DistancePriority(address, center))
            .Take(TerrainConstants.MaximumResidentChunks)
            .ToArray();
        return new TerrainResidencyPlan(center, requested, retained, TerrainConstants.MaximumResidencyOperationsPerTick,
            new Dictionary<TerrainChunkAddress, TerrainChunk>(cachedChunks));
    }

    private IEnumerable<TerrainChunkAddress> CandidateChunks(TerrainChunkAddress center, int radius)
    {
        long minimumY = FloorDivide(recipe.MinimumMaterialY, TerrainConstants.ChunkEdgeLength);
        long maximumY = FloorDivide(recipe.MaximumMaterialY, TerrainConstants.ChunkEdgeLength);
        for (long x = center.X - radius; x <= center.X + radius; x++)
        {
            for (long z = center.Z - radius; z <= center.Z + radius; z++)
            {
                for (long y = minimumY; y <= maximumY; y++)
                {
                    yield return new TerrainChunkAddress(x, y, z);
                }
            }
        }
    }

    private static bool IsWithinHorizontalRadius(TerrainChunkAddress address, TerrainChunkAddress center, int radius) =>
        address.X >= center.X - radius && address.X <= center.X + radius
        && address.Z >= center.Z - radius && address.Z <= center.Z + radius;

    private static (long HorizontalDistance, long Y, TerrainChunkAddress Address) DistancePriority(
        TerrainChunkAddress address, TerrainChunkAddress center)
    {
        long x = address.X - center.X;
        long z = address.Z - center.Z;
        return ((x * x) + (z * z), address.Y, address);
    }

    private static long FloorDivide(long value, int divisor)
    {
        long quotient = value / divisor;
        return value % divisor < 0 ? quotient - 1 : quotient;
    }
}

internal sealed class TerrainResidencyPlan
{
    internal TerrainResidencyPlan(TerrainChunkAddress center, TerrainChunkAddress[] requested,
        TerrainChunkAddress[] retained, int maximumOperationsPerTick, IReadOnlyDictionary<TerrainChunkAddress, TerrainChunk> chunks)
    {
        Center = center;
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        Retained = retained ?? throw new ArgumentNullException(nameof(retained));
        MaximumOperationsPerTick = maximumOperationsPerTick;
        this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
    }

    internal TerrainChunkAddress Center { get; }

    internal IReadOnlyList<TerrainChunkAddress> Requested { get; }

    internal IReadOnlyList<TerrainChunkAddress> Retained { get; }

    internal int MaximumOperationsPerTick { get; }

    private readonly IReadOnlyDictionary<TerrainChunkAddress, TerrainChunk> chunks;

    internal TerrainChunk Chunk(TerrainChunkAddress address) => chunks[address];
}
