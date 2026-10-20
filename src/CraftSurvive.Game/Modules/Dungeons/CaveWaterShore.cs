using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// Where a cave's still water is drawn (#10178): the half-metre columns of a lake's or pool's surface. The generator's open
/// surface (<see cref="WaterZone.SurfaceColumns"/>) stops where rock stands over the water and steps at every column,
/// so drawn alone it shows gaps and stairs where the water meets the rock. The drawn surface takes every column open at
/// the water's level within the zone and joined to its open surface - under an overhang too - and one more column into
/// the rock all round, so the rock's own face draws the shore. A rock column is left out where open air lies just
/// beyond it (a lip the water would spill over, a thin wall), so the water never hangs in the air past it.
/// </summary>
internal static class CaveWaterShore
{
    /// <summary>The side of each column: the cave field's step.</summary>
    internal const float Column = KarstCave.FieldStep;

    /// <summary>How far under the surface the cave's shape is read to tell water from rock at the water's level (metres).</summary>
    internal const float ProbeBelow = 0.05f;

    /// <summary>The most columns one surface may take for each of its open ones, so a mistaken zone cannot flood the field.</summary>
    private const int MostColumnsPerOpen = 4;

    /// <summary>The columns a zone's surface covers (see the class remarks), by index on the field's half-metre lattice.</summary>
    internal static HashSet<(int X, int Z)> Columns(WaterZone zone, Func<Vector3, float>? shape)
    {
        HashSet<(int X, int Z)> open = [.. zone.SurfaceColumns.Select(c => (Index(c.X), Index(c.Y)))];
        if (shape is null)
        {
            return open;
        }

        float probe = zone.Surface - ProbeBelow;
        bool Open((int X, int Z) c) => shape(At(c, probe)) > 0f;
        // The zone's cells are whole metres; the water's top row lies in the cell under the surface, or the one below it.
        bool InZone((int X, int Z) c) => zone.Contains(At(c, probe)) || zone.Contains(At(c, probe - 1f));

        HashSet<(int X, int Z)> water = [.. open];
        Queue<(int X, int Z)> frontier = new(open);
        int most = open.Count * MostColumnsPerOpen;
        while (frontier.TryDequeue(out (int X, int Z) c) && water.Count < most)
        {
            foreach ((int dx, int dz) in Around4)
            {
                (int X, int Z) n = (c.X + dx, c.Z + dz);
                if (!water.Contains(n) && InZone(n) && Open(n))
                {
                    water.Add(n);
                    frontier.Enqueue(n);
                }
            }
        }

        // One column into the rock all round, where the rock goes on past it rather than opening onto air.
        bool AirBeyond((int X, int Z) c) => Around4.Any(d => (c.X + d.Dx, c.Z + d.Dz) is var beyond && !water.Contains(beyond) && Open(beyond));
        HashSet<(int X, int Z)> shore = [.. water.SelectMany(c => Around8.Select(d => (c.X + d.Dx, c.Z + d.Dz)))
            .Where(n => !water.Contains(n) && !Open(n) && !AirBeyond(n))];

        water.UnionWith(shore);
        return water;
    }

    private static readonly (int Dx, int Dz)[] Around4 = [(1, 0), (-1, 0), (0, 1), (0, -1)];
    private static readonly (int Dx, int Dz)[] Around8 = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    internal static int Index(float metres) => (int)MathF.Floor(metres / Column);

    /// <summary>A column's centre at a height, in layout metres.</summary>
    internal static Vector3 At((int X, int Z) c, float y) => new((c.X + 0.5f) * Column, y, (c.Z + 0.5f) * Column);
}
