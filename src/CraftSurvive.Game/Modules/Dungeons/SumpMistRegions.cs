using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A box of a sump's mist that the Engine moves (#10993, on rusty-engine #10988's fog regions), in layout metres: its
/// centre, half extents turned <paramref name="YawDegrees"/> about the vertical, its cells' size, how thick it is at rest,
/// its own light, and its current along the passage (its share, in passage lengths a second, as a volume's).
/// </summary>
internal readonly record struct MistRegion(Vector3 Centre, Vector3 HalfExtents, float YawDegrees, float CellMetres, float Density, Vector3 Glow, Vector3 Current)
{
    /// <summary>How many cells cover it.</summary>
    internal int Cells => SumpMistRegions.CellsAlong(HalfExtents.X, CellMetres) * SumpMistRegions.CellsAlong(HalfExtents.Y, CellMetres) * SumpMistRegions.CellsAlong(HalfExtents.Z, CellMetres);

    /// <summary>Whether a point lies in the box.</summary>
    internal bool Contains(Vector3 point)
    {
        Vector3 offset = point - Centre;
        float turn = -YawDegrees * MathF.PI / 180f;
        Vector3 local = new((offset.X * MathF.Cos(turn)) + (offset.Z * MathF.Sin(turn)), offset.Y, (-offset.X * MathF.Sin(turn)) + (offset.Z * MathF.Cos(turn)));
        return MathF.Abs(local.X) <= HalfExtents.X && MathF.Abs(local.Y) <= HalfExtents.Y && MathF.Abs(local.Z) <= HalfExtents.Z;
    }
}

/// <summary>
/// Fits a sump's mist into the Engine's fog regions (#10993), Engine-free: the volumes a sump's mist is laid in along its
/// passage (KarstCave's draws) are taken a straight run at a time, and each run becomes one box covering them, at their
/// mean thickness and current. A fog stirrer then parts and clears it where a body passes, which added volumes never could.
/// The Engine's limits bound it; volumes no region covers stay volumes.
/// </summary>
internal static class SumpMistRegions
{
    /// <summary>The Engine's limits (rusty-engine #10988): regions in a scene, cells along an axis, in a region and in all.</summary>
    internal const int EngineMostRegions = 8, EngineMostCellsPerAxis = 128, EngineMostCellsPerRegion = 131_072, EngineMostCellsInScene = 262_144;

    /// <summary>A run is at most this many volumes, and breaks where the way turns more than this from its start.</summary>
    internal const int MostVolumesPerRun = 4;

    internal const float MostBendDegrees = 30f;

    /// <summary>
    /// How much of its volumes' mean half extents a box takes across, up and beyond its ends: an ellipsoid's heart is
    /// thick and its rim thin, so the box keeps to about where the mist read.
    /// </summary>
    internal const float AcrossShare = 0.8f, UpShare = 0.65f, BeyondShare = 0.7f;

    /// <summary>Cells a quarter metre across, coarser where a box would pass the Engine's limits.</summary>
    internal const float CellMetres = 0.25f;

    /// <summary>
    /// Fits regions over one sump's volumes, given in order along its passage (its mouths left out), using at most
    /// <paramref name="regionsLeft"/> regions and <paramref name="cellsLeft"/> cells. Returns the regions and how many of
    /// the volumes, from the first, they cover: the rest stay volumes.
    /// </summary>
    internal static (List<MistRegion> Regions, int Covered) Fit(IReadOnlyList<MistVolume> along, int regionsLeft, int cellsLeft)
    {
        ArgumentNullException.ThrowIfNull(along);
        List<MistRegion> regions = [];
        int covered = 0;
        while (covered < along.Count && regions.Count < regionsLeft)
        {
            int end = RunEnd(along, covered);
            MistRegion region = Cover(along, covered, end);
            if (region.Cells > cellsLeft) break;
            regions.Add(region);
            cellsLeft -= region.Cells;
            covered = end;
        }

        return (regions, covered);
    }

    /// <summary>
    /// Where the straight run from <paramref name="start"/> ends (exclusive). It also ends before a volume its box would leave
    /// a heart outside (#11170): a box turns only about the vertical, so a passage that rises or bends along the run breaks it.
    /// </summary>
    private static int RunEnd(IReadOnlyList<MistVolume> along, int start)
    {
        int end = start + 1;
        while (end < along.Count && end - start < MostVolumesPerRun)
        {
            Vector3 first = Flat(along[start + 1 < along.Count ? start + 1 : start].Centre - along[start].Centre);
            Vector3 next = Flat(along[end].Centre - along[end - 1].Centre);
            if (first != Vector3.Zero && next != Vector3.Zero && Vector3.Dot(first, next) < MathF.Cos(MostBendDegrees * MathF.PI / 180f)) break;
            MistRegion wider = Cover(along, start, end + 1);
            bool holds = true;
            for (int i = start; i <= end && holds; i++)
            {
                holds = wider.Contains(along[i].Centre);
            }

            if (!holds) break;
            end++;
        }

        return end;
    }

    /// <summary>The box over volumes [start, end): from the first's centre to the last's, as wide and tall as they are.</summary>
    private static MistRegion Cover(IReadOnlyList<MistVolume> along, int start, int end)
    {
        int count = end - start;
        MistVolume first = along[start], last = along[end - 1];
        Vector3 way = Flat(last.Centre - first.Centre);
        float yaw = way == Vector3.Zero ? first.YawDegrees : MathF.Atan2(way.X, way.Z) * 180f / MathF.PI;
        Vector3 half = Vector3.Zero, current = Vector3.Zero;
        float density = 0f, height = 0f;
        for (int i = start; i < end; i++)
        {
            half += along[i].HalfExtents;
            current += along[i].Current;
            density += along[i].Density;
            height += along[i].Centre.Y;
        }

        half /= count;
        Vector3 middle = (first.Centre + last.Centre) / 2f;
        float span = Vector3.Distance(Flat3(first.Centre), Flat3(last.Centre));
        Vector3 extents = new(half.X * AcrossShare, half.Y * UpShare, (span / 2f) + (half.Z * BeyondShare));
        return new MistRegion(new Vector3(middle.X, height / count, middle.Z), extents, yaw, CellFor(extents), density / count, first.Glow, current / count);
    }

    /// <summary>The cell size for a box: a quarter metre, or coarser to keep within the Engine's cells per axis and per region.</summary>
    internal static float CellFor(Vector3 half)
    {
        float cell = Math.Max(CellMetres, 2f * Math.Max(half.X, Math.Max(half.Y, half.Z)) / EngineMostCellsPerAxis);
        while (CellsAlong(half.X, cell) * CellsAlong(half.Y, cell) * CellsAlong(half.Z, cell) > EngineMostCellsPerRegion) cell *= 1.1f;
        return cell;
    }

    /// <summary>How many cells of this size span a half extent's whole width.</summary>
    internal static int CellsAlong(float half, float cell) => Math.Max(1, (int)MathF.Ceiling(2f * half / cell));

    private static Vector3 Flat(Vector3 v)
    {
        Vector3 flat = new(v.X, 0f, v.Z);
        return flat.LengthSquared() < 1e-6f ? Vector3.Zero : Vector3.Normalize(flat);
    }

    private static Vector3 Flat3(Vector3 v) => new(v.X, 0f, v.Z);
}
