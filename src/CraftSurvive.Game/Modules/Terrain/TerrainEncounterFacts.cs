using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Describes a column of the generated world in the terms encounter placement
/// needs: how high the ground is, where the water line sits, whether there is
/// ground at all, and whether a creature that ended up in the water could reach a
/// shore from there.
///
/// This is the seam between the world and the encounter rules. The rules own
/// whether a creature may stand somewhere; this owns what the world actually
/// looks like, and it is the only place in the encounter path that knows about
/// terrain at all.
/// </summary>
internal sealed class TerrainEncounterFacts
{
    /// <summary>
    /// How far a creature may be from a shore and still be considered able to
    /// reach it. Deliberately modest: it is a reachability proxy, not a path.
    /// </summary>
    internal const int ShoreReachRadius = 16;

    private readonly TerrainRecipe recipe;

    internal TerrainEncounterFacts(TerrainRecipe recipe) =>
        this.recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));

    internal long WaterLevel => GenerationConstants.WaterLevel;

    /// <summary>Whether the column is inside the finite world.</summary>
    internal bool IsInside(long x, long z) =>
        x >= -recipe.Radius && x <= recipe.Radius && z >= -recipe.Radius && z <= recipe.Radius;

    /// <summary>
    /// Whether a land column (at or above the water line) lies within the reach
    /// radius, which is what lets a swimmer leave the water.
    /// </summary>
    internal bool IsShoreReachable(long x, long z)
    {
        for (long offsetX = -ShoreReachRadius; offsetX <= ShoreReachRadius; offsetX += 4)
        {
            for (long offsetZ = -ShoreReachRadius; offsetZ <= ShoreReachRadius; offsetZ += 4)
            {
                long candidateX = x + offsetX;
                long candidateZ = z + offsetZ;
                if (IsInside(candidateX, candidateZ) && recipe.SurfaceAt(candidateX, candidateZ) >= WaterLevel)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Describes the site at a column, or refuses a column outside the world.</summary>
    internal bool TryDescribe(RegionKind region, long regionId, long x, long z, out EncounterSite site)
    {
        if (!IsInside(x, z))
        {
            site = default;
            return false;
        }

        site = new EncounterSite(
            region,
            regionId,
            SurfaceY: recipe.SurfaceAt(x, z),
            WaterLevel,
            HasGround: true,
            ShoreIsReachable: IsShoreReachable(x, z));
        return true;
    }
}
