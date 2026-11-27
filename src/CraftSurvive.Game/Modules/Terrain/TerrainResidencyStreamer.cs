using System.Globalization;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Keeps the Engine's resident chunks in step with the residency policy around the player: a
/// bounded number of admissions and evictions per update, each evicted chunk written to the chunk
/// cache off the visible path, and a copy of the Engine's readout for every resident chunk.
/// </summary>
internal sealed class TerrainResidencyStreamer(
    IEngineContext engine,
    TerrainResidencyPolicy policy,
    TerrainChunkGenerator generator,
    TerrainChunkCache cache,
    TerrainOverlayState overlay,
    ulong seed)
{
    private readonly Dictionary<TerrainChunkAddress, VoxelChunkReadout> resident = [];
    private readonly Queue<TerrainChunkAddress> pendingCacheWrites = new();

    // The materials each resident chunk was admitted with, and an evicted one's until its cache write: what the generator
    // produced (an edited chunk is never cached), so the write needs no second generation (#10889: going down a cave lets
    // hundreds of overworld chunks go at once).
    private readonly Dictionary<TerrainChunkAddress, ReadOnlyMemory<ushort>> admittedMaterials = [];
    private readonly Dictionary<TerrainChunkAddress, ReadOnlyMemory<ushort>> evictedMaterials = [];

    /// <summary>Resident chunks whose generation changed without an edit (a cave admitted, #10889), replaced a few an update.</summary>
    private readonly HashSet<TerrainChunkAddress> stale = [];

    /// <summary>The workers generating chunks away from the player (#10907).</summary>
    private readonly TerrainChunkPreparer preparer = new(generator);

    /// <summary>Chunks a worker gave back (a draw it was not given): the update thread generates them itself.</summary>
    private readonly HashSet<TerrainChunkAddress> generateHere = [];

    /// <summary>Whether chunks away from the player are generated on workers (#10907), or all on the update thread for a comparison.</summary>
    internal bool Workers { get; set; } = true;

    /// <summary>Lets the workers go: what they are generating is dropped.</summary>
    internal void Stop()
    {
        preparer.Dispose();
        reads.Dispose();
    }

    /// <summary>A chunk a worker finished, if it was generated against the world as it stands under that chunk now.</summary>
    private TerrainChunk? FromWorker(TerrainResidencyPlan plan, TerrainChunkAddress address)
    {
        long adopting = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return FromWorkerTimed(plan, address);
        }
        finally
        {
            adoptTicks += System.Diagnostics.Stopwatch.GetTimestamp() - adopting;
        }
    }

    private long adoptTicks, askTicks;

    private TerrainChunk? FromWorkerTimed(TerrainResidencyPlan plan, TerrainChunkAddress address)
    {
        if (!preparer.TryTake(address, out TerrainChunkPreparer.Prepared? prepared))
        {
            return null;
        }

        if (prepared.Chunk is null)
        {
            generateHere.Add(address);
            return null;
        }

        // An edit in the chunk since, or a cave admitted or let go under it since, and it is asked again.
        TerrainRecipe recipe = generator.Recipe;
        bool overlayCurrent = prepared.OverlayRevision == plan.OverlayRevision || (!prepared.Edited && !plan.Overlay.TouchesChunk(address));
        bool cavesCurrent = prepared.CavesRevision == recipe.Caves.Revision || !recipe.Caves.Touches(address);
        if (!overlayCurrent || !cavesCurrent || !plan.Retains(address))
        {
            preparer.Dropped();
            return null;
        }

        return plan.Adopt(address, prepared.Chunk);
    }

    /// <summary>Hands a chunk to the workers, its draws made first, if there is room and the update's time for it lasts.</summary>
    private void Ask(TerrainResidencyPlan plan, TerrainChunkAddress address, long asking)
    {
        if (!askingNow || preparer.Holds(address) || !preparer.CanQueue
            || System.Diagnostics.Stopwatch.GetElapsedTime(asking).TotalMilliseconds >= TerrainConstants.ResidencyAskBudgetMs)
        {
            return;
        }

        long asking0 = System.Diagnostics.Stopwatch.GetTimestamp();
        TerrainRecipe recipe = generator.Recipe;
        preparer.Queue(address, plan.Overlay, plan.OverlayRevision, recipe.Caves.Revision);
        asked++;
        askTicks += System.Diagnostics.Stopwatch.GetTimestamp() - asking0;
    }

    private long asked;

    /// <summary>Whether this update hands chunks to the workers: in a light one (#10907), as their draws are made here.</summary>
    private bool askingNow;
    private World.Deferral askDeferral, warmDeferral;

    /// <summary>Whether a chunk is generated on the update thread now rather than on a worker.</summary>
    private bool HereNow(TerrainResidencyPlan plan, TerrainChunkAddress address, TerrainChunkAddress center) =>
        !Workers || Round(address, center) || generateHere.Contains(address) || plan.Produced(address);
    private long replacements;

    /// <summary>Keeps a chunk's admitted materials for its cache write, only while they are the generator's alone (no edit in it).</summary>
    private void Keep(TerrainChunkAddress address, ReadOnlyMemory<ushort> materials)
    {
        if (overlay.TouchesChunk(address))
        {
            admittedMaterials.Remove(address);
        }
        else
        {
            admittedMaterials[address] = materials;
        }
    }

    /// <summary>The workers generating chunks other owners read and residency does not hold (the chemistry's window, #10907).</summary>
    private readonly TerrainChunkPreparer reads = new(generator);

    /// <summary>Finished reads held at most for owners that may not come back for them.</summary>
    private const int MostReadsHeld = 64;

    /// <summary>
    /// A chunk's generated materials from a worker (#10907), for an owner reading a chunk residency does not hold and no edit
    /// reaches: true once a worker has generated it against the caves as they stand; otherwise it is asked for (its draws
    /// made here first) and <paramref name="pending"/> says to come back, unless a worker gave it back, when the owner reads
    /// it itself.
    /// </summary>
    internal bool TryWorkerMaterials(TerrainChunkAddress address, TerrainOverlaySnapshot snapshot, ulong overlayRevision, Span<ushort> materials, out bool pending)
    {
        pending = false;
        reads.Collect();
        TerrainRecipe recipe = generator.Recipe;
        if (reads.TryTake(address, out TerrainChunkPreparer.Prepared? prepared))
        {
            if (prepared.Chunk is null)
            {
                return false;
            }

            if (prepared.CavesRevision == recipe.Caves.Revision || !recipe.Caves.Touches(address))
            {
                prepared.Chunk.Materials.Span.CopyTo(materials);
                return true;
            }
        }

        pending = true;
        if (!reads.Holds(address) && reads.CanQueue)
        {
            reads.Queue(address, snapshot, overlayRevision, recipe.Caves.Revision);
        }

        return false;
    }

    /// <summary>Lets go of finished reads no owner came back for.</summary>
    private void TrimReads()
    {
        if (reads.Ready > MostReadsHeld)
        {
            reads.Keep(_ => false);
        }
    }

    /// <summary>
    /// A resident chunk's materials as it was admitted, while no edit has reached it since (#10907): what the chemistry's window
    /// reads of a chunk the plan no longer holds (a cave's, as the plans move with the descent), not cell by cell.
    /// </summary>
    internal bool TryAdmittedMaterials(TerrainChunkAddress address, Span<ushort> materials)
    {
        if (!resident.ContainsKey(address) || overlay.TouchesChunk(address) || !admittedMaterials.TryGetValue(address, out ReadOnlyMemory<ushort> kept))
        {
            return false;
        }

        kept.Span.CopyTo(materials);
        return true;
    }

    /// <summary>Marks resident chunks to be generated again and replaced in the Engine (the rest come in with the plan).</summary>
    internal void MarkStale(IEnumerable<TerrainChunkAddress> addresses)
    {
        foreach (TerrainChunkAddress address in addresses)
        {
            if (resident.ContainsKey(address)) stale.Add(address);
        }
    }

    /// <summary>How many resident chunks wait to be replaced.</summary>
    internal int StaleCount => stale.Count;
    private long admissions;
    private long evictions;
    /// <summary>Chunks the plan wants that are not resident yet, as of the last synchronize: 0 once the window has filled.</summary>
    internal int PendingAdmissions { get; private set; }

    internal int ResidentCount => resident.Count;

    /// <summary>The chunks resident now.</summary>
    internal IEnumerable<TerrainChunkAddress> Resident => resident.Keys;

    /// <summary>Whether a chunk is in the Engine's scene now, with its collision.</summary>
    internal bool IsResident(TerrainChunkAddress chunk) => resident.ContainsKey(chunk);

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"workers=[{(Workers ? "on" : "off")} asked={asked} hereAfterWorker={generateHere.Count} {preparer.Readout()}] cached={cache.Count} pendingWrites={pendingCacheWrites.Count} writesDropped={cacheWritesDropped} resident={resident.Count} admissions={admissions} evictions={evictions} replaced={replacements} stale={stale.Count} batches={urgentBatches}+{deferredBatches}deferred undrawn={deferredUndrawn} staleDropped={cache.StaleDropped} cacheHits={generator.CacheHits} cacheWrites={(cache.Failure is null ? "on" : "off")}{(cache.Failure is null ? "" : $" cacheFailure=\"{cache.Failure}\"")}");

    // A transaction's operations and payloads, kept across updates (#10907): eight chunks' payloads made afresh each update were
    // arrays of 128 KB and more, which went to the large object heap and brought on the collector's slow collections. The
    // Engine reads a transaction's payload during the call.
    private sealed class Batch
    {
        internal List<VoxelResidencyOperation> Operations { get; } = [];
        private uint[] materials = new uint[TerrainConstants.ChunkVolume];
        private float[] densities = new float[TerrainConstants.ChunkVolume];
        internal int MaterialCount { get; private set; }
        internal int DensityCount { get; private set; }

        internal ReadOnlyMemory<uint> Materials => new(materials, 0, MaterialCount);

        internal ReadOnlyMemory<float> Densities => new(densities, 0, DensityCount);

        internal void Clear()
        {
            Operations.Clear();
            MaterialCount = 0;
            DensityCount = 0;
        }

        internal void Append(ReadOnlySpan<ushort> chunkMaterials, ReadOnlySpan<float> chunkDensities)
        {
            if (MaterialCount + chunkMaterials.Length > materials.Length) Array.Resize(ref materials, Math.Max(materials.Length * 2, MaterialCount + chunkMaterials.Length));
            if (DensityCount + chunkDensities.Length > densities.Length) Array.Resize(ref densities, Math.Max(densities.Length * 2, DensityCount + chunkDensities.Length));
            Span<uint> into = materials.AsSpan(MaterialCount, chunkMaterials.Length);
            for (int i = 0; i < chunkMaterials.Length; i++) into[i] = chunkMaterials[i];
            chunkDensities.CopyTo(densities.AsSpan(DensityCount));
            MaterialCount += chunkMaterials.Length;
            DensityCount += chunkDensities.Length;
        }
    }

    private readonly Batch urgentBatch = new(), deferredBatch = new();

    /// <summary>
    /// The scene revision the last deferred transaction installs at, while the presentation has not drawn it (0 when it
    /// has): chunks away from the player are meshed on the Engine's worker (#10889; rusty-engine #10327, as the streamed
    /// cave of #10332 did), and the presentation is refreshed once they are in.
    /// </summary>
    private ulong deferredUndrawn;
    private long deferredBatches, urgentBatches;

    /// <summary>Whether a chunk is round the player's own: handed over at once, so they never stand where rock is not yet live.</summary>
    private static bool Round(TerrainChunkAddress chunk, TerrainChunkAddress center) =>
        Math.Abs(chunk.X - center.X) <= 1 && Math.Abs(chunk.Y - center.Y) <= 1 && Math.Abs(chunk.Z - center.Z) <= 1;

    /// <summary>Where the last synchronize's time went (#10889 R10889-2): cache write, plan, generation, Engine hand-off, readback.</summary>
    internal string LastTiming { get; private set; } = "";

    /// <summary>Moves residency one bounded step toward the plan for the centre; true when the presentation should be refreshed.</summary>
    internal bool Synchronize(SpatialSession session, TerrainChunkAddress center)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool result = SynchronizeTimed(session, center, t0);
        // What the next plans will ask, worked out ahead with what is left of a small budget (#10889 R10889-2).
        double spent = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        // Warming ahead waits for a light update too (#10907).
        if (warmDeferral.Runs())
        {
            long warming = System.Diagnostics.Stopwatch.GetTimestamp();
            policy.WarmAhead(center, Math.Max(TerrainConstants.ResidencyWarmMinimumMs, TerrainConstants.ResidencyGenerationBudgetMs - spent));
            LastTiming += string.Create(CultureInfo.InvariantCulture, $" warm={Ms(warming, System.Diagnostics.Stopwatch.GetTimestamp()):F1}");
        }
        return result;
    }

    private long tCache, tPlan, tGenerate, tApply, engineTicks;

    /// <summary>The time the last synchronize spent in Engine services (#10907): the residency hand-off, readback and the cache's store.</summary>
    internal double LastEngineMs { get; private set; }

    /// <summary>Times an Engine call into the synchronize's Engine share.</summary>
    private T Engine<T>(Func<T> call)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return call();
        }
        finally
        {
            engineTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        }
    }
    private int generatedNow;

    private static double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private bool SynchronizeTimed(SpatialSession session, TerrainChunkAddress center, long t0)
    {
        tCache = t0;
        generatedNow = 0;
        engineTicks = 0;
        adoptTicks = 0;
        askTicks = 0;
        bool done = SynchronizeInner(session, center);
        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        // An evicted chunk is written only while the player is not streaming and in a quiet update (#10907).
        lastCacheWrite = "";
        updatesSinceAdmission = generatedNow > 0 ? 0 : updatesSinceAdmission + 1;
        if (PlayerStill && updatesSinceAdmission >= TerrainConstants.ResidencyCacheWriteRestUpdates && Ms(t0, end) < TerrainConstants.ResidencyCacheWriteQuietMs)
        {
            DrainCacheWrite();
        }

        long written = System.Diagnostics.Stopwatch.GetTimestamp();
        LastEngineMs = System.Diagnostics.Stopwatch.GetElapsedTime(0, engineTicks).TotalMilliseconds + (lastCacheWrite.Length > 0 ? cache.LastWriteEngineMs : 0d);
        LastTiming = string.Create(CultureInfo.InvariantCulture,
            $"adopt={Ms(0, adoptTicks):F1} ask={Ms(0, askTicks):F1} plan={Ms(tCache, tPlan):F1}{(Ms(tCache, tPlan) >= 5 ? $"[{policy.LastPlanTiming}]" : "")} generate={Ms(tPlan, tGenerate):F1}x{generatedNow} apply={Ms(tGenerate, tApply == 0 ? end : tApply):F1} readback={Ms(tApply == 0 ? end : tApply, end):F1} cacheWrite={Ms(end, written):F1}{(lastCacheWrite.Length > 0 ? $"({lastCacheWrite}{cache.LastWriteTiming})" : "")}");
        tApply = 0;
        return done;
    }

    private bool SynchronizeInner(SpatialSession session, TerrainChunkAddress center)
    {

        // Deferred chunks handed over earlier are drawn once the scene's meshes reach their revision.
        bool installed = false;
        if (deferredUndrawn > 0 && Engine(() => engine.Voxel.ReadScene(new VoxelSceneReadRequest(session))).MeshRevision >= deferredUndrawn)
        {
            deferredUndrawn = 0;
            installed = true;
        }

        TerrainResidencyPlan plan = policy.PlanFor(center, overlay);
        tPlan = System.Diagnostics.Stopwatch.GetTimestamp();
        // What the workers finished since the last update; what the player has left behind meanwhile is let go.
        preparer.Collect();
        preparer.Keep(address => plan.Retains(address) && (!resident.ContainsKey(address) || stale.Contains(address)));
        TrimReads();
        if (generateHere.Count > TerrainConstants.MaximumResidentChunks)
        {
            generateHere.Clear();
        }

        long asking = System.Diagnostics.Stopwatch.GetTimestamp();
        askingNow = askDeferral.Runs();
        tGenerate = tPlan;
        Batch urgent = urgentBatch, deferred = deferredBatch;
        urgent.Clear();
        deferred.Clear();
        int Count() => urgent.Operations.Count + deferred.Operations.Count;
        // Generating chunks is the update's cost: past a time budget no more are generated this update (at least one is), so
        // streaming through caves, where more chunks come in per metre walked, keeps the frame (#10889).
        long generating = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Spent() => Count() > 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(generating).TotalMilliseconds >= TerrainConstants.ResidencyGenerationBudgetMs;

        // Chunks a cave changed are replaced first, nearest the player first, within the same budget (#10889); away from the
        // player a worker generates them (#10907).
        foreach (TerrainChunkAddress address in stale.OrderBy(a => Math.Abs(a.X - center.X) + Math.Abs(a.Y - center.Y) + Math.Abs(a.Z - center.Z)).ToArray())
        {
            if (Count() == plan.MaximumOperationsPerTick)
            {
                break;
            }

            if (!resident.ContainsKey(address) || !plan.Retains(address))
            {
                stale.Remove(address);
                continue;
            }

            TerrainChunk? replaced = FromWorker(plan, address);
            if (replaced is null)
            {
                if (!HereNow(plan, address, center))
                {
                    Ask(plan, address, asking);
                    continue;
                }

                if (Spent())
                {
                    break;
                }

                replaced = plan.Chunk(address);
            }

            stale.Remove(address);
            generateHere.Remove(address);
            AddAdmission(replaced, Round(address, center) ? urgent : deferred, VoxelResidencyOperationKind.Replace);
            Keep(address, replaced.Materials);
            replacements++;
        }

        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (resident.ContainsKey(address))
            {
                continue;
            }

            // Away from the player a worker generates the chunk (#10907); round the player, after a worker gave one back, or
            // with the workers off, the update generates it within its budget.
            TerrainChunk? admitted = FromWorker(plan, address);
            if (admitted is null)
            {
                if (!HereNow(plan, address, center))
                {
                    Ask(plan, address, asking);
                    continue;
                }

                if (Spent())
                {
                    break;
                }

                admitted = plan.Chunk(address);
            }

            generateHere.Remove(address);
            AddAdmission(admitted, Round(address, center) ? urgent : deferred);
            Keep(address, admitted.Materials);
            admissions++;
            if (Count() == plan.MaximumOperationsPerTick)
            {
                break;
            }
        }

        tGenerate = System.Diagnostics.Stopwatch.GetTimestamp();
        generatedNow = Count();
        foreach (TerrainChunkAddress address in resident.Keys)
        {
            if (plan.Retains(address) || Count() == plan.MaximumOperationsPerTick)
            {
                continue;
            }

            // A chunk leaving the resident set is the chunk worth keeping: it would cost
            // generation again if the player turned around. Edited chunks are not cached; the
            // read path refuses them anyway.
            if (!overlay.TouchesChunk(address) && generator.Cacheable(address) && !pendingCacheWrites.Contains(address))
            {
                if (pendingCacheWrites.Count >= TerrainConstants.MaximumPendingCacheWrites && pendingCacheWrites.TryDequeue(out TerrainChunkAddress oldest))
                {
                    evictedMaterials.Remove(oldest);
                    cacheWritesDropped++;
                }

                pendingCacheWrites.Enqueue(address);
                if (admittedMaterials.TryGetValue(address, out ReadOnlyMemory<ushort> kept))
                {
                    evictedMaterials[address] = kept;
                }
            }

            admittedMaterials.Remove(address);
            evictions++;
            deferred.Operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Evict, ToEngine(address), 0, 0));
        }

        if (Count() == 0)
        {
            PendingAdmissions = 0;
            return installed;
        }

        // Round the player first and at once: a change that is not deferred installs every deferred one first, waiting, so it
        // never follows this update's deferred hand-off.
        if (urgent.Operations.Count > 0)
        {
            VoxelResidencyTransaction transaction = new(ReadOnlyMemory<uint>.Empty,
                session, urgent.Operations.ToArray(), urgent.Materials, urgent.Densities);
            Engine(() => engine.Voxel.ApplyResidency(transaction));
            urgentBatches++;
            // Every deferred hand-off before it is installed now too.
            deferredUndrawn = 0;
            installed = true;
        }

        if (deferred.Operations.Count > 0)
        {
            VoxelResidencyTransaction transaction = new(ReadOnlyMemory<uint>.Empty,
                session, deferred.Operations.ToArray(), deferred.Materials, deferred.Densities, true);
            VoxelResidencyReceipt receipt = Engine(() => engine.Voxel.ApplyResidency(transaction));
            deferredUndrawn = Math.Max(deferredUndrawn, receipt.AcceptedRevision);
            deferredBatches++;
        }

        tApply = System.Diagnostics.Stopwatch.GetTimestamp();
        Refresh(session, urgent.Operations.Concat(deferred.Operations).Select(operation => new TerrainChunkAddress(operation.Chunk.X, operation.Chunk.Y, operation.Chunk.Z)));
        PendingAdmissions = plan.Requested.Count(address => !resident.ContainsKey(address));
        return installed;
    }

    /// <summary>Re-reads the Engine's view of these chunks after they changed.</summary>
    internal void Refresh(SpatialSession session, IEnumerable<TerrainChunkAddress> addresses)
    {
        foreach (TerrainChunkAddress address in addresses.Distinct())
        {
            VoxelChunkReadout readout = Engine(() => engine.Voxel.ReadChunk(new VoxelChunkReadRequest(session, ToEngine(address))));
            if (readout.Present)
            {
                resident[address] = readout;
            }
            else
            {
                resident.Remove(address);
            }
        }
    }

    internal void Clear()
    {
        resident.Clear();
        admittedMaterials.Clear();
        evictedMaterials.Clear();
    }

    /// <summary>
    /// Writes at most one evicted chunk to the cache per update, and only in a quiet one (#10907). A write is the Engine's
    /// store, so it stays off the path of a chunk becoming visible. The cache holds what the generator produces, never the
    /// player's edits.
    /// </summary>
    private string lastCacheWrite = "";
    private int updatesSinceAdmission;

    /// <summary>Whether the player has stood in one place a while: evicted chunks are written to the cache only then (#10907).</summary>
    internal bool PlayerStill { get; set; } = true;
    private long cacheWritesDropped;

    private void DrainCacheWrite()
    {
        if (!pendingCacheWrites.TryDequeue(out TerrainChunkAddress address))
        {
            return;
        }

        bool kept = evictedMaterials.Remove(address, out ReadOnlyMemory<ushort> materials);
        if (resident.ContainsKey(address))
        {
            return;
        }

        // The materials it was admitted with are what the generator produced; a chunk admitted before they were kept is generated again.
        lastCacheWrite = kept && !overlay.TouchesChunk(address) ? "kept" : "regenerated";
        cache.Write(address, kept && !overlay.TouchesChunk(address) ? materials.Span : generator.Generate(address, new TerrainOverlaySnapshot(seed, [])).Materials.Span);
    }

    private static void AddAdmission(TerrainChunk chunk, Batch batch, VoxelResidencyOperationKind kind = VoxelResidencyOperationKind.Admit)
    {
        uint offset = checked((uint)batch.MaterialCount);
        uint densityOffset = checked((uint)batch.DensityCount);
        batch.Append(chunk.Materials.Span, chunk.Densities.Span);
        batch.Operations.Add(new VoxelResidencyOperation(
            kind,
            ToEngine(chunk.Address),
            offset,
            checked((uint)chunk.Materials.Length),
            densityOffset,
            checked((uint)chunk.Densities.Length)));
    }

    private static VoxelChunkIdentity ToEngine(TerrainChunkAddress address) => new(address.X, address.Y, address.Z);
}
