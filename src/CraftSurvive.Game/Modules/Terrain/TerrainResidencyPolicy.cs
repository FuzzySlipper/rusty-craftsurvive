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

    /// <summary>The ground requested about the player, in chunks: the player's view distance (#9759).</summary>
    internal int RequestedRadius { get; private set; } = TerrainConstants.RequestedChunkRadius;

    /// <summary>The ring kept beyond the request, so crossing a chunk boundary reuses chunks.</summary>
    internal int RetainedRadius => RequestedRadius + (TerrainConstants.RetainedChunkRadius - TerrainConstants.RequestedChunkRadius);

    /// <summary>
    /// Changes the request window. The next plan is built for it while the player stands still; the
    /// streamer admits and evicts the difference within its per-update budget.
    /// </summary>
    internal void SetRequestedRadius(int radius)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(radius, 1);
        if (radius == RequestedRadius) return;
        RequestedRadius = radius;
        cachedPlan = null;
    }
    private TerrainOverlaySnapshot? cachedOverlay;
    private ulong cachedOverlayRevision;

    /// <summary>
    /// What is kept underground (#10889): the chunks of the caves and tendrils round the player, kept as the surface is;
    /// how far round the player the overworld's surface is kept (all of it at the surface, none deep in a cave); and
    /// whether the player's own storey is kept in every column (not underground, where it is buried rock). Its revision
    /// changes whenever any of them does.
    /// </summary>
    internal sealed record UndergroundResidency(long Revision, int SurfaceRadius, bool Storey, IReadOnlySet<TerrainChunkAddress> Shell)
    {
        internal static UndergroundResidency None { get; } = new(0, int.MaxValue, true, new HashSet<TerrainChunkAddress>());
    }

    /// <summary>The underground the plans are built for.</summary>
    internal UndergroundResidency Underground { get; private set; } = UndergroundResidency.None;

    private long plannedUnderground = -1;

    /// <summary>Sets what is kept underground; the next plan follows it.</summary>
    internal void SetUnderground(UndergroundResidency underground)
    {
        ArgumentNullException.ThrowIfNull(underground);
        if (underground.Revision == Underground.Revision) return;
        Underground = underground;
        cachedPlan = null;
    }

    /// <summary>
    /// Drops the payloads produced for chunks whose generation changed without an edit (a cave admitted or let go,
    /// #10889), so the next plan generates them again; the streamer replaces the resident ones.
    /// </summary>
    internal void Forget(Func<TerrainChunkAddress, bool> changed)
    {
        foreach (TerrainChunkAddress address in cachedChunks.Keys.Where(changed).ToArray())
        {
            cachedChunks.Remove(address);
        }

        cachedPlan = null;
    }

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
        if (cachedPlan is not null && cachedPlan.Center == center && cachedOverlayRevision == overlay.Revision && plannedUnderground == Underground.Revision)
        {
            return cachedPlan;
        }

        plannedUnderground = Underground.Revision;

        long planStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        bool overlayChanged = cachedOverlay is null || cachedOverlayRevision != overlay.Revision;
        TerrainOverlaySnapshot snapshot = overlayChanged ? overlay.Snapshot() : cachedOverlay!;
        // A restore or an edit not reported through RefreshAfterOverlayChange
        // invalidates the payloads. Ordinary boundary crossings reuse overlap.
        if (overlayChanged) cachedChunks.Clear();
        HashSet<TerrainChunkAddress> candidates = CandidateChunks(center, RetainedRadius, snapshot).ToHashSet();
        long gathered = System.Diagnostics.Stopwatch.GetTimestamp();
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
        long building = System.Diagnostics.Stopwatch.GetTimestamp();
        cachedPlan = BuildPlan(center, snapshot);
        long built = System.Diagnostics.Stopwatch.GetTimestamp();
        LastPlanTiming = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"candidates={Ms(planStarted, gathered):F1}({candidates.Count}) prune={Ms(gathered, building):F1} build={Ms(building, built):F1}(content {contentScans} scanned)");
        contentScans = 0;
        return cachedPlan;
    }

    /// <summary>
    /// Works out ahead, within a time budget, what the next plans will ask of the recipe (#10889 R10889-2): the bands of the
    /// columns just past the retained ring and whether their chunks hold anything, nearest first. Crossing a chunk border
    /// brings a row of new columns into the ring, each a few hundred samples of the map; warmed ahead, the plan finds them
    /// known instead of stalling the update that crosses.
    /// </summary>
    internal int WarmAhead(TerrainChunkAddress center, double budgetMs)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int warmed = 0;
        int inner = RetainedRadius - 1, outer = RetainedRadius + WarmRings;
        int surface = Math.Min(outer, Underground.SurfaceRadius);
        for (int ring = inner; ring <= surface; ring++)
        {
            for (long x = center.X - ring; x <= center.X + ring; x++)
            {
                for (long z = center.Z - ring; z <= center.Z + ring; z++)
                {
                    if (Math.Max(Math.Abs(x - center.X), Math.Abs(z - center.Z)) != ring)
                    {
                        continue;
                    }

                    if (System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds >= budgetMs)
                    {
                        return warmed;
                    }

                    bool known = recipe.KnowsColumnBand(x, z);
                    (long minimum, long maximum) = recipe.ChunkColumnBand(x, z);
                    warmed += known ? 0 : 1;
                    for (long y = minimum; y <= maximum; y++)
                    {
                        TerrainChunkAddress address = new(x, y, z);
                        if (!recipe.KnowsChunkContent(address))
                        {
                            recipe.ChunkHasContent(address);
                            warmed++;
                        }
                    }
                }
            }
        }

        return warmed;
    }

    /// <summary>How many rings past the retained one are warmed ahead.</summary>
    private const int WarmRings = 2;

    /// <summary>Where the last plan's time went (#10889 R10889-2).</summary>
    internal string LastPlanTiming { get; private set; } = "";

    private int contentScans;

    /// <summary>
    /// A chunk already produced for the overlay at <paramref name="overlayRevision"/>: its materials are the world's now,
    /// edits included (each edit patches it; any other overlay change drops the produced chunks). False when none is held,
    /// or the policy has not caught up with the overlay.
    /// </summary>
    internal bool TryProduced(TerrainChunkAddress address, ulong overlayRevision, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TerrainChunk? chunk)
    {
        chunk = null;
        return cachedOverlay is not null && cachedOverlayRevision == overlayRevision && cachedChunks.TryGetValue(address, out chunk);
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
        HashSet<TerrainChunkAddress> candidates = CandidateChunks(cachedPlan.Center, RetainedRadius, snapshot).ToHashSet();
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

        // A cave's or tendril's chunk holds its rock round the air (#10889): content, without scanning the map's columns for it.
        if (Underground.Shell.Contains(address))
        {
            return true;
        }

        // The predicate answers for the generated world; an edit can put content into a chunk
        // generation leaves empty, and one that arrives without RefreshAfterOverlayChange
        // clears the payloads rather than refreshing them, so the overlay is consulted here
        // too. Without this an unreported edit is invisible until something happens to
        // request the chunk for another reason.
        contentScans++;
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
            .Where(address => IsWithinHorizontalRadius(address, center, RequestedRadius))
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
        // Underground (#10889) the surface is kept only so far round the player, none of it deep in a cave, and the storey
        // is buried rock, not kept.
        UndergroundResidency underground = Underground;
        int surface = Math.Min(radius, underground.SurfaceRadius);
        long storey = underground.Storey ? TerrainConstants.PlayerStoreyChunks : -1;
        for (long x = center.X - surface; x <= center.X + surface; x++)
        {
            for (long z = center.Z - surface; z <= center.Z + surface; z++)
            {
                (long bandMinimum, long bandMaximum) = recipe.ChunkColumnBand(x, z);
                long lower = Math.Max(minimumY, storey < 0 ? bandMinimum : Math.Min(bandMinimum, center.Y - storey));
                long upper = Math.Min(maximumY, storey < 0 ? bandMaximum : Math.Max(bandMaximum, center.Y + storey));
                for (long y = lower; y <= upper; y++)
                {
                    if (y >= bandMinimum && y <= bandMaximum || Math.Abs(y - center.Y) <= storey)
                    {
                        yield return new TerrainChunkAddress(x, y, z);
                    }
                }
            }
        }

        // The caves' and tendrils' chunks round the player, as far as the surface would be kept.
        foreach (TerrainChunkAddress cave in underground.Shell)
        {
            if (IsWithinHorizontalRadius(cave, center, radius))
            {
                yield return cave;
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
