using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// The far field's ground rules (<see cref="FarField"/>, #9850): how deep it is sunk under the near ground
/// about the player, what a far cell is made of, and where water stands over it. Pure, so the heightfield's
/// workers may use them.
/// </summary>
internal static class FarGround
{
    /// <summary>
    /// The drawn ground stands this far above the recipe's continuous height: the top face of its unrounded
    /// top cell, where the voxels' isosurface lies (TerrainDensity). The far field and the vegetation tiles
    /// both draw there, so they meet the near ground at its own height.
    /// </summary>
    internal const double GroundTopFace = 1d;

    /// <summary>One far cell, in metres: eight keeps a ridge's shape and a valley's floor at a kilometre.</summary>
    internal const double CellMetres = 8d;

    /// <summary>
    /// How far the far field is sunk under the near ground: fully within the inner distance of the player,
    /// rising to its true height by the outer one, short of the near ground's edge, so the join is under the
    /// fog and never a drop. Six metres covers a far cell's error against the metre ground.
    /// </summary>
    internal const double SinkMetres = 6d;

    /// <summary>The sunk zone ends this far inside the near ground's edge, and is fully sunk this far inside it.</summary>
    internal const double SinkOuterInsideEdgeMetres = 16d, SinkInnerInsideEdgeMetres = 64d;

    /// <summary>How far the player walks before the sunk zone is moved onto them again.</summary>
    internal const double SinkFollowStepMetres = 24d;

    /// <summary>Ground steeper than this, in metres of rise per metre, shows rock in the far field.</summary>
    private const double RockSlope = 0.7d;

    /// <summary>The sunk zone's inner and outer distances for a near ground reaching <paramref name="nearEdgeMetres"/>.</summary>
    internal static (double Inner, double Outer) SinkBand(double nearEdgeMetres)
    {
        double inner = Math.Max(0d, nearEdgeMetres - SinkInnerInsideEdgeMetres);
        return (inner, Math.Max(inner + 1d, nearEdgeMetres - SinkOuterInsideEdgeMetres));
    }

    /// <summary>How far the far field is sunk <paramref name="away"/> metres (Chebyshev) from the sunk zone's centre.</summary>
    internal static double Sink(double away, double inner, double outer)
    {
        double t = Math.Clamp((away - inner) / (outer - inner), 0d, 1d);
        return SinkMetres * (1d - (t * t * (3d - (2d * t))));
    }

    /// <summary>Where water stands at a place (<see cref="WaterRules"/>): the sea, a river's surface or a lake's level, unfloored (#9877).</summary>
    internal static double WaterTop(MapSample geography) => WaterRules.Surface(geography);

    /// <summary>
    /// A far cell's natural material: rock where the map says so or the ground is steep (<paramref name="rise"/>,
    /// the largest height change to a neighbouring cell), snow by climate, sand at the water or in deep desert, else
    /// meadow, sun-dried where the climate is dry (#10041).
    /// </summary>
    internal static BlockId Natural(MapSample geography, double rise, double height, double cellMetres)
    {
        if (geography.Rock >= TerrainRecipe.ExposedRockThreshold || rise / cellMetres > RockSlope) return BlockId.Stone;
        if (height <= GenerationConstants.WaterLevel + 1) return BlockId.Sand;
        if (WorldMap.Frozen(geography)) return BlockId.Snow;
        if (Soils.DeepDesert(geography)) return BlockId.Sand;
        return Soils.Dry(geography) ? BlockId.DryGrass : BlockId.Grass;
    }
}

/// <summary>
/// How the far field's ground blends (#10041 review). A heightfield weighs only the first four layers of a terrain
/// layer material, and the walking ground now has five, so the far field draws with two four-layer sets that share
/// their first three: lush (meadow, rock, sand, frost) and dry (meadow, rock, sand, dry meadow). A vertex takes the
/// dry set when dry meadow lies within one cell beyond its blend reach. Then every vertex carrying dry meadow's
/// weight stands among dry-set vertices, and where the sets meet only the shared layers carry weight, so the two
/// draw alike and leave no seam. Frost in a dry set (cold and dry rarely meet) weighs as rock.
/// </summary>
internal static class FarGroundBlend
{
    internal static readonly string[] LushLayers = ["sage-ground", "ochre-rock", "dune-sand", "frost-stone"];
    internal static readonly string[] DryLayers = ["sage-ground", "ochre-rock", "dune-sand", "dry-meadow"];

    /// <summary>The channel (0 to 3) a far ground block weighs into in a set.</summary>
    internal static int Channel(BlockId ground, bool drySet) => ground switch
    {
        BlockId.Stone => 1,
        BlockId.Sand => 2,
        BlockId.Snow or BlockId.Gravel => drySet ? 1 : 3,
        BlockId.DryGrass => drySet ? 3 : 0,
        // A fire's scar is too small to read from afar (#10296): it weighs as the meadow it burnt.
        BlockId.BurntGround => 0,
        _ => 0,
    };

    /// <summary>Whether a vertex takes the dry set: dry meadow within <paramref name="reach"/> + 1 cells (null for water or none).</summary>
    internal static bool UsesDrySet(Func<int, int, BlockId?> groundAt, int reach)
    {
        for (int dz = -(reach + 1); dz <= reach + 1; dz++)
        for (int dx = -(reach + 1); dx <= reach + 1; dx++)
            if (groundAt(dx, dz) == BlockId.DryGrass) return true;
        return false;
    }

    /// <summary>
    /// A vertex's layer weights in its set: each land cell within <paramref name="reach"/> weighs reach + 1 at the vertex,
    /// one less a cell outward (Chebyshev), as a voxel chunk's layers do. Zero when no land is near.
    /// </summary>
    internal static Vector4 Weights(Func<int, int, BlockId?> groundAt, int reach, bool drySet)
    {
        Vector4 weights = Vector4.Zero;
        for (int dz = -reach; dz <= reach; dz++)
        for (int dx = -reach; dx <= reach; dx++)
        {
            if (groundAt(dx, dz) is not BlockId ground) continue;
            float weight = reach + 1 - Math.Max(Math.Abs(dx), Math.Abs(dz));
            weights += Channel(ground, drySet) switch { 0 => Vector4.UnitX, 1 => Vector4.UnitY, 2 => Vector4.UnitZ, _ => Vector4.UnitW } * weight;
        }

        float total = weights.X + weights.Y + weights.Z + weights.W;
        return total > 0 ? weights / total : Vector4.Zero;
    }
}
