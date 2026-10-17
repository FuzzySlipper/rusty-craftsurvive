using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>
/// The far field's ground through its real mapping (#10041 review R10041-1): FarGround.Natural decides a far cell's
/// ground, FarGroundBlend its four-layer set and channel. Dry meadow and snow must stay distinct textures, and no
/// vertex weighing dry meadow may draw with the lush set (whose fourth layer is frost).
/// </summary>
internal static class FarGroundBlendChecks
{
    private const int Reach = 2;
    private const int Side = 40;
    private const double CellMetres = FarGround.CellMetres, Gentle = 0, Height = 60;

    internal static void Run()
    {
        MapSample dry = new(Height, Temperature: 0.5, Moisture: 0.15, Rock: 0, Detail: 0.5);
        MapSample lush = new(Height, Temperature: 0.5, Moisture: 0.6, Rock: 0, Detail: 0.5);
        MapSample frozen = new(Height, Temperature: 0.05, Moisture: 0.5, Rock: 0, Detail: 0.5);
        BlockId dryGround = FarGround.Natural(dry, Gentle, Height, CellMetres);
        BlockId lushGround = FarGround.Natural(lush, Gentle, Height, CellMetres);
        BlockId snowGround = FarGround.Natural(frozen, Gentle, Height, CellMetres);
        Check.That(dryGround == BlockId.DryGrass && lushGround == BlockId.Grass && snowGround == BlockId.Snow,
            $"far ground by climate: dry {dryGround}, lush {lushGround}, frozen {snowGround}");

        Check.That(FarGroundBlend.DryLayers[FarGroundBlend.Channel(dryGround, drySet: true)] == "dry-meadow",
            "dry meadow far away draws the dry-meadow texture");
        Check.That(FarGroundBlend.LushLayers[FarGroundBlend.Channel(snowGround, drySet: false)] == "frost-stone",
            "snow far away draws frost-stone");
        Check.That(FarGroundBlend.LushLayers[FarGroundBlend.Channel(lushGround, false)] == "sage-ground"
            && FarGroundBlend.DryLayers[FarGroundBlend.Channel(lushGround, true)] == "sage-ground",
            "meadow draws sage-ground in either set");
        for (int channel = 0; channel < 3; channel++)
            Check.That(FarGroundBlend.LushLayers[channel] == FarGroundBlend.DryLayers[channel], "the two sets share their first three layers");

        // A meadow with a dry patch on one side and snow on the other: the vertices' sets and weights as the sampler
        // gives them.
        BlockId?[,] grid = new BlockId?[Side, Side];
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
            grid[x, z] = x < 12 ? snowGround : x >= 24 && z >= 10 && z < 30 ? dryGround : lushGround;
        BlockId? At(int x, int z) => x < 0 || z < 0 || x >= Side || z >= Side ? null : grid[x, z];
        bool[,] drySet = new bool[Side, Side];
        System.Numerics.Vector4[,] weights = new System.Numerics.Vector4[Side, Side];
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
        {
            int vx = x, vz = z;
            BlockId? Around(int dx, int dz) => At(vx + dx, vz + dz);
            drySet[x, z] = FarGroundBlend.UsesDrySet(Around, Reach);
            weights[x, z] = FarGroundBlend.Weights(Around, Reach, drySet[x, z]);
        }

        int dryWeighted = 0, snowWeighted = 0, seams = 0, wrongSet = 0;
        for (int z = 0; z < Side; z++)
        for (int x = 0; x < Side; x++)
        {
            bool weighsDry = drySet[x, z] && weights[x, z].W > 0;
            bool weighsSnow = !drySet[x, z] && weights[x, z].W > 0;
            if (weighsDry) dryWeighted++;
            if (weighsSnow) snowWeighted++;
            // A vertex weighing dry meadow, and every vertex a cell from it, draw the dry set.
            if (weighsDry)
                for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (At(x + dx, z + dz) is not null && !drySet[x + dx, z + dz]) wrongSet++;
            // Where the sets meet, the fourth layer carries no weight on either side: no seam.
            for (int dz = -1; dz <= 1; dz++)
            for (int dx = -1; dx <= 1; dx++)
                if (At(x + dx, z + dz) is not null && drySet[x + dx, z + dz] != drySet[x, z] && (weights[x, z].W > 0 || weights[x + dx, z + dz].W > 0))
                    seams++;
        }

        Check.That(dryWeighted > 0 && snowWeighted > 0, $"the patch weighs dry meadow ({dryWeighted}) and snow ({snowWeighted}) vertices");
        Check.That(wrongSet == 0, $"a vertex weighing dry meadow and its neighbours draw the dry set ({wrongSet} do not)");
        Check.That(seams == 0, $"where the lush and dry sets meet only shared layers carry weight ({seams} seams)");
        Console.WriteLine($"Far ground: dry meadow {dryWeighted} and snow {snowWeighted} vertices weighed in their own sets, no seams.");
    }
}
