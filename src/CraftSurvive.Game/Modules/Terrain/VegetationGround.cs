using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// What the generated ground offers vegetation at a lattice of columns, without fine voxels (#10016):
/// the height and the top block the near ground's rules would give the column. Pure
/// and safe on workers, as the far field's sampler is: it reads only the recipe's height, geography
/// and towns, never the memo tables of trees, sites or crossings. Placements still belong to the
/// Engine's scatter; each kind grows on the blocks it names, as the voxel scatter's do.
/// </summary>
internal sealed class VegetationGround(TerrainRecipe recipe)
{
    /// <summary>
    /// Fills a square lattice of <paramref name="count"/> columns a side from (<paramref name="x0"/>,
    /// <paramref name="z0"/>), <paramref name="step"/> metres apart: the ground's height and its top block's
    /// registry slot. Outside the world the height is NaN and the block air.
    /// The near ground's slope rule is read from the lattice's own neighbours, so a one-metre lattice
    /// agrees with the voxels and a coarser one approximates them; its outermost ring has no slope and
    /// is meant as the caller's border.
    /// </summary>
    internal void Fill(double x0, double z0, double step, int count, Span<float> heights, Span<byte> slots)
    {
        int total = count * count;
        if (heights.Length < total || slots.Length < total)
            throw new ArgumentException("Each lattice span must hold count * count columns.");

        long[] surface = new long[total];
        long[] waterTop = new long[total];
        MapSample[] geography = new MapSample[total];
        for (int j = 0; j < count; j++)
        for (int i = 0; i < count; i++)
        {
            int at = (j * count) + i;
            long x = (long)Math.Floor(x0 + (i * step)), z = (long)Math.Floor(z0 + (j * step));
            if (Math.Abs(x) > recipe.Radius || Math.Abs(z) > recipe.Radius)
            {
                heights[at] = float.NaN;
                slots[at] = Slot(BlockId.Air);
                continue;
            }

            MapSample here = recipe.Geography(x, z);
            double height = recipe.ContinuousHeightAt(x, z);
            geography[at] = here;
            surface[at] = (long)Math.Round(height, MidpointRounding.AwayFromZero);
            waterTop[at] = (long)Math.Ceiling(WaterRules.Surface(here)) - 1;
            heights[at] = (float)(height + FarGround.GroundTopFace);
        }

        for (int j = 0; j < count; j++)
        for (int i = 0; i < count; i++)
        {
            int at = (j * count) + i;
            if (float.IsNaN(heights[at])) continue;
            bool edge = i == 0 || j == 0 || i == count - 1 || j == count - 1;
            // Rise per lattice step, scaled to metres per metre so a coarse lattice keeps the voxel rule's meaning.
            double slope = edge ? 0 : Math.Max(
                Math.Max(Math.Abs(surface[at] - surface[at - 1]), Math.Abs(surface[at] - surface[at + 1])),
                Math.Max(Math.Abs(surface[at] - surface[at - count]), Math.Abs(surface[at] - surface[at + count]))) / step;
            long x = (long)Math.Floor(x0 + (i * step)), z = (long)Math.Floor(z0 + (j * step));
            slots[at] = Slot(Top(geography[at], surface[at], waterTop[at], slope, x, z));
        }
    }

    /// <summary>
    /// One column's ground: the drawn height (the top face), its top block and the map sample, with the
    /// slope from its four neighbours, as <see cref="Fill"/> reads them on a metre lattice. Null outside the
    /// world.
    /// </summary>
    internal (double Height, BlockId Top, MapSample Geography)? At(long x, long z)
    {
        if (Math.Abs(x) > recipe.Radius || Math.Abs(z) > recipe.Radius) return null;
        MapSample here = recipe.Geography(x, z);
        double height = recipe.ContinuousHeightAt(x, z);
        long surface = Round(height);
        long slope = Math.Max(
            Math.Max(Math.Abs(surface - Round(recipe.ContinuousHeightAt(x - 1, z))), Math.Abs(surface - Round(recipe.ContinuousHeightAt(x + 1, z)))),
            Math.Max(Math.Abs(surface - Round(recipe.ContinuousHeightAt(x, z - 1))), Math.Abs(surface - Round(recipe.ContinuousHeightAt(x, z + 1)))));
        long waterTop = (long)Math.Ceiling(WaterRules.Surface(here)) - 1;
        return (height + FarGround.GroundTopFace, Top(here, surface, waterTop, slope, x, z), here);
    }

    private static long Round(double height) => (long)Math.Round(height, MidpointRounding.AwayFromZero);

    /// <summary>The top block of a column by the near ground's rules (TerrainRecipe.NaturalMaterialAt), without its structures.</summary>
    private BlockId Top(MapSample geography, long surface, long waterTop, double slope, long x, long z)
    {
        if (recipe.InTownYard(x, z)) return BlockId.Dirt;
        if (geography.Rock >= TerrainRecipe.ExposedRockThreshold) return BlockId.Stone;
        if (surface <= waterTop) return BlockId.Sand;
        if (slope <= GenerationConstants.TopsoilSlopeMaximum)
        {
            if (WorldMap.Frozen(geography)) return BlockId.Snow;
            if (WorldMap.Arid(geography)) return BlockId.Sand;
            // Under a closed canopy the topsoil is forest floor (#10032), as the voxels say.
            return recipe.Forest.Canopy(x, z, geography) >= Forest.FloorFromCanopy ? BlockId.Dirt : BlockId.Grass;
        }

        return slope <= GenerationConstants.SubsoilSlopeMaximum ? BlockId.Dirt : BlockId.Stone;
    }

    private static byte Slot(BlockId block) => (byte)BlockRegistry.Get(block).Slot;
}
