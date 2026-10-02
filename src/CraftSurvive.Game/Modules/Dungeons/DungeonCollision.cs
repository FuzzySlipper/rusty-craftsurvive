using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What a dungeon is made of to the Engine's spatial services: its own session, its voxels admitted
/// chunk by chunk, and a sculpted dungeon's rock meshed by dual contouring and admitted as static
/// collision. The loaded game and the offline route check build the space through these alone, so
/// the space the check walks is the space the player stands in.
/// </summary>
internal static class DungeonCollision
{
    /// <summary>Where a dungeon's volume begins, in its session: far below anything in the open world.</summary>
    internal static readonly Vector3 Origin = new(0f, -2048f, 0f);

    /// <summary>Samples written to the Engine's sampled volume per write.</summary>
    private const int RockWriteBatch = 65_536;

    /// <summary>How often the rock's texture repeats per metre of surface.</summary>
    private const float RockUvScale = 0.25f;

    /// <summary>Faces meeting at more than this angle keep a hard edge; gentler ones shade smoothly.</summary>
    private const float RockCreaseDegrees = 50f;

    /// <summary>The sculpted rock's texture, authored by scripts/generate-cave-rock.mjs.</summary>
    internal const string RockTextureContentPath = "textures/cave-rock.png";

    private const float RockRoughness = 0.92f;

    private const ulong RockAssetId = 1UL;
    private const ulong RockInstanceId = 1UL;

    /// <summary>Where a point of the layout stands in the session.</summary>
    internal static Vector3 InSession(Vector3 layoutPoint) => layoutPoint + Origin;

    /// <summary>A voxel's density beside its sign: how close to the surface an air or solid voxel may be held.</summary>
    private const float DensityFloor = 0.001f;

    /// <summary>A voxel's density without a sculpted one: the surface on the cube face between solid and empty.</summary>
    private const float FaceDensity = 0.5f;

    /// <summary>
    /// A dungeon's own session, with the world's blocks colliding as they do outside and its
    /// voxels surfaced as the look asks: cubes, or rock reconstructed beside grid-kept building.
    /// </summary>
    /// <param name="voxelSize">The side of a voxel in metres: the world's block unless a finer grid is being measured.</param>
    internal static SpatialSession CreateSession(IEngineContext engine, DungeonSurface surface = DungeonSurface.Cubes,
        double voxelSize = TerrainConstants.VoxelSize)
    {
        VoxelSurfaceMode mode = DungeonSurfaces.SessionMode(surface);
        SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
            voxelSize, TerrainConstants.VoxelChunkSize, mode));
        VoxelMaterialSurface[] materials = DungeonSurfaces.Materials(surface);
        if (materials.Length > 0)
        {
            engine.Voxel.ConfigureMaterialSurfaces(new VoxelMaterialSurfaceRequest(session, mode, materials));
        }

        engine.Voxel.ConfigureMaterialCollision(new VoxelMaterialCollisionRequest(
            session,
            BlockRegistry.MaterialBlocks.Select(block => new VoxelMaterialCollision((uint)block.Id, block.Collidable)).ToArray()));
        return session;
    }

    /// <summary>Every chunk of a volume, in the order a load admits them.</summary>
    internal static IEnumerable<(int X, int Y, int Z)> Chunks(DungeonVolume volume)
    {
        for (int z = 0; z < volume.ChunksZ; z++)
        {
            for (int y = 0; y < volume.ChunksY; y++)
            {
                for (int x = 0; x < volume.ChunksX; x++)
                {
                    yield return (x, y, z);
                }
            }
        }
    }

    /// <summary>
    /// Admits some of a volume's chunks into the session's voxels, with their densities when the
    /// layout has them: each solid voxel's density is negative and each empty one's positive, as the
    /// Engine requires, taken from the sculpted field where it agrees and from the cube face where not.
    /// </summary>
    /// <param name="firstChunk">The chunk the volume's first chunk is admitted as; by default the one at <see cref="Origin"/> in metre voxels.</param>
    internal static void Admit(IEngineContext engine, SpatialSession session, DungeonVolume volume, IEnumerable<(int X, int Y, int Z)> chunks,
        RockDensity? densities = null, bool weathered = false, (long X, long Y, long Z)? firstChunk = null)
    {
        (long X, long Y, long Z) first = firstChunk ?? (
            (long)Origin.X / TerrainConstants.ChunkEdgeLength,
            (long)Origin.Y / TerrainConstants.ChunkEdgeLength,
            (long)Origin.Z / TerrainConstants.ChunkEdgeLength);
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        List<float> chunkDensities = [];
        int edge = TerrainConstants.ChunkEdgeLength;
        foreach ((int x, int y, int z) in chunks)
        {
            uint offset = checked((uint)materials.Count);
            uint densityOffset = checked((uint)chunkDensities.Count);
            ushort[] chunkMaterials = volume.Chunk(x, y, z);
            for (int index = 0; index < chunkMaterials.Length; index++)
            {
                ushort material = chunkMaterials[index];
                materials.Add(material);
                if (densities is RockDensity field)
                {
                    int cx = (x * edge) + (index % edge);
                    int cy = (y * edge) + (index / edge % edge);
                    int cz = (z * edge) + (index / (edge * edge));
                    chunkDensities.Add(Density(volume, field, cx, cy, cz, (BlockId)material, weathered));
                }
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Admit,
                new VoxelChunkIdentity(x + first.X, y + first.Y, z + first.Z),
                offset,
                checked((uint)(materials.Count - (int)offset)),
                densityOffset,
                checked((uint)(chunkDensities.Count - (int)densityOffset))));
        }

        if (operations.Count > 0)
        {
            engine.Voxel.ApplyResidency(densities is null
                ? new VoxelResidencyTransaction(session, operations.ToArray(), materials.ToArray())
                : new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, session, operations.ToArray(), materials.ToArray(), chunkDensities.ToArray()));
        }
    }

    /// <summary>The sculpted rock's material: its texture, repeating, and a rough finish.</summary>
    internal static Material CreateRockMaterial(IEngineContext engine)
    {
        RenderResourceInfo texture = engine.Graphics.OpenResource(new RenderResourceRequest(RockTextureContentPath, TextureFilter.Linear, TextureWrap.Repeat));
        return engine.Graphics.CreateMaterial(new MaterialRequest(
            new Color(1f, 1f, 1f, 1f), texture.Handle, RockRoughness, new Color(1f, 1f, 1f, 1f), Vector3.Zero, 0f, false,
            MaterialAlphaMode.Opaque, 0f));
    }

    /// <summary>Meshes the rock's density by dual contouring through the Engine's sampled volume.</summary>
    internal static MeshResource MeshRock(IEngineContext engine, RockDensity rock, Material material, out ImplicitGenerationReadout readout)
    {
        using SampledVolume volume = engine.ImplicitSurfaces.CreateSampledVolume(new SampledVolumeCreateRequest(
            Origin + new Vector3(0.5f, 0.5f, 0.5f), RockDensity.Spacing, (uint)rock.Width, (uint)rock.Height, (uint)rock.Depth, 1f));
        DensitySample[] batch = new DensitySample[RockWriteBatch];
        for (int start = 0; start < rock.Values.Length; start += RockWriteBatch)
        {
            int count = Math.Min(RockWriteBatch, rock.Values.Length - start);
            for (int index = 0; index < count; index++)
            {
                batch[index] = new DensitySample(rock.Values[start + index]);
            }

            engine.ImplicitSurfaces.WriteSampledVolume(new SampledVolumeWriteRequest(volume, (uint)start, batch.AsMemory(0, count)));
        }

        using ImplicitField regions = engine.ImplicitSurfaces.CreateField();
        MeshResource mesh = engine.ImplicitSurfaces.GenerateSampledVolume(new SampledVolumeGenerateRequest(
            volume, regions, 0f, RockCreaseDegrees, RockUvScale, material, ReadOnlyMemory<ImplicitMaterialRegion>.Empty,
            ImplicitMaterialBoundaryMode.Centroid, 0f));
        readout = engine.ImplicitSurfaces.ReadSampledVolumeGeneration(volume);
        return mesh;
    }

    /// <summary>
    /// A voxel's density: negative when solid, positive when empty, as the Engine requires. Rock
    /// floors and ceilings - rock with empty space above or below - and the space against them sit
    /// exactly on the cell face, so treads stay flat, a one-block step stays one block and a
    /// passage keeps its full height; walls take the sculpted field, and building blocks keep the
    /// cube face.
    /// </summary>
    private static float Density(DungeonVolume volume, RockDensity field, int x, int y, int z, BlockId block, bool weathered)
    {
        bool rock = DungeonSurfaces.IsRock(block);
        bool air = block == BlockId.Air;
        bool floor = rock && volume.At(x, y + 1, z) == BlockId.Air;
        bool ceiling = rock && y > 0 && volume.At(x, y - 1, z) == BlockId.Air;
        bool overFloor = air && y > 0 && DungeonSurfaces.IsRock(volume.At(x, y - 1, z));
        bool underCeiling = air && DungeonSurfaces.IsRock(volume.At(x, y + 1, z));
        if (floor || ceiling || overFloor || underCeiling)
        {
            return block == BlockId.Air ? FaceDensity : -FaceDensity;
        }

        if (air)
        {
            // Beside building blocks the face stays where the block puts it; the rock's field, which
            // counts building weakly, would otherwise pull masonry out toward the rock.
            return BesideBuilding(volume, x, y, z) ? FaceDensity : Math.Clamp(field.At(x, y, z), DensityFloor, FaceDensity);
        }

        if (rock)
        {
            return Math.Clamp(field.At(x, y, z), -FaceDensity, -DensityFloor);
        }

        return weathered && DungeonSurfaces.Wears(block) ? -Weathered(volume, x, y, z) : -FaceDensity;
    }

    /// <summary>The most a weathered block's exposed surface is worn back, in density units (0.5 is none).</summary>
    private const float MaximumWear = 0.4f;

    /// <summary>How much more an exposed corner wears than a face, per further exposed side.</summary>
    private const float CornerWear = 0.12f;

    private static readonly (int X, int Y, int Z)[] Sides = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

    /// <summary>
    /// How solid a weathered building block reads: a block with no open side, or open only above or
    /// below, stays a full block; one open to a side is worn back by a fixed, per-block amount, more
    /// where several sides are open, so edges and corners round off and wall faces go uneven.
    /// </summary>
    private static float Weathered(DungeonVolume volume, int x, int y, int z)
    {
        int open = 0;
        bool sideOpen = false;
        foreach ((int dx, int dy, int dz) in Sides)
        {
            bool empty = volume.At(x + dx, y + dy, z + dz) == BlockId.Air;
            open += empty ? 1 : 0;
            sideOpen |= empty && dy == 0;
        }

        // Only what faces sideways wears - walls, wall tops, ledge edges. A block open only above or
        // below is a floor or a ceiling, kept whole so routes and headroom hold.
        if (!sideOpen)
        {
            return FaceDensity;
        }

        float wear = (MaximumWear * Wear(x, y, z)) + (CornerWear * (open - 1));
        return Math.Clamp(FaceDensity - wear, DensityFloor + DensityFloor, FaceDensity);
    }

    /// <summary>A block's wear in [0, 1]: a fixed hash of its cell, squared so most blocks wear little.</summary>
    private static float Wear(int x, int y, int z)
    {
        ulong h = ((ulong)(uint)x * 0x9E37_79B1UL) ^ ((ulong)(uint)y * 0x85EB_CA77UL << 17) ^ ((ulong)(uint)z * 0xC2B2_AE3DUL << 31);
        h ^= h >> 33;
        h *= 0xFF51_AFD7_ED55_8CCDUL;
        h ^= h >> 33;
        float unit = (h >> 40) / (float)(1UL << 24);
        return unit * unit;
    }

    /// <summary>Whether an empty cell touches a building block on any side.</summary>
    private static bool BesideBuilding(DungeonVolume volume, int x, int y, int z)
    {
        foreach ((int dx, int dy, int dz) in Sides)
        {
            BlockId side = volume.At(x + dx, y + dy, z + dz);
            if (side != BlockId.Air && !DungeonSurfaces.IsRock(side) && volume.Contains(x + dx, y + dy, z + dz))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Admits the rock mesh as the session's static collision.</summary>
    internal static void AdmitRock(IEngineContext engine, SpatialSession session, MeshResource mesh) =>
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            session,
            new[] { new StaticMeshAsset(RockAssetId, new MeshResourceReference(mesh), 0, 0, 0, 0) },
            ReadOnlyMemory<Vector3>.Empty,
            ReadOnlyMemory<Triangle>.Empty,
            new[] { new StaticMeshInstance(RockInstanceId, RockAssetId, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One)) }));
}
