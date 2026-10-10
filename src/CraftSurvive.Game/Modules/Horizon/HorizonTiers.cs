using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Horizon;

/// <summary>
/// The pure rules of a continent's horizon tiers (#9822, R9822-1): where the coarser tiers sink beneath the
/// region window, and which middle-tier columns a change to that window can touch, so every one whose
/// surface depends on it is sampled again.
/// </summary>
internal static class HorizonTiers
{
    /// <summary>The region window's 32 m chunk column (512 m) and the band inside its edge over which it eases onto the tier beneath.</summary>
    internal const double RegionChunkMetres = TerrainConstants.ChunkEdgeLength * 32.0;
    internal const int RegionFadeChunks = 2;
    internal const double RegionFadeMetres = RegionFadeChunks * RegionChunkMetres;

    /// <summary>The middle tier's 250 m chunk column (4 km).</summary>
    internal const double MidChunkMetres = TerrainConstants.ChunkEdgeLength * 250.0;

    /// <summary>
    /// Whether a point lies beneath the region window's ground: on a region column whose tiles are ready
    /// (<paramref name="covered"/>), inside the window past its fade band. Tiers beneath sink there.
    /// </summary>
    internal static bool UnderRegion(IReadOnlySet<(long X, long Z)> covered, Vector2 minimum, Vector2 maximum, double worldX, double worldZ)
    {
        if (!covered.Contains(((long)Math.Floor(worldX / RegionChunkMetres), (long)Math.Floor(worldZ / RegionChunkMetres)))) return false;
        double inset = Math.Min(Math.Min(worldX - minimum.X, maximum.X - worldX), Math.Min(worldZ - minimum.Y, maximum.Y - worldZ));
        return inset >= RegionFadeMetres;
    }

    /// <summary>
    /// The middle-tier columns a region window can sink: every one overlapping it. When the window moves or
    /// its coverage changes, those of the old window and the new are sampled again; no point outside both
    /// windows is ever beneath the region.
    /// </summary>
    internal static IEnumerable<(long X, long Z)> MidColumnsUnder(Vector2 minimum, Vector2 maximum)
    {
        if (maximum.X <= minimum.X || maximum.Y <= minimum.Y) yield break;
        long x0 = (long)Math.Floor(minimum.X / MidChunkMetres), x1 = (long)Math.Floor((maximum.X - 1e-3) / MidChunkMetres);
        long z0 = (long)Math.Floor(minimum.Y / MidChunkMetres), z1 = (long)Math.Floor((maximum.Y - 1e-3) / MidChunkMetres);
        for (long z = z0; z <= z1; z++)
        for (long x = x0; x <= x1; x++)
            yield return (x, z);
    }
}
