using CraftSurvive.Game.Modules.WorldGen;
namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Product policy for which deterministic chunks should be requested or kept. It creates no
/// Engine residency operation; <see cref="TerrainResidencyStreamer"/> turns its plan into them.
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

    /// <summary>Where the last post-edit refresh spent its time, in milliseconds (#9578).</summary>
    internal readonly record struct RefreshTiming(double Snapshot, double Regenerate, double Candidates, double Plan)
    {
        public override string ToString() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"snapshot={Snapshot:F2} regenerate={Regenerate:F2} candidates={Candidates:F2} plan={Plan:F2}");
    }

    internal RefreshTiming LastRefresh { get; private set; }

    private static double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

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
        HashSet<TerrainChunkAddress> candidates = CandidateChunks(center, TerrainConstants.RetainedChunkRadius, snapshot).ToHashSet();
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

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        TerrainOverlaySnapshot snapshot = overlay.Snapshot();
        long snapshotted = System.Diagnostics.Stopwatch.GetTimestamp();
        // An edited chunk already produced is patched voxel by voxel rather than generated again.
        foreach (IGrouping<TerrainChunkAddress, VoxelAddress> chunk in receipt.AppliedEdits.Select(edit => edit.Address).GroupBy(voxel => voxel.Chunk))
        {
            if (!cachedChunks.TryGetValue(chunk.Key, out TerrainChunk? previous))
            {
                continue;
            }

            cachedChunks[chunk.Key] = generator.Patch(previous, chunk, snapshot);
        }

        long regenerated = System.Diagnostics.Stopwatch.GetTimestamp();
        cachedOverlay = snapshot;
        cachedOverlayRevision = overlay.Revision;
        // An edit can occupy a chunk outside every surface band, which makes it a new candidate.
        HashSet<TerrainChunkAddress> candidates = CandidateChunks(cachedPlan.Center, TerrainConstants.RetainedChunkRadius, snapshot).ToHashSet();
        cachedCandidates = candidates.ToArray();
        cachedWindow = candidates;
        long candidated = System.Diagnostics.Stopwatch.GetTimestamp();
        cachedPlan = BuildPlan(cachedPlan.Center, cachedOverlay);
        long planned = System.Diagnostics.Stopwatch.GetTimestamp();
        LastRefresh = new(Ms(started, snapshotted), Ms(snapshotted, regenerated), Ms(regenerated, candidated), Ms(candidated, planned));
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

    /// <summary>
    /// Candidates are each column's band around its own generated surface, the player's own
    /// storey everywhere in the ring, and every edited chunk in the ring. Mountains are hundreds
    /// of metres tall; buried rock with no surface must not crowd visible ground out of the cap.
    /// </summary>
    private IEnumerable<TerrainChunkAddress> CandidateChunks(TerrainChunkAddress center, int radius, TerrainOverlaySnapshot snapshot)
    {
        long minimumY = WorldGen.GridMath.FloorDivide(recipe.MinimumMaterialY, TerrainConstants.ChunkEdgeLength);
        long maximumY = WorldGen.GridMath.FloorDivide(recipe.MaximumMaterialY, TerrainConstants.ChunkEdgeLength);
        for (long x = center.X - radius; x <= center.X + radius; x++)
        {
            for (long z = center.Z - radius; z <= center.Z + radius; z++)
            {
                (long bandMinimum, long bandMaximum) = recipe.ChunkColumnBand(x, z);
                long lower = Math.Max(minimumY, Math.Min(bandMinimum, center.Y - TerrainConstants.PlayerStoreyChunks));
                long upper = Math.Min(maximumY, Math.Max(bandMaximum, center.Y + TerrainConstants.PlayerStoreyChunks));
                for (long y = lower; y <= upper; y++)
                {
                    if (y >= bandMinimum && y <= bandMaximum || Math.Abs(y - center.Y) <= TerrainConstants.PlayerStoreyChunks)
                    {
                        yield return new TerrainChunkAddress(x, y, z);
                    }
                }
            }
        }

        foreach (TerrainChunkAddress edited in snapshot.TouchedChunks())
        {
            if (IsWithinHorizontalRadius(edited, center, radius))
            {
                yield return edited;
            }
        }
    }

    private static bool IsWithinHorizontalRadius(TerrainChunkAddress address, TerrainChunkAddress center, int radius) =>
        address.X >= center.X - radius && address.X <= center.X + radius
        && address.Z >= center.Z - radius && address.Z <= center.Z + radius;

    private static (long HorizontalDistance, long VerticalDistance, TerrainChunkAddress Address) DistancePriority(
        TerrainChunkAddress address, TerrainChunkAddress center)
    {
        long x = address.X - center.X;
        long z = address.Z - center.Z;
        return ((x * x) + (z * z), Math.Abs(address.Y - center.Y), address);
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
