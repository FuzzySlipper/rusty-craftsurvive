using CraftSurvive.Game.Modules.WorldGen;
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
    private TerrainChunkAddress[] cachedCandidates = [];
    private HashSet<TerrainChunkAddress> cachedWindow = [];
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
        // No payloads are produced here. Content is decided by the recipe's predicate, which
        // matches generation exactly and is asserted to, so the request and retained sets
        // need no chunks and the ring is never generated. Producing it cost 49 chunks, about
        // 70 ms, on the first update of a run - the largest part of the measured 113 ms
        // worst-case tick. Chunks are produced by the plan, for the addresses it is asked
        // about and only within this window.
        cachedCandidates = candidates.ToArray();
        cachedWindow = candidates;

        cachedOverlay = snapshot;
        cachedOverlayRevision = overlay.Revision;
        cachedPlan = BuildPlan(center, snapshot);
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
        cachedPlan = BuildPlan(cachedPlan.Center, cachedOverlay);
    }

    /// <summary>
    /// Whether a chunk holds content: one already produced answers from itself, and one that
    /// has not is answered by the recipe's predicate, which matches generation exactly.
    /// </summary>
    private bool HasContent(TerrainChunkAddress address)
    {
        if (cachedChunks.TryGetValue(address, out TerrainChunk? chunk))
        {
            return chunk.SolidVoxelCount > 0;
        }

        // The predicate answers for the generated world; an edit can put content into a chunk
        // generation leaves empty, and one that arrives without RefreshAfterOverlayChange
        // clears the payloads rather than refreshing them, so the overlay is consulted here
        // too. Without this an unreported edit is invisible until something happens to
        // request the chunk for another reason.
        return (cachedOverlay?.TouchesChunk(address) ?? false) || recipe.ChunkHasContent(address);
    }

    private TerrainResidencyPlan BuildPlan(TerrainChunkAddress center, TerrainOverlaySnapshot planSnapshot)
    {
        TerrainChunkAddress[] retained = cachedCandidates
            .Where(HasContent)
            .OrderBy(address => DistancePriority(address, center))
            .Take(TerrainConstants.MaximumResidentChunks)
            .ToArray();

        // Only what will be retained is requested: a requested chunk beyond the retained cap would
        // be admitted on one update and evicted on the next, forever.
        TerrainChunkAddress[] requested = retained
            .Where(address => IsWithinHorizontalRadius(address, center, TerrainConstants.RequestedChunkRadius))
            .ToArray();

        // A plan is a snapshot with a window. It serves the chunks that exist when it is
        // built, produces one on demand for an address inside its window, throws outside it,
        // and only publishes back to the policy while its revision is still current - so two
        // plans of the same state share instances, a stale plan serves its own payloads, and
        // nothing outside the window is ever produced.
        Dictionary<TerrainChunkAddress, TerrainChunk> planChunks = new(cachedChunks);
        HashSet<TerrainChunkAddress> window = cachedWindow;
        ulong planRevision = cachedOverlayRevision;
        bool IsCurrent() => cachedOverlayRevision == planRevision;
        return new TerrainResidencyPlan(center, requested, retained, TerrainConstants.MaximumResidencyOperationsPerTick,
            planChunks,
            window,
            address =>
            {
                if (IsCurrent() && cachedChunks.TryGetValue(address, out TerrainChunk? shared))
                {
                    return shared;
                }

                TerrainChunk produced = generator.Generate(address, planSnapshot);
                if (IsCurrent())
                {
                    cachedChunks[address] = produced;
                }

                return produced;
            });
    }

    private IEnumerable<TerrainChunkAddress> CandidateChunks(TerrainChunkAddress center, int radius)
    {
        long minimumY = WorldGen.GridMath.FloorDivide(recipe.MinimumMaterialY, TerrainConstants.ChunkEdgeLength);
        long maximumY = WorldGen.GridMath.FloorDivide(recipe.MaximumMaterialY, TerrainConstants.ChunkEdgeLength);
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

}

internal sealed class TerrainResidencyPlan
{
    internal TerrainResidencyPlan(TerrainChunkAddress center, TerrainChunkAddress[] requested,
        TerrainChunkAddress[] retained, int maximumOperationsPerTick, Dictionary<TerrainChunkAddress, TerrainChunk> chunks,
        HashSet<TerrainChunkAddress> window, Func<TerrainChunkAddress, TerrainChunk> chunkSource)
    {
        Center = center;
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        Retained = retained ?? throw new ArgumentNullException(nameof(retained));
        MaximumOperationsPerTick = maximumOperationsPerTick;
        this.chunks = chunks ?? throw new ArgumentNullException(nameof(chunks));
        this.window = window ?? throw new ArgumentNullException(nameof(window));
        this.chunkSource = chunkSource ?? throw new ArgumentNullException(nameof(chunkSource));
    }

    internal TerrainChunkAddress Center { get; }

    internal IReadOnlyList<TerrainChunkAddress> Requested { get; }

    internal IReadOnlyList<TerrainChunkAddress> Retained { get; }

    internal int MaximumOperationsPerTick { get; }

    private readonly Dictionary<TerrainChunkAddress, TerrainChunk> chunks;
    private readonly HashSet<TerrainChunkAddress> window;
    private readonly Func<TerrainChunkAddress, TerrainChunk> chunkSource;

    /// <summary>
    /// The chunk at an address. An address outside the plan's window is refused rather than
    /// produced: the window is part of what the plan promises, and answering for anything
    /// else would quietly turn a residency plan into a generator.
    /// </summary>
    internal TerrainChunk Chunk(TerrainChunkAddress address)
    {
        if (chunks.TryGetValue(address, out TerrainChunk? chunk))
        {
            return chunk;
        }

        if (!window.Contains(address))
        {
            throw new KeyNotFoundException($"Chunk {address} is outside this plan's window.");
        }

        chunk = chunkSource(address);
        chunks[address] = chunk;
        return chunk;
    }
}
