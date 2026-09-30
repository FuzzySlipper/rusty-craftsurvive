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
    private long admissions;
    private long evictions;

    internal int ResidentCount => resident.Count;

    /// <summary>Whether a chunk is in the Engine's scene now, with its collision.</summary>
    internal bool IsResident(TerrainChunkAddress chunk) => resident.ContainsKey(chunk);

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"cached={cache.Count} pendingWrites={pendingCacheWrites.Count} resident={resident.Count} admissions={admissions} evictions={evictions} staleDropped={cache.StaleDropped} cacheHits={generator.CacheHits}");

    /// <summary>Moves residency one bounded step toward the plan for the centre; true when anything changed.</summary>
    internal bool Synchronize(SpatialSession session, TerrainChunkAddress center)
    {
        DrainCacheWrite();

        TerrainResidencyPlan plan = policy.PlanFor(center, overlay);
        List<VoxelResidencyOperation> operations = [];
        List<uint> materialSlots = [];
        foreach (TerrainChunkAddress address in plan.Requested)
        {
            if (resident.ContainsKey(address))
            {
                continue;
            }

            AddAdmission(plan.Chunk(address), operations, materialSlots);
            admissions++;
            if (operations.Count == plan.MaximumOperationsPerTick)
            {
                break;
            }
        }

        foreach (TerrainChunkAddress address in resident.Keys)
        {
            if (plan.Retained.Contains(address) || operations.Count == plan.MaximumOperationsPerTick)
            {
                continue;
            }

            // A chunk leaving the resident set is the chunk worth keeping: it would cost
            // generation again if the player turned around. Edited chunks are not cached; the
            // read path refuses them anyway.
            if (!overlay.TouchesChunk(address) && !pendingCacheWrites.Contains(address))
            {
                pendingCacheWrites.Enqueue(address);
            }

            evictions++;
            operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Evict, ToEngine(address), 0, 0));
        }

        if (operations.Count == 0)
        {
            return false;
        }

        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(session, operations.ToArray(), materialSlots.ToArray()));
        Refresh(session, operations.Select(operation => new TerrainChunkAddress(operation.Chunk.X, operation.Chunk.Y, operation.Chunk.Z)));
        return true;
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

    internal void Clear() => resident.Clear();

    /// <summary>
    /// Writes at most one evicted chunk to the cache per update. A write is regeneration plus a
    /// store, so it stays off the path of a chunk becoming visible. The cache holds what the
    /// generator produces, never the player's edits.
    /// </summary>
    private void DrainCacheWrite()
    {
        if (!pendingCacheWrites.TryDequeue(out TerrainChunkAddress address) || resident.ContainsKey(address))
        {
            return;
        }

        TerrainChunk chunk = generator.Generate(address, new TerrainOverlaySnapshot(seed, []));
        cache.Write(address, chunk.Materials.Span);
    }

    private static void AddAdmission(TerrainChunk chunk, List<VoxelResidencyOperation> operations, List<uint> materialSlots)
    {
        uint offset = checked((uint)materialSlots.Count);
        foreach (ushort material in chunk.Materials.Span)
        {
            materialSlots.Add(material);
        }

        operations.Add(new VoxelResidencyOperation(
            VoxelResidencyOperationKind.Admit,
            ToEngine(chunk.Address),
            offset,
            checked((uint)chunk.Materials.Length)));
    }

    private static VoxelChunkIdentity ToEngine(TerrainChunkAddress address) => new(address.X, address.Y, address.Z);
}
