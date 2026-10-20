using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>What a cave's flooded place is in play (#10094; owner, 2026-10-20: water in caves is mood and variety, rarely traversal).</summary>
internal enum WaterZoneKind
{
    /// <summary>A wide body of still water with one open surface: real water.</summary>
    Lake,

    /// <summary>A small, shallow body of still water: real water.</summary>
    Pool,

    /// <summary>
    /// A flooded passage joining two parts of the cave (its surface broken into two or more mouths): where a cave diver
    /// would dive through, the cave holds a heavy mist instead of water.
    /// </summary>
    MistSump,
}

/// <summary>
/// One flooded place of a cave, as the level data every later pass reads (enemy placement and respawning, lighting, sound,
/// route checks), in layout metres: its kind, the height of its water's surface, the 1 m cells it fills, the columns of
/// its open surface (each mouth for a sump), and for a sump a path through it from one mouth to the next.
/// </summary>
internal sealed record WaterZone(WaterZoneKind Kind, float Surface, IReadOnlySet<int> Cells, IReadOnlyList<Vector2> SurfaceColumns,
    IReadOnlyList<Vector3> Mouths, IReadOnlyList<Vector3> Path)
{
    /// <summary>The 1 m cell key of a place (layouts span at most 1024 m on a side).</summary>
    internal static int CellKey(Vector3 p) => (int)MathF.Floor(p.X) + ((int)MathF.Floor(p.Y) * CellSpan) + ((int)MathF.Floor(p.Z) * CellSpan * CellSpan);

    private const int CellSpan = 1024;

    /// <summary>The lowest 1 m cell of the zone in each column (its bed there), found once when first asked for.</summary>
    private Dictionary<(int X, int Z), int>? beds;

    /// <summary>The zone's bed (the bottom of its lowest cell) under a place in layout metres, or null where the zone is not.</summary>
    internal float? BedUnder(Vector3 p)
    {
        if (beds is null)
        {
            beds = [];
            foreach (int key in Cells)
            {
                int x = key % CellSpan, y = key / CellSpan % CellSpan, z = key / (CellSpan * CellSpan);
                beds[(x, z)] = beds.TryGetValue((x, z), out int lowest) ? Math.Min(lowest, y) : y;
            }
        }

        return beds.TryGetValue(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Z)), out int bed) ? bed : null;
    }

    /// <summary>Whether a place in layout metres lies in the zone.</summary>
    internal bool Contains(Vector3 p) => p.X >= 0f && p.Y >= 0f && p.Z >= 0f && Cells.Contains(CellKey(p));
}
