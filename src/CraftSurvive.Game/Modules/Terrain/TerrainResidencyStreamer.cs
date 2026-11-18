using System.Globalization;
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
        $"cached={cache.Count} pendingWrites={pendingCacheWrites.Count} resident={resident.Count} admissions={admissions} evictions={evictions} replaced={replacements} stale={stale.Count} batches={urgentBatches}+{deferredBatches}deferred undrawn={deferredUndrawn} staleDropped={cache.StaleDropped} cacheHits={generator.CacheHits} cacheWrites={(cache.Failure is null ? "on" : "off")}{(cache.Failure is null ? "" : $" cacheFailure=\"{cache.Failure}\"")}");

    // A transaction's operations and payloads.
    private sealed class Batch
    {
        internal List<VoxelResidencyOperation> Operations { get; } = [];
        internal List<uint> Materials { get; } = [];
        internal List<float> Densities { get; } = [];
    }

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
        policy.WarmAhead(center, Math.Max(TerrainConstants.ResidencyWarmMinimumMs, TerrainConstants.ResidencyGenerationBudgetMs - spent));
        return result;
    }

    private long tCache, tPlan, tGenerate, tApply;
    private int generatedNow;

    private static double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;

    private bool SynchronizeTimed(SpatialSession session, TerrainChunkAddress center, long t0)
    {
        DrainCacheWrite();
        tCache = System.Diagnostics.Stopwatch.GetTimestamp();
        generatedNow = 0;
        bool done = SynchronizeInner(session, center);
        long end = System.Diagnostics.Stopwatch.GetTimestamp();
        LastTiming = string.Create(CultureInfo.InvariantCulture,
            $"cacheWrite={Ms(t0, tCache):F1} plan={Ms(tCache, tPlan):F1}{(Ms(tCache, tPlan) >= 5 ? $"[{policy.LastPlanTiming}]" : "")} generate={Ms(tPlan, tGenerate):F1}x{generatedNow} apply={Ms(tGenerate, tApply == 0 ? end : tApply):F1} readback={Ms(tApply == 0 ? end : tApply, end):F1}");
        tApply = 0;
        return done;
    }

    private bool SynchronizeInner(SpatialSession session, TerrainChunkAddress center)
    {

        // Deferred chunks handed over earlier are drawn once the scene's meshes reach their revision.
        bool installed = false;
        if (deferredUndrawn > 0 && engine.Voxel.ReadScene(new VoxelSceneReadRequest(session)).MeshRevision >= deferredUndrawn)
        {
            deferredUndrawn = 0;
            installed = true;
        }

        TerrainResidencyPlan plan = policy.PlanFor(center, overlay);
        tPlan = System.Diagnostics.Stopwatch.GetTimestamp();
        tGenerate = tPlan;
        Batch urgent = new(), deferred = new();
        int Count() => urgent.Operations.Count + deferred.Operations.Count;
        // Generating chunks is the update's cost: past a time budget no more are generated this update (at least one is), so
        // streaming through caves, where more chunks come in per metre walked, keeps the frame (#10889).
        long generating = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Spent() => Count() > 0
            && System.Diagnostics.Stopwatch.GetElapsedTime(generating).TotalMilliseconds >= TerrainConstants.ResidencyGenerationBudgetMs;

        // Chunks a cave changed are replaced first, nearest the player first, within the same budget (#10889).
        foreach (TerrainChunkAddress address in stale.OrderBy(a => Math.Abs(a.X - center.X) + Math.Abs(a.Y - center.Y) + Math.Abs(a.Z - center.Z)).ToArray())
        {
            if (Count() == plan.MaximumOperationsPerTick || Spent())
            {
                break;
            }

            stale.Remove(address);
            if (!resident.ContainsKey(address) || !plan.Retained.Contains(address))
            {
                continue;
            }

            TerrainChunk replaced = plan.Chunk(address);
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

            if (Spent())
            {
                break;
            }

            TerrainChunk admitted = plan.Chunk(address);
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
            if (plan.Retained.Contains(address) || Count() == plan.MaximumOperationsPerTick)
            {
                continue;
            }

            // A chunk leaving the resident set is the chunk worth keeping: it would cost
            // generation again if the player turned around. Edited chunks are not cached; the
            // read path refuses them anyway.
            if (!overlay.TouchesChunk(address) && generator.Cacheable(address) && !pendingCacheWrites.Contains(address))
            {
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
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty,
                session, urgent.Operations.ToArray(), urgent.Materials.ToArray(), urgent.Densities.ToArray()));
            urgentBatches++;
            // Every deferred hand-off before it is installed now too.
            deferredUndrawn = 0;
            installed = true;
        }

        if (deferred.Operations.Count > 0)
        {
            VoxelResidencyReceipt receipt = engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty,
                session, deferred.Operations.ToArray(), deferred.Materials.ToArray(), deferred.Densities.ToArray(), true));
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
            VoxelChunkReadout readout = engine.Voxel.ReadChunk(new VoxelChunkReadRequest(session, ToEngine(address)));
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
    /// Writes at most one evicted chunk to the cache per update. A write is regeneration plus a
    /// store, so it stays off the path of a chunk becoming visible. The cache holds what the
    /// generator produces, never the player's edits.
    /// </summary>
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
        cache.Write(address, kept && !overlay.TouchesChunk(address) ? materials.Span : generator.Generate(address, new TerrainOverlaySnapshot(seed, [])).Materials.Span);
    }

    private static void AddAdmission(TerrainChunk chunk, Batch batch, VoxelResidencyOperationKind kind = VoxelResidencyOperationKind.Admit)
    {
        List<VoxelResidencyOperation> operations = batch.Operations;
        List<uint> materialSlots = batch.Materials;
        List<float> densities = batch.Densities;
        uint offset = checked((uint)materialSlots.Count);
        uint densityOffset = checked((uint)densities.Count);
        foreach (ushort material in chunk.Materials.Span)
        {
            materialSlots.Add(material);
        }
        foreach (float density in chunk.Densities.Span)
        {
            densities.Add(density);
        }

        operations.Add(new VoxelResidencyOperation(
            kind,
            ToEngine(chunk.Address),
            offset,
            checked((uint)chunk.Materials.Length),
            densityOffset,
            checked((uint)chunk.Densities.Length)));
    }

    private static VoxelChunkIdentity ToEngine(TerrainChunkAddress address) => new(address.X, address.Y, address.Z);
}
