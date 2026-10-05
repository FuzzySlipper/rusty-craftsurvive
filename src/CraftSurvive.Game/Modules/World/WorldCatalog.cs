using System.Diagnostics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// One owner for the active world choice. Its atomic saved record selects both map and
/// gameplay-key namespace. A store with no usable world never generates one during
/// construction: it starts the default world's simulation off-thread, so product startup
/// never waits on generation, and the world exists once an update commits it.
/// </summary>
internal sealed class WorldCatalog
{
    private readonly ProductStore store;
    private readonly ProductSaveSlot<WorldMapSave> slot;
    private Task<WorldMap>? pending;
    private long pendingStart;
    internal WorldCatalog(IEngineContext engine, ProductStore store)
    {
        this.store = store;
        slot = new(engine, store, SaveManifest.WorldMap, new WorldMapCodec());
        SaveRestoreDecision<WorldMapSave> restore = slot.Restore();
        if (restore.State is WorldMapSave restored)
        {
            current = restored;
            store.WorldGeneration = restored.Generation;
        }
        else
        {
            TerrainConfiguration first = TerrainConfiguration.Default;
            BeginPrepare(first.Seed, first.Size);
        }
    }

    private WorldMapSave? current;

    /// <summary>Whether a world has been restored or committed; until then only its preparation exists.</summary>
    internal bool HasWorld => current is not null;

    internal WorldMapSave Current => current ?? throw new InvalidOperationException("The first world is still being generated.");
    internal double GenerationMilliseconds { get; private set; }
    internal string RestoreOutcome => slot.RestoreOutcome;
    internal int StoredBytes => slot.Probe().Bytes;

    internal WorldMapSave Prepare(ulong seed, int size) => Generate(new(seed, size), NextGeneration);

    private long NextGeneration => checked((current?.Generation ?? 0) + 1);

    /// <summary>Whether a new world's map is still being simulated off the update thread.</summary>
    internal bool Preparing => pending is not null;

    /// <summary>
    /// Start simulating a new world's map. Generation is pure product computation over copied
    /// configuration, so it runs off-thread; only <see cref="TakePrepared"/>, called from an
    /// update, admits its result.
    /// </summary>
    internal void BeginPrepare(ulong seed, int size)
    {
        if (pending is not null) throw new InvalidOperationException("A world is already being prepared.");
        TerrainConfiguration configuration = new TerrainConfiguration(seed, size).Validate();
        pendingStart = Stopwatch.GetTimestamp();
        pending = Task.Run(() => WorldMapGenerator.Generate(configuration));
    }

    /// <summary>The prepared world once its simulation finishes, or null while it runs. A failure is rethrown once.</summary>
    internal WorldMapSave? TakePrepared()
    {
        if (pending is not { IsCompleted: true } finished) return null;
        pending = null;
        WorldMap map = finished.GetAwaiter().GetResult();
        GenerationMilliseconds = Stopwatch.GetElapsedTime(pendingStart).TotalMilliseconds;
        return new(NextGeneration, map);
    }

    internal bool Commit(WorldMapSave prepared)
    {
        if (!slot.Save(prepared)) return false;
        current = prepared;
        store.WorldGeneration = prepared.Generation;
        return true;
    }

    private WorldMapSave Generate(TerrainConfiguration configuration, long generation)
    {
        long start = Stopwatch.GetTimestamp();
        WorldMap map = WorldMapGenerator.Generate(configuration);
        GenerationMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return new(generation, map);
    }
}
