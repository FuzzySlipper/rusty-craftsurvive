using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// What a cave under a cave mouth is generated from (#10889), in one place (#11125) so the walking session and the authoring
/// tools' golden-seed export generate the same cave: the entrance's seed, the setting under it, and the box and switches a
/// cave under the world is generated with. Each is a pure function of the world and the mouth.
/// </summary>
internal static class UnderCaves
{
    private const ulong SeedBasis = 0x6A09_E667_F3BC_C908UL;
    private const ulong SeedPrime = 0x0000_0100_0000_01B3UL;

    /// <summary>An entrance's seed (a cave mouth's or a dungeon entrance's): the world's seed mixed with its anchor cell.</summary>
    internal static ulong Seed(ulong worldSeed, long cellX, long cellZ)
    {
        ulong hash = worldSeed ^ SeedBasis;
        foreach (long coordinate in (ReadOnlySpan<long>)[cellX, cellZ])
        {
            hash = unchecked((hash ^ (ulong)coordinate) * SeedPrime);
        }

        return hash;
    }

    /// <summary>The setting under a place of the world (#10304): the region's climate above it and the cave's depth.</summary>
    internal static CaveSetting SettingAt(TerrainRecipe recipe, double x, double z)
    {
        MapSample above = recipe.Geography(x, z);
        return CaveSetting.Under(above.Temperature, above.Moisture, above.Elevation / Math.Max(1d, recipe.Map.Scale.PeakElevation), above.Rock);
    }

    /// <summary>The box and switches of a cave under the world: the next caves' study switches, in the mouth's box, entered underground at the way in.</summary>
    internal static KarstCave.Settings Settings() => KarstCave.Next with
    {
        ChunksX = CavePlacement.BoxChunksX,
        ChunksY = CavePlacement.BoxChunksY,
        ChunksZ = CavePlacement.BoxChunksZ,
        Underground = true,
        WayIn = CavePlacement.WayIn,
    };

    /// <summary>The cave under a mouth, as the walking session asks for it before preparing it for the chunks.</summary>
    internal static DungeonCandidate Generate(ulong worldSeed, PoiSite mouth, CaveSetting setting) =>
        DungeonCandidates.Generate(DungeonApproach.Karst, Seed(worldSeed, mouth.CellX, mouth.CellZ), 0, 1d, null, setting, Settings());

    /// <summary>
    /// A generated cave made ready for the walking session (off the update): each voxel of its box as the overworld's chunks
    /// read it - its rock, mud and silt the cave's own blocks, the densities a natural cave is admitted with - and the
    /// walking session's chunks holding its air, with every chunk beside them; and the way down from the tendril's end past
    /// the first sink (<see cref="CaveWayDown"/>), the route starting with it. The authoring tools' cave view reads this too.
    /// </summary>
    internal static UndergroundCave Prepare(DungeonCandidate candidate, CavePlacement cave)
    {
        DungeonLayout all = candidate.AllVoxels;
        DungeonVolume volume = all.Volume;
        if (volume.SizeX != CavePlacement.SizeX || volume.SizeY != CavePlacement.SizeY || volume.SizeZ != CavePlacement.SizeZ)
        {
            throw new InvalidOperationException($"A cave under the world must fill its {CavePlacement.SizeX}x{CavePlacement.SizeY}x{CavePlacement.SizeZ} box.");
        }

        int edge = TerrainConstants.ChunkEdgeLength;
        ushort[] materials = new ushort[volume.SizeX * volume.SizeY * volume.SizeZ];
        float[] densities = new float[materials.Length];
        HashSet<TerrainChunkAddress> open = [];
        CaveInterior? interior = null;
        foreach ((int cx, int cy, int cz) in DungeonCollision.Chunks(volume))
        {
            (uint[] slots, float[]? values) = DungeonCollision.Prepare(volume, (cx, cy, cz), all.Densities, weathered: false, pinned: false, voxelSize: 1d);
            for (int index = 0; index < slots.Length; index++)
            {
                int i = (cx * edge) + (index % edge), j = (cy * edge) + (index / edge % edge), k = (cz * edge) + (index / (edge * edge));
                int at = (((k * volume.SizeY) + j) * volume.SizeX) + i;
                BlockId block = (BlockId)slots[index];
                materials[at] = (ushort)CaveMaterials.Underground(block);
                densities[at] = MathF.Abs(values?[index] ?? 0.5f);
                if (block == BlockId.Air)
                {
                    Vector3 world = cave.ToWorld(new Vector3(i + 0.5f, j + 0.5f, k + 0.5f));
                    open.Add(new VoxelAddress((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y), (long)MathF.Floor(world.Z)).Chunk);
                }
            }
        }

        // The way down from the tendril's end past the cave's first sink (R10889-3): a ramp the player walks back up, and the
        // route starting with it.
        // A planned box (#11219) is entered through its ports and its sinkhole's way in, which the generator made walkable.
        Vector3[] lead = cave.IsPlanned ? [] : [.. cave.Path.Select(cave.ToCanonical).Where(p => p.X >= 0f && p.X < volume.SizeX && p.Z >= 0f && p.Z < volume.SizeZ && p.Y >= 0f && p.Y < volume.SizeY)];
        CaveWayDown.Carved? down = cave.IsPlanned ? null : CaveWayDown.Carve(volume.SizeX, volume.SizeY, volume.SizeZ, materials, densities, lead.Length > 0 ? lead : [cave.ToCanonical(cave.Path[^1])],
            candidate.Layout.MainRoute, CavePlacement.TendrilHalfWidth, CavePlacement.TendrilHalfHeight, CavePlacement.TendrilFloorBelowCentre,
            TerrainConstants.EmptyMaterial, (ushort)BlockId.CaveLimestone);
        foreach (int at in down?.Opened ?? [])
        {
            int i = at % volume.SizeX, j = at / volume.SizeX % volume.SizeY, k = at / (volume.SizeX * volume.SizeY);
            Vector3 world = cave.ToWorld(new Vector3(i + 0.5f, j + 0.5f, k + 0.5f));
            open.Add(new VoxelAddress((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y), (long)MathF.Floor(world.Z)).Chunk);
        }

        interior = new CaveInterior(volume.SizeX, volume.SizeY, volume.SizeZ, materials, densities);
        // What dressing it reads: the layout without the voxels it was generated in.
        DungeonLayout dressing = candidate.Layout with
        {
            Volume = new DungeonVolume(1, 1, 1, BlockId.Air), Rock = null, Densities = null, KitDrawn = null, Moisture = null,
            MainRoute = down?.Route ?? candidate.Layout.MainRoute,
        };
        if (down is not null)
        {
            // What the way down cut through: the ruin's pieces in its way and the dressing left hanging in its air.
            dressing = CaveWayDown.Clear(dressing, down, CavePlacement.TendrilHalfWidth, CavePlacement.TendrilHalfHeight, CavePlacement.TendrilFloorBelowCentre);
        }
        return new UndergroundCave(dressing, interior, ChunkNeighbours.Beside(open));
    }
}
