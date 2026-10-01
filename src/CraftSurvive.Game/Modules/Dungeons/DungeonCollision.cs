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

    /// <summary>A dungeon's own session, with the world's blocks colliding as they do outside.</summary>
    internal static SpatialSession CreateSession(IEngineContext engine)
    {
        SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
            TerrainConstants.VoxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.GreedyCubes));
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

    /// <summary>Admits some of a volume's chunks into the session's voxels.</summary>
    internal static void Admit(IEngineContext engine, SpatialSession session, DungeonVolume volume, IEnumerable<(int X, int Y, int Z)> chunks)
    {
        List<VoxelResidencyOperation> operations = [];
        List<uint> materials = [];
        foreach ((int x, int y, int z) in chunks)
        {
            uint offset = checked((uint)materials.Count);
            foreach (ushort material in volume.Chunk(x, y, z))
            {
                materials.Add(material);
            }

            operations.Add(new VoxelResidencyOperation(
                VoxelResidencyOperationKind.Admit,
                new VoxelChunkIdentity(
                    x + ((long)Origin.X / TerrainConstants.ChunkEdgeLength),
                    y + ((long)Origin.Y / TerrainConstants.ChunkEdgeLength),
                    z + ((long)Origin.Z / TerrainConstants.ChunkEdgeLength)),
                offset,
                checked((uint)(materials.Count - (int)offset))));
        }

        if (operations.Count > 0)
        {
            engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(session, operations.ToArray(), materials.ToArray()));
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

    /// <summary>Admits the rock mesh as the session's static collision.</summary>
    internal static void AdmitRock(IEngineContext engine, SpatialSession session, MeshResource mesh) =>
        engine.Spatial.ReplaceCollision(new CollisionReplaceRequest(
            session,
            new[] { new StaticMeshAsset(RockAssetId, new MeshResourceReference(mesh), 0, 0, 0, 0) },
            ReadOnlyMemory<Vector3>.Empty,
            ReadOnlyMemory<Triangle>.Empty,
            new[] { new StaticMeshInstance(RockInstanceId, RockAssetId, new Transform(Vector3.Zero, Quaternion.Identity, Vector3.One)) }));
}
