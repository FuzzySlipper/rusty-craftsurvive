using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A built dungeon's water marks (#10092), as projected decals from the shared decal atlas (textures/decals/atlas.png):
/// under every seep a drip stain on the floor, down the nearest wall beside it a streak (a dark wet run, or a pale calcite
/// one where the water has been at it longest), and at the feet of the wettest walls a damp wash. Where each lies is
/// worked out once from the layout; the space places and removes them.
/// </summary>
internal static class DungeonDrips
{
    /// <summary>A mark: its box's centre (layout metres), its turn (box +y the surface's outward normal, +z down the atlas region), its size, and its atlas region.</summary>
    internal readonly record struct Mark(Vector3 Centre, Quaternion Turn, Vector3 Size, Vector4 Region);

    // The atlas (decal_atlas.py): 256 x 128 texels. Wall marks 32 x 96 from x 128 (two dark, two calcite), floor stains
    // 32 x 32 from x 128 at y 96, damp washes 64 x 64 from y 64.
    private const float AtlasWidth = 256f, AtlasHeight = 128f;

    // Sizes (metres; 32 texels a metre): a wall mark 1 across and 3 down, a floor stain 1 across, a wash 2; how deep a box
    // projects through the surface.
    private const float StreakWidth = 1f, StreakLength = 3f, StainSize = 1f, WashSize = 2f, Depth = 0.6f;

    /// <summary>A wall mark's box projects this deep: a kit wall's curved face stands up to half a metre behind the voxels' edge.</summary>
    private const float WallDepth = 1.2f;

    // How far from a seep a wall may stand and take its streak; the share of streaks that are calcite; the dampness a
    // wall's foot must reach for a wash, and the share of such walls that take one.
    private const float WallReach = 1.6f;
    private const double CalciteShare = 0.35, WashShare = 0.5;
    private const float WashWet = 0.62f;

    internal static IReadOnlyList<Mark> Marks(DungeonVolume volume, DungeonMoisture moisture, IReadOnlyList<KitPlacement> kit, ulong seed)
    {
        List<Mark> marks = [];
        int index = 0;
        foreach (Vector3 seep in moisture.SeepPoints)
        {
            index++;
            double pick = Hash(seed, index);
            // The floor under it: down from the ceiling to the first solid voxel.
            int x = (int)MathF.Floor(seep.X), z = (int)MathF.Floor(seep.Z);
            int y = (int)MathF.Floor(seep.Y) - 1;
            while (y > 0 && volume.At(x, y, z) == BlockId.Air)
            {
                y--;
            }

            if (y > 0)
            {
                Vector3 floor = new(x + 0.5f, y + 1f, z + 0.5f);
                marks.Add(new Mark(floor, Quaternion.CreateFromAxisAngle(Vector3.UnitY, (float)(pick * MathF.Tau)), new Vector3(StainSize, Depth, StainSize),
                    Region(128 + (32 * (int)(pick * 4)), 96, 32, 32)));
            }

            // The nearest wall beside it, under the ceiling: its streak runs down from there.
            (Vector3 face, Vector3 normal)? wall = null;
            float nearest = WallReach;
            foreach (Vector3 dir in new[] { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitZ })
            {
                for (float d = 0.1f; d <= WallReach; d += 0.1f)
                {
                    Vector3 p = seep + (dir * d) - new Vector3(0f, 0.5f, 0f);
                    if (volume.At((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z)) != BlockId.Air)
                    {
                        if (d < nearest)
                        {
                            nearest = d;
                            wall = (seep + (dir * d), -dir);
                        }

                        break;
                    }
                }
            }

            if (wall is (Vector3 at, Vector3 n))
            {
                bool calcite = Hash(seed, index + 7919) < CalciteShare;
                int column = (calcite ? 2 : 0) + (int)(Hash(seed, index + 104729) * 2);
                Vector3 centre = at - new Vector3(0f, StreakLength / 2f, 0f);
                marks.Add(new Mark(centre, OnWall(n), new Vector3(StreakWidth, WallDepth, StreakLength), Region(128 + (32 * column), 0, 32, 96)));
            }
        }

        // A damp wash at the foot of the wettest walls.
        foreach (KitPlacement p in kit.Where(p => p.Piece is KitPiece.Wall or KitPiece.WallAlcove or KitPiece.WallRoundel or KitPiece.WallTall && p.Wet >= WashWet))
        {
            index++;
            if (Hash(seed, index) > WashShare)
            {
                continue;
            }

            Vector3 n = DungeonKit.Turned(p, Vector3.UnitZ);
            Vector3 foot = DungeonKit.Local(p, new Vector3((float)(Hash(seed, index + 31) - 0.5) * 2f, 1f + (WashSize / 2f), -3.1f));
            marks.Add(new Mark(foot + (n * 0.2f), OnWall(n), new Vector3(WashSize, WallDepth, WashSize), Region(64 * (int)(Hash(seed, index + 17) * 2), 64, 64, 64)));
        }

        return marks;
    }

    /// <summary>A box on a wall facing <paramref name="normal"/>: its +y out of the wall, its +z (the atlas region's v) down it.</summary>
    private static Quaternion OnWall(Vector3 normal)
    {
        Vector3 y = Vector3.Normalize(normal), z = -Vector3.UnitY, x = Vector3.Cross(y, z);
        Matrix4x4 basis = new(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f);
        return Quaternion.CreateFromRotationMatrix(basis);
    }

    private static Vector4 Region(int x, int y, int w, int h) => new(x / AtlasWidth, y / AtlasHeight, (x + w) / AtlasWidth, (y + h) / AtlasHeight);

    private static double Hash(ulong seed, int i)
    {
        ulong h = seed ^ 0x44726970UL ^ ((ulong)(uint)i * 0x9E3779B97F4A7C15UL);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h >> 11) / (double)(1UL << 53);
    }
}
