using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Building;

/// <summary>
/// Draws the remnants (#9731) in their own fine (<see cref="PieceRemnant.VoxelMetres"/>) dual-contoured
/// spatial session, with sharp features from their signed distances, as the building study's
/// quarter-metre cabin was drawn: a bitten wall keeps crisp planes and its craters read as broken
/// edges. The session is aligned to the walking frame like the far field, draws only (its pieces
/// collide through <see cref="PieceRemnant.Boxes"/>), and redraws just the chunks a change touches.
/// </summary>
internal sealed class RemnantVoxels : IDisposable
{
    private const float SharpCreaseDegrees = 30f;
    private const float DensityFloor = 0.001f;
    private const float FaceDensity = 0.5f;

    /// <summary>The construction maps each piece material draws with, by the bound block whose slot carries it.</summary>
    private static readonly Dictionary<PieceMaterial, (BlockId Block, string Map)> Slots = new()
    {
        [PieceMaterial.Planks] = (BlockId.Planks, "planks"),
        [PieceMaterial.Timber] = (BlockId.Log, "timber"),
        [PieceMaterial.Masonry] = (BlockId.Cobblestone, "masonry"),
        [PieceMaterial.Shingles] = (BlockId.Brick, "shingles"),
    };

    private readonly IEngineContext engine;
    private readonly WorldFrame frame;
    private readonly RemnantSet remnants;
    private readonly TerrainGroundMaterials maps;
    private readonly SpatialSession session;
    private readonly HashSet<(long X, long Y, long Z)> resident = [];
    private VoxelScenePresentation? projection;
    private (long X, long Y, long Z) alignedOrigin = (long.MinValue, 0, 0);

    internal RemnantVoxels(IEngineContext engine, ProductContent content, WorldFrame frame, RemnantSet remnants)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.frame = frame ?? throw new ArgumentNullException(nameof(frame));
        this.remnants = remnants ?? throw new ArgumentNullException(nameof(remnants));
        maps = new TerrainGroundMaterials(engine, content, PieceRemnant.VoxelMetres, GroundTextureSet.Construction);
        try
        {
            session = engine.Spatial.CreateSession(new SpatialSessionConfig(PieceRemnant.VoxelMetres, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.DualContouring));
            uint[] slots = [.. Slots.Values.Select(entry => (uint)BlockRegistry.Get(entry.Block).Slot)];
            SurfaceCharacter sharp = new(VertexPlacement.Sharp, SharpCreaseDegrees, 0f);
            engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(session, VoxelSurfaceMode.DualContouring,
                slots.Select(slot => new VoxelMaterialSurface(slot, VoxelSurfaceMode.DualContouring, sharp)).ToArray()));
            // Drawing only: a second colliding session would compete with the walking world for the player.
            engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(session, slots.Select(slot => new VoxelMaterialCollision(slot, false)).ToArray()));
            engine.Voxel.ConfigureMaterialOcclusion(new VoxelMaterialOcclusionRequest(session, slots.Select(slot => new VoxelMaterialOcclusion(slot, true)).ToArray()));
        }
        catch
        {
            maps.Dispose();
            throw;
        }

        frame.Rebased += _ => Align();
        Align();
    }

    internal int ResidentChunks => resident.Count;

    /// <summary>Redraws everything: after a restore, or a clear.</summary>
    internal void RedrawAll()
    {
        List<(Vector3 Low, Vector3 High)> bounds = [.. remnants.Remnants.Select(remnant => remnant.Bounds())];
        foreach ((long X, long Y, long Z) chunk in resident.ToArray())
        {
            Vector3 low = ChunkLow(chunk);
            bounds.Add((low, low + new Vector3(ChunkMetres)));
        }

        Redraw(bounds);
    }

    /// <summary>Redraws the chunks these world bounds touch: admitted where something stands, evicted where nothing does.</summary>
    internal void Redraw(IEnumerable<(Vector3 Low, Vector3 High)> changed)
    {
        HashSet<(long X, long Y, long Z)> chunks = [];
        float pad = PieceRemnant.VoxelMetres * 2;
        foreach ((Vector3 low, Vector3 high) in changed)
        {
            (long X, long Y, long Z) a = ChunkOf(low - new Vector3(pad)), b = ChunkOf(high + new Vector3(pad));
            for (long x = a.X; x <= b.X; x++)
                for (long y = a.Y; y <= b.Y; y++)
                    for (long z = a.Z; z <= b.Z; z++)
                        chunks.Add((x, y, z));
        }

        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> densities = [];
        foreach ((long X, long Y, long Z) chunk in chunks)
        {
            Vector3 low = ChunkLow(chunk), high = low + new Vector3(ChunkMetres);
            PieceRemnant[] touching = [.. remnants.Remnants.Where(remnant => Overlaps(remnant.Bounds(), low - new Vector3(pad), high + new Vector3(pad)))];
            VoxelChunkIdentity identity = new(chunk.X, chunk.Y, chunk.Z);
            if (touching.Length == 0)
            {
                if (resident.Remove(chunk)) operations.Add(new(VoxelResidencyOperationKind.Evict, identity, 0, 0));
                continue;
            }

            uint offset = (uint)materials.Count;
            bool any = Fill(chunk, touching, materials, densities);
            if (!any)
            {
                materials.RemoveRange((int)offset, materials.Count - (int)offset);
                densities.RemoveRange((int)offset, densities.Count - (int)offset);
                if (resident.Remove(chunk)) operations.Add(new(VoxelResidencyOperationKind.Evict, identity, 0, 0));
                continue;
            }

            operations.Add(new(resident.Add(chunk) ? VoxelResidencyOperationKind.Admit : VoxelResidencyOperationKind.Replace, identity,
                offset, TerrainConstants.ChunkVolume, offset, TerrainConstants.ChunkVolume));
        }

        if (operations.Count == 0) return;
        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, session,
            operations.ToArray(), materials.ToArray(), densities.ToArray()));
        if (projection is null && resident.Count > 0)
        {
            projection = engine.VoxelScenePresentation.ProjectSceneDirectional(new ProjectVoxelSceneDirectionalRequest(session,
                Slots.Values.Select(entry => new VoxelSceneMaterialBinding(BlockRegistry.Get(entry.Block).Slot, maps.Plain(entry.Map))).ToArray(),
                ReadOnlyMemory<VoxelSceneFaceMaterialBinding>.Empty));
        }
        else if (projection is not null)
        {
            engine.VoxelScenePresentation.RefreshScene(projection);
        }

        Align();
    }

    public void Dispose()
    {
        projection?.Dispose();
        projection = null;
        session.Dispose();
        maps.Dispose();
    }

    private static float ChunkMetres => TerrainConstants.ChunkEdgeLength * PieceRemnant.VoxelMetres;

    private static Vector3 ChunkLow((long X, long Y, long Z) chunk) => new(chunk.X * ChunkMetres, chunk.Y * ChunkMetres, chunk.Z * ChunkMetres);

    private static (long X, long Y, long Z) ChunkOf(Vector3 metres) =>
        ((long)MathF.Floor(metres.X / ChunkMetres), (long)MathF.Floor(metres.Y / ChunkMetres), (long)MathF.Floor(metres.Z / ChunkMetres));

    private static bool Overlaps((Vector3 Low, Vector3 High) a, Vector3 low, Vector3 high) =>
        a.Low.X <= high.X && a.High.X >= low.X && a.Low.Y <= high.Y && a.High.Y >= low.Y && a.Low.Z <= high.Z && a.High.Z >= low.Z;

    /// <summary>One chunk's materials and densities from the remnants touching it; whether anything is solid.</summary>
    private static bool Fill((long X, long Y, long Z) chunk, PieceRemnant[] touching, List<uint> materials, List<float> densities)
    {
        int edge = TerrainConstants.ChunkEdgeLength;
        bool any = false;
        for (int index = 0; index < TerrainConstants.ChunkVolume; index++)
        {
            long x = (chunk.X * edge) + (index % edge), y = (chunk.Y * edge) + (index / edge % edge), z = (chunk.Z * edge) + (index / (edge * edge));
            Vector3 centre = PieceRemnant.Centre(x, y, z);
            float best = float.MaxValue;
            PieceMaterial material = PieceMaterial.Planks;
            foreach (PieceRemnant remnant in touching)
            {
                float distance = remnant.Distance(centre);
                if (distance < best)
                {
                    best = distance;
                    material = remnant.Piece.Material;
                }
            }

            bool solid = best < 0;
            any |= solid;
            materials.Add(solid ? BlockRegistry.Get(Slots[material].Block).Slot : TerrainConstants.EmptyMaterial);
            float density = Math.Clamp(best / PieceRemnant.VoxelMetres, -FaceDensity, FaceDensity);
            densities.Add(solid ? Math.Min(density, -DensityFloor) : Math.Max(density, DensityFloor));
        }

        return any;
    }

    /// <summary>Keeps the session's origin with the walking frame's, as the far field does.</summary>
    private void Align()
    {
        (long X, long Y, long Z) origin = frame.Origin;
        if (origin == alignedOrigin) return;
        using WorldOriginPrepared prepared = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            session, origin.X, origin.Y, origin.Z, Array.Empty<WorldOriginEntityRow>()));
        engine.WorldOrigin.Commit(new WorldOriginCommitRequest(prepared));
        alignedOrigin = origin;
    }
}
