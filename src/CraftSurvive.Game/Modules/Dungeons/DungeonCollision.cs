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
    internal static SpatialSession CreateSession(IEngineContext engine, DungeonSurface surface = DungeonSurface.Cubes)
    {
        VoxelSurfaceMode mode = DungeonSurfaces.SessionMode(surface);
        SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
            TerrainConstants.VoxelSize, TerrainConstants.VoxelChunkSize, mode));
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
    internal static void Admit(IEngineContext engine, SpatialSession session, DungeonVolume volume, IEnumerable<(int X, int Y, int Z)> chunks,
        RockDensity? densities = null)
    {
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
                    chunkDensities.Add(Density(volume, field, cx, cy, cz, (BlockId)material));
                }
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Admit,
                new VoxelChunkIdentity(
                    x + ((long)Origin.X / TerrainConstants.ChunkEdgeLength),
                    y + ((long)Origin.Y / TerrainConstants.ChunkEdgeLength),
                    z + ((long)Origin.Z / TerrainConstants.ChunkEdgeLength)),
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
    private static float Density(DungeonVolume volume, RockDensity field, int x, int y, int z, BlockId block)
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

        float sculpted = field.At(x, y, z);
        return block == BlockId.Air
            ? Math.Clamp(sculpted, DensityFloor, FaceDensity)
            : Math.Clamp(rock ? sculpted : -FaceDensity, -FaceDensity, -DensityFloor);
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
