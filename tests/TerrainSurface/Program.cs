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

    void Admit(long chunkX, bool water = false, bool cleared = false)
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
});
return Check.Finish("TerrainSurface");
