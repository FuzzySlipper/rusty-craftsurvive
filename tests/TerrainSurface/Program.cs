using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine;
using Rusty.Engine.Testing;
using EngineVoxelAddress = Rusty.Engine.VoxelAddress;

// Exercise the product's surface/material policy against the installed Engine, not a mesher fake.
const int Edge = TerrainConstants.ChunkEdgeLength;
const float BaseHeight = 3.3f;
const float Slope = 0.1f;
const float RayTop = 14f;
const float RayLength = 20f;
const float RayZ = 8.5f;
const float Tolerance = 0.002f;
const int WaterTop = 9;
const int EditX = 8, EditY = 4, EditZ = 8;
const float EditedRayX = EditX + 0.5f;
const int BuildY = 10;
float[] probes = [8.5f, 15.9f, 16.1f, 24.5f];

using EngineTestHost host = EngineTestHost.Create(new EngineTestHostOptions());
host.Call(engine =>
{
    using SpatialSession session = engine.Spatial.CreateSession(new SpatialSessionConfig(
        TerrainConstants.VoxelSize, TerrainConstants.VoxelChunkSize, VoxelSurfaceMode.DualContouring));
    TerrainSurfaces.Apply(engine, session);
    VoxelMaterialRules.Apply(engine, session);

    void Admit(long chunkX, bool water = false, bool cleared = false, bool aliases = false)
    {
        uint[] materials = new uint[TerrainConstants.ChunkVolume];
        float[] densities = new float[materials.Length];
        for (int z = 0; z < Edge; z++)
        for (int y = 0; y < Edge; y++)
        for (int x = 0; x < Edge; x++)
        {
            int index = (z * Edge + y) * Edge + x;
            double height = BaseHeight + (chunkX * Edge + x + 0.5f) * Slope;
            ushort original = y + 0.5 < height + 1 ? (ushort)BlockId.Stone
                : water && y < WaterTop ? (ushort)BlockId.Water : (ushort)BlockId.Air;
            if (aliases && original == (ushort)BlockId.Stone)
            {
                BlockId[] slots = [BlockId.Grass, BlockId.Dirt, BlockId.Stone, BlockId.Sand, BlockId.Snow, BlockId.Gravel];
                original = (ushort)slots[(chunkX * Edge + x) % slots.Length];
            }
            ushort material = cleared && chunkX == 0 && x == EditX && y == EditY && z == EditZ
                ? (ushort)BlockId.Air : original;
            materials[index] = material;
            densities[index] = TerrainDensity.At(y, height, original, material);
        }

        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(ReadOnlyMemory<uint>.Empty, session,
            new VoxelResidencyOperation[] { new(VoxelResidencyOperationKind.Admit, new(chunkX, 0, 0), 0, (uint)materials.Length, 0, (uint)densities.Length) },
            materials, densities));
    }

    SpatialHit Cast(float x) => engine.Spatial.CastRay(new SpatialRaycastRequest(session,
            new Vector3(x, RayTop, RayZ), -Vector3.UnitY, RayLength,
            new SpatialQueryFilter(uint.MaxValue, uint.MaxValue), ReadOnlyMemory<SpatialEntityCollider>.Empty,
            ReadOnlyMemory<ulong>.Empty, ReadOnlyMemory<SpatialEntityCollider>.Empty));

    float Height(float x)
    {
        SpatialHit hit = Cast(x);
        return hit.Present ? RayTop - (float)hit.Distance : float.NaN;
    }

    void CheckSlope(string label)
    {
        foreach (float x in probes)
        {
            float expected = BaseHeight + 1f + x * Slope;
            float actual = Height(x);
            Check.That(Math.Abs(expected - actual) < Tolerance, $"{label}: slope at {x} expected {expected}, got {actual}");
        }
    }

    Check.Section("DC collision across streamed chunks", () =>
    {
        Admit(0);
        Admit(1);
        CheckSlope("dry");
        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(session,
            new VoxelResidencyOperation[] { new(VoxelResidencyOperationKind.Evict, new(1, 0, 0), 0, 0) }, ReadOnlyMemory<uint>.Empty));
        Admit(1);
        CheckSlope("re-admitted");
        SpatialHit hit = Cast(EditedRayX);
        Check.Equal(SpatialHitKind.Voxel, hit.Kind, "DC collision keeps a voxel identity for picking");
        SpatialHit picked = engine.Spatial.PickVoxel(new SpatialPickRequest(session,
            new Vector3(EditedRayX, RayTop, RayZ), -Vector3.UnitY, RayLength,
            hit.VoxelX, hit.VoxelY, hit.VoxelZ, hit.Face));
        Check.That(picked.Present && picked.Kind == SpatialHitKind.Voxel, "the existing edit picker can target reconstructed ground");
    });

    Check.Section("saved material edit recreates live density", () =>
    {
        engine.Voxel.ApplyEdits(new VoxelEditTransaction(session,
            new VoxelEdit[] { new(VoxelEditKind.Clear, new EngineVoxelAddress(EditX, EditY, EditZ), 0) }));
        float edited = Height(EditedRayX);
        Check.That(edited < BaseHeight + 1 + EditedRayX * Slope - Tolerance, "clear changes the actual collision surface");
        Admit(0, cleared: true);
        Check.That(Math.Abs(edited - Height(EditedRayX)) < Tolerance, "overlay replay must reproduce the live edited surface");
    });

    Check.Section("DC ground under passable water", () =>
    {
        Admit(0, water: true);
        Admit(1, water: true);
        CheckSlope("under water");
    });

    Check.Section("construction remains grid aligned beside DC", () =>
    {
        engine.Voxel.ApplyEdits(new VoxelEditTransaction(session,
            new VoxelEdit[] { new(VoxelEditKind.Set, new EngineVoxelAddress(EditX, BuildY, EditZ), (uint)BlockId.Planks) }));
        Check.That(Math.Abs(Height(EditedRayX) - (BuildY + 1)) < Tolerance, "placed planks retain their exact cubic top");
        engine.Voxel.ApplyEdits(new VoxelEditTransaction(session,
            new VoxelEdit[] { new(VoxelEditKind.Clear, new EngineVoxelAddress(EditX, BuildY, EditZ), 0) }));
        CheckSlope("construction removed");
    });

    Check.Section("six physical slots share four visual layers", () =>
    {
        Admit(0, aliases: true);
        Admit(1, aliases: true);
        uint[] before = Enumerable.Range(0, Edge * 2).Select(x => engine.Voxel.Read(
            new VoxelReadRequest(session, new(x, 0, 0))).MaterialSlot).ToArray();
        ulong baselineCost = engine.Voxel.ReadScene(new(session)).MeshMicroseconds;
        foreach (uint width in new uint[] { 1, 2, 4 })
        {
            TerrainLayers.Configure(engine, session, width);
            CheckSlope($"layers width {width}");
            uint[] after = Enumerable.Range(0, Edge * 2).Select(x => engine.Voxel.Read(
                new VoxelReadRequest(session, new(x, 0, 0))).MaterialSlot).ToArray();
            Check.That(before.SequenceEqual(after) && after.Distinct().Count() == 6,
                "blending retains every physical material identity");
            Console.WriteLine($"terrain blend width={width} meshMicroseconds={engine.Voxel.ReadScene(new(session)).MeshMicroseconds} unblendedAdmissionMicroseconds={baselineCost}");
        }
        engine.Voxel.ApplyResidency(new VoxelResidencyTransaction(session,
            new VoxelResidencyOperation[] { new(VoxelResidencyOperationKind.Evict, new(1, 0, 0), 0, 0) }, ReadOnlyMemory<uint>.Empty));
        Admit(1, aliases: true);
        CheckSlope("blended re-admission");
        engine.Voxel.ApplyEdits(new VoxelEditTransaction(session,
            new VoxelEdit[] { new(VoxelEditKind.Clear, new EngineVoxelAddress(15, EditY, EditZ), 0) }));
        Check.Equal(0U, engine.Voxel.Read(new VoxelReadRequest(session, new(15, EditY, EditZ))).MaterialSlot,
            "an edit at a blended chunk border preserves the requested physical result");
        Admit(0, aliases: true);
        CheckSlope("blended overlay replay");
        using WorldOriginPrepared rebase = engine.WorldOrigin.Prepare(new WorldOriginPrepareRequest(
            session, Edge, 0, 0, ReadOnlyMemory<WorldOriginEntityRow>.Empty));
        engine.WorldOrigin.Commit(new WorldOriginCommitRequest(rebase));
        foreach (float x in probes)
            Check.That(Math.Abs(Height(x - Edge) - (BaseHeight + 1 + x * Slope)) < Tolerance,
                "blended terrain collision stays coherent after origin rebase");
    });
});

Check.Section("independent blend controls", () =>
{
    TerrainBlendSettings settings = TerrainBlendSettings.Parse("""
        {"transitionCells":4,"weightContrast":8,"textureScale":2,"projectionSharpnessScale":0.5}
        """);
    Check.That(settings == new TerrainBlendSettings(4, 8, 2, 0.5f), "width, contrast, texture scale and projection sharpness remain independent");
    foreach (string json in new[]
    {
        """{"transitionCells":0,"weightContrast":2,"textureScale":1,"projectionSharpnessScale":1}""",
        """{"transitionCells":5,"weightContrast":2,"textureScale":1,"projectionSharpnessScale":1}""",
        """{"transitionCells":2,"weightContrast":0.5,"textureScale":1,"projectionSharpnessScale":1}""",
        """{"transitionCells":2,"weightContrast":2,"textureScale":0,"projectionSharpnessScale":1}""",
        """{"transitionCells":2,"weightContrast":2,"textureScale":1,"projectionSharpnessScale":0}""",
    }) Check.Throws<InvalidDataException>(() => TerrainBlendSettings.Parse(json), "unsupported material tuning is refused");
    Check.That(TerrainLayers.Layer(BlockId.Dirt) == TerrainLayers.Layer(BlockId.Grass)
        && TerrainLayers.Layer(BlockId.Snow) == TerrainLayers.Layer(BlockId.Gravel)
        && TerrainLayers.Layer(BlockId.Water) == -1 && TerrainLayers.Layer(BlockId.Planks) == -1,
        "aliases preserve four visuals while leaving water and construction outside the blend");
});
return Check.Finish("TerrainSurface");
