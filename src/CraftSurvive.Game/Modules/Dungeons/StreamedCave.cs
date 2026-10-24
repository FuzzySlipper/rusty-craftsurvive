using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The streamed underworld prototype (#10304: sectioned depths against a streamed underworld). An endless cave in its own
/// separate space, generated chunk by chunk from a field local to each point (tunnels where two noises are both near zero,
/// chambers where a broad noise is high), admitted nearest-first around the player within a budget each update and evicted
/// behind, with the indirect light's probe volume following the player. It measures what streaming costs and how well it
/// keeps up; it is not a cave generator (no water history, no routes, no ruins, no growth).
/// </summary>
internal sealed class StreamedCave : IDisposable
{
    private const int Edge = TerrainConstants.ChunkEdgeLength;

    // The cave's band of height (layout metres): rock below and above it.
    private const float Floor = 8f, Ceiling = 88f;

    // Tunnels: two noises sampled this many metres a cell (stretched along y so passages run level), open where the sum of
    // their squares is under this; chambers: a broad noise this many metres a cell across and up, open above this.
    private const float TunnelCell = 30f, TunnelLevel = 1.8f, TunnelRadius = 0.05f, ChamberCell = 70f, ChamberCellUp = 30f, ChamberAbove = 0.66f;

    // How sharply the field crosses zero, so the densities place the surface; the Engine's density limits.
    private const float TunnelSharpness = 40f, ChamberSharpness = 30f, DensityFloor = 0.001f, DensityLimit = 1f;

    /// <summary>The probe volume follows the player once they are this far from its centre; its spacing (metres) and bounces.</summary>
    private const float ProbeFollow = 24f, ProbeSpacing = 3f;
    private const uint ProbeBounces = 2;

    private readonly IEngineContext engine;
    private readonly ulong seed;
    private readonly int radius, radiusUp, budget;
    private readonly (long X, long Y, long Z) first;
    private readonly HashSet<(int X, int Y, int Z)> resident = [];
    private readonly VoxelScenePresentation projection;
    private Vector3? probeCentre;

    // Measures: chunks admitted and evicted, time spent generating and admitting, the slowest update, probe moves.
    private long admitted, evicted, updates;
    private double generateMs, applyMs, slowestMs, probeMs;
    private int probeMoves, lastPending, notReadyUpdates;

    /// <param name="radius">How many chunks (16 m) about the player are kept across; <paramref name="radiusUp"/> up and down.</param>
    /// <param name="budget">The most chunks admitted in one update.</param>
    internal StreamedCave(IEngineContext engine, TerrainWorld terrain, IReadOnlyDictionary<BlockId, Material>? rock, DungeonSurface surface, ulong seed,
        int radius, int radiusUp, int budget)
    {
        this.engine = engine;
        this.seed = seed;
        this.radius = radius;
        this.radiusUp = radiusUp;
        this.budget = budget;
        Session = DungeonCollision.CreateSession(engine, surface);
        projection = terrain.ProjectSeparateSpace(Session, rock);
        first = ((long)MathF.Floor(DungeonCollision.Origin.X / Edge), (long)MathF.Floor(DungeonCollision.Origin.Y / Edge), (long)MathF.Floor(DungeonCollision.Origin.Z / Edge));
        Arrival = FindStanding(new Vector3(8f, 40f, 8f));
    }

    internal SpatialSession Session { get; }

    /// <summary>Where the player starts, in layout metres: the nearest open floor to the origin.</summary>
    internal Vector3 Arrival { get; }

    /// <summary>How open the cave is at a point (layout metres): above 0 is air.</summary>
    internal float Open(Vector3 p)
    {
        if (p.Y < Floor || p.Y > Ceiling)
        {
            return -1f;
        }

        Vector3 t = new(p.X / TunnelCell, p.Y * TunnelLevel / TunnelCell, p.Z / TunnelCell);
        float a = NaturalCavern.Signed(NaturalCavern.Noise(t, seed ^ 0x51UL)), b = NaturalCavern.Signed(NaturalCavern.Noise(t, seed ^ 0x52UL));
        float tunnel = (TunnelRadius - ((a * a) + (b * b))) * TunnelSharpness;
        float c = NaturalCavern.Noise(new Vector3(p.X / ChamberCell, p.Y / ChamberCellUp, p.Z / ChamberCell), seed ^ 0x53UL);
        float chamber = (c - ChamberAbove) * ChamberSharpness;
        // Rock closes in near the band's floor and ceiling.
        float band = MathF.Min(p.Y - Floor, Ceiling - p.Y) * 0.25f;
        return MathF.Min(MathF.Max(tunnel, chamber), band);
    }

    /// <summary>The nearest standing place below a point: open, with rock under it.</summary>
    internal Vector3 FindStanding(Vector3 near)
    {
        for (int ring = 0; ring < 400; ring++)
        {
            for (int k = 0; k < 8; k++)
            {
                float angle = k * MathF.PI / 4f;
                Vector3 at = near + new Vector3(MathF.Cos(angle) * ring, 0f, MathF.Sin(angle) * ring);
                for (float y = Ceiling - 2f; y > Floor; y -= 1f)
                {
                    Vector3 p = new(MathF.Floor(at.X) + 0.5f, y, MathF.Floor(at.Z) + 0.5f);
                    if (Open(p) > 0f && Open(p + Vector3.UnitY) > 0f && Open(p + (Vector3.UnitY * 2f)) > 0f && Open(p - Vector3.UnitY) <= 0f)
                    {
                        return new Vector3(p.X, MathF.Floor(y), p.Z);
                    }
                }
            }
        }

        return near;
    }

    /// <summary>Moves residency toward the player (layout metres): admits nearest-first within the budget, evicts behind.</summary>
    internal void Update(Vector3 player, bool all = false)
    {
        long started = Stopwatch.GetTimestamp();
        updates++;
        (int cx, int cy, int cz) = ((int)MathF.Floor(player.X / Edge), (int)MathF.Floor(player.Y / Edge), (int)MathF.Floor(player.Z / Edge));
        int lowest = (int)MathF.Floor(Floor / Edge), highest = (int)MathF.Floor(Ceiling / Edge);
        List<(int X, int Y, int Z)> wanted = [];
        for (int x = cx - radius; x <= cx + radius; x++)
            for (int z = cz - radius; z <= cz + radius; z++)
                for (int y = Math.Max(lowest, cy - radiusUp); y <= Math.Min(highest, cy + radiusUp); y++)
                {
                    if (!resident.Contains((x, y, z)))
                    {
                        wanted.Add((x, y, z));
                    }
                }

        wanted.Sort((a, b) => Distance(a, cx, cy, cz).CompareTo(Distance(b, cx, cy, cz)));
        lastPending = wanted.Count;
        // Whether the chunks round the player (the 3 x 3 x 3 about them) are all in: if not, they could fall or walk into void.
        bool urgent = wanted.Any(w => Math.Abs(w.X - cx) <= 1 && Math.Abs(w.Y - cy) <= 1 && Math.Abs(w.Z - cz) <= 1);
        if (urgent)
        {
            notReadyUpdates++;
        }

        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        long generating = Stopwatch.GetTimestamp();
        foreach ((int X, int Y, int Z) chunk in wanted.Take(all ? wanted.Count : budget))
        {
            Generate(chunk, operations, materials, densities);
            resident.Add(chunk);
            admitted++;
        }

        foreach ((int X, int Y, int Z) chunk in resident.Where(r => Math.Max(Math.Abs(r.X - cx), Math.Abs(r.Z - cz)) > radius + 1 || Math.Abs(r.Y - cy) > radiusUp + 1).ToList())
        {
            operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Evict, Identity(chunk), 0, 0));
            resident.Remove(chunk);
            evicted++;
        }

        generateMs += Stopwatch.GetElapsedTime(generating).TotalMilliseconds;
        // Away from the player the hand-off is deferred (#10332), as the stored cave's is; a batch round them, or the first
        // whole fill, synchronous.
        bool deferring = DungeonSpace.StreamDeferred;
        if (operations.Count > 0 || deferring)
        {
            long applying = Stopwatch.GetTimestamp();
            if (operations.Count > 0)
            {
                engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, Session, operations.ToArray(), materials.ToArray(), densities.ToArray(),
                    deferring && !urgent && !all));
            }

            // The presentation follows residency only when refreshed; deferred chunks install at a later update's start, so a
            // deferring stream refreshes every update.
            engine.VoxelScenePresentation.RefreshScene(projection);
            applyMs += Stopwatch.GetElapsedTime(applying).TotalMilliseconds;
        }

        Vector3 here = DungeonCollision.InSession(player);
        if (probeCentre is not Vector3 centre || Vector3.Distance(centre, here) > ProbeFollow)
        {
            long probing = Stopwatch.GetTimestamp();
            Vector3 extent = new(radius * Edge, radiusUp * Edge, radius * Edge);
            engine.CameraView.SetIndirectLight(new IndirectLightRequest(here, extent, ProbeSpacing, ProbeBounces, IndirectAmbient.Floor));
            probeCentre = here;
            probeMoves++;
            probeMs += Stopwatch.GetElapsedTime(probing).TotalMilliseconds;
        }

        slowestMs = Math.Max(slowestMs, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private static int Distance((int X, int Y, int Z) a, int cx, int cy, int cz) => Math.Abs(a.X - cx) + (Math.Abs(a.Y - cy) * 2) + Math.Abs(a.Z - cz);

    private VoxelChunkIdentity Identity((int X, int Y, int Z) chunk) => new(chunk.X + first.X, chunk.Y + first.Y, chunk.Z + first.Z);

    private void Generate((int X, int Y, int Z) chunk, List<VoxelResidencyOperation> operations, List<uint> materials, List<float> densities)
    {
        uint offset = checked((uint)materials.Count), densityOffset = checked((uint)densities.Count);
        for (int index = 0; index < Edge * Edge * Edge; index++)
        {
            int x = (chunk.X * Edge) + (index % Edge), y = (chunk.Y * Edge) + (index / Edge % Edge), z = (chunk.Z * Edge) + (index / (Edge * Edge));
            float open = Open(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            bool air = open > 0f;
            materials.Add((uint)(air ? BlockId.Air : BlockId.Stone));
            densities.Add(air ? Math.Clamp(open, DensityFloor, DensityLimit) : Math.Clamp(open, -DensityLimit, -DensityFloor));
        }

        operations.Add(new VoxelResidencyOperation(VoxelResidencyOperationKind.Admit, Identity(chunk), offset, checked((uint)(materials.Count - (int)offset)),
            densityOffset, checked((uint)(densities.Count - (int)densityOffset))));
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"streamed cave: resident={resident.Count} pending={lastPending} admitted={admitted} evicted={evicted} updates={updates} notReadyUpdates={notReadyUpdates} " +
        $"generate={generateMs / Math.Max(1, admitted):F2}ms/chunk apply={applyMs / Math.Max(1, admitted):F2}ms/chunk slowestUpdate={slowestMs:F1}ms " +
        $"probeMoves={probeMoves} probe={probeMs / Math.Max(1, probeMoves):F1}ms/move");

    /// <summary>Clears the measures (after the first fill), so they describe walking.</summary>
    internal void ResetMeasures()
    {
        admitted = evicted = updates = 0;
        generateMs = applyMs = slowestMs = probeMs = 0;
        probeMoves = notReadyUpdates = 0;
    }

    public void Dispose()
    {
        engine.CameraView.SetIndirectLight(new IndirectLightRequest(Vector3.Zero, Vector3.Zero, ProbeSpacing, ProbeBounces, IndirectAmbient.Floor));
        projection.Dispose();
        Session.Dispose();
    }
}
