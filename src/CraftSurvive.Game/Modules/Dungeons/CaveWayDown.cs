using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The walkable way from a cave mouth's tendril down into its cave (#10889 R10889-3). A karst cave's first sink is where water
/// entered: a pipe falling from the tendril's end to the passages below, which a player drops down but cannot climb back up
/// on a stamina bar; and the sink's chamber undercuts the tendril's last metres. Where the cave's main route falls that far
/// below the tendril's floor, a ramp with the tendril's section and level floor is carved from the tendril's end down to the
/// first point of the route a walkable slope reaches. Rock is laid under its floor, and under the tendril's own floor where it
/// runs in the box, wherever they cross open space, and the route starts with the ramp. All in the cave's own box (canonical metres,
/// voxel centres at +0.5), before the cave is admitted, so it is stored with the cave.
/// </summary>
internal static class CaveWayDown
{
    /// <summary>The grade the ramp keeps to where the route allows: the tendril's own is about 18 degrees.</summary>
    internal const float SlopeDegrees = 22f;

    /// <summary>The steepest the ramp may be where the route falls faster than <see cref="SlopeDegrees"/> everywhere near.</summary>
    internal const float MaximumSlopeDegrees = 30f;

    /// <summary>How far across from the tendril's end the ramp may reach for its foot.</summary>
    internal const float ReachMetres = 40f;

    /// <summary>A fall below the way in's floor deeper than this is a sink the way down goes round, not a step.</summary>
    internal const float DropMetres = 2.5f;

    /// <summary>How thick the floor laid under the ramp is where it crosses open space.</summary>
    internal const float FloorThickness = 1.5f;

    /// <summary>How far over the ramp's floor its route points lie (the route runs through a passage, not on its floor).</summary>
    internal const float RouteAboveFloor = 1f;

    /// <summary>The distance between the ramp's route points.</summary>
    internal const float RouteStep = 2f;

    /// <summary>How far beyond the ramp's section a ruin's kit piece is cleared from it (a piece spans up to a module's cell).</summary>
    internal const float KitClearMetres = 4f;

    /// <summary>How far round the ramp's section a growth, an overgrowth or a moss card left in its air is cleared.</summary>
    private const float DressingClearMetres = 1f;

    /// <summary>How far round the bore its rock's surface distance is brought to the bore's wall.</summary>
    private const float WallReach = 1.5f;

    private const float MinimumMagnitude = 0.001f;

    /// <summary>The ramp carved, as the route's new start and the voxels it opened.</summary>
    internal sealed record Carved(List<Vector3> Route, List<int> Opened, Vector3 From, Vector3 To);

    /// <summary>
    /// Carves the way down into a cave's voxels (x fastest, then y, then z) if its route needs one, and returns the route
    /// starting with it; null where the route never falls <see cref="DropMetres"/> under the way in's floor, or no point of it
    /// can be reached at a walkable grade.
    /// </summary>
    /// <param name="lead">The tendril's centre line where it runs in the box, ending at the centre of its last section.</param>
    /// <param name="floorBelowCentre">How far under a section's centre its level floor lies.</param>
    internal static Carved? Carve(int sizeX, int sizeY, int sizeZ, ushort[] materials, float[] densities, IReadOnlyList<Vector3> lead,
        IReadOnlyList<Vector3> route, float halfWidth, float halfHeight, float floorBelowCentre, ushort air, ushort rock)
    {
        ArgumentNullException.ThrowIfNull(materials);
        ArgumentNullException.ThrowIfNull(densities);
        ArgumentNullException.ThrowIfNull(route);
        if (lead is not { Count: > 0 })
        {
            return null;
        }

        Vector3 end = lead[^1];
        int Index(int i, int j, int k) => (((k * sizeY) + j) * sizeX) + i;
        float FloorUnder(Vector3 p)
        {
            int i = (int)MathF.Floor(p.X), k = (int)MathF.Floor(p.Z);
            if (i < 0 || i >= sizeX || k < 0 || k >= sizeZ)
            {
                return float.NaN;
            }

            int j = Math.Clamp((int)MathF.Floor(p.Y), 0, sizeY - 1);
            while (j > 0 && materials[Index(i, j, k)] == air)
            {
                j--;
            }

            return j + 1;
        }

        float endFloor = end.Y - floorBelowCentre;
        float grade = MathF.Tan(SlopeDegrees * MathF.PI / 180f);
        int sink = -1;
        for (int n = 0; n < route.Count && sink < 0; n++)
        {
            float floor = FloorUnder(route[n]);
            if (!float.IsNaN(floor) && floor <= endFloor - DropMetres)
            {
                sink = n;
            }
        }

        if (sink < 0)
        {
            return null;
        }

        // The ramp's foot: the first point past the sink it reaches at the preferred grade, else the gentlest within reach.
        int target = -1, gentlest = -1;
        float targetFloor = 0f, gentlestFloor = 0f, gentlestGrade = float.PositiveInfinity;
        for (int n = sink; n < route.Count && target < 0; n++)
        {
            float floor = FloorUnder(route[n]);
            float across = new Vector2(route[n].X - end.X, route[n].Z - end.Z).Length();
            if (float.IsNaN(floor) || across > ReachMetres || across < 1f)
            {
                continue;
            }

            float needed = (endFloor - floor) / across;
            if (needed <= grade)
            {
                target = n;
                targetFloor = floor;
            }
            else if (needed < gentlestGrade)
            {
                (gentlest, gentlestFloor, gentlestGrade) = (n, floor, needed);
            }
        }

        if (target < 0 && gentlestGrade <= MathF.Tan(MaximumSlopeDegrees * MathF.PI / 180f))
        {
            (target, targetFloor) = (gentlest, gentlestFloor);
        }

        if (target < 0)
        {
            return null;
        }

        Vector3 a = end, b = new(route[target].X, targetFloor + floorBelowCentre, route[target].Z);
        // The centre line laid: the tendril's run in the box (its floor only), then the ramp (carved and floored).
        Vector3[] line = [.. lead, b];
        float squash = halfHeight / halfWidth;
        Vector3 scale = new(squash, 1f, squash);
        float reach = halfWidth + WallReach + 1f;
        int i0 = Math.Max(0, (int)MathF.Floor(line.Min(q => q.X) - reach)), i1 = Math.Min(sizeX - 1, (int)MathF.Ceiling(line.Max(q => q.X) + reach));
        int j0 = Math.Max(0, (int)MathF.Floor(line.Min(q => q.Y) - floorBelowCentre - FloorThickness - 1f));
        int j1 = Math.Min(sizeY - 1, (int)MathF.Ceiling(line.Max(q => q.Y) + halfHeight + WallReach + 1f));
        int k0 = Math.Max(0, (int)MathF.Floor(line.Min(q => q.Z) - reach)), k1 = Math.Min(sizeZ - 1, (int)MathF.Ceiling(line.Max(q => q.Z) + reach));
        List<int> opened = [];
        for (int k = k0; k <= k1; k++)
        for (int j = j0; j <= j1; j++)
        for (int i = i0; i <= i1; i++)
        {
            Vector3 p = new(i + 0.5f, j + 0.5f, k + 0.5f), sp = p * scale;
            // The bore's reach by the nearest point of the line (its section squashed to a circle); its floor by the line
            // straight over or under the voxel, so the floor is level across and continuous along however steep the line.
            float nearest = float.PositiveInfinity, nearestAcross = float.PositiveInfinity;
            Vector3 above = line[^1];
            bool ramp = false;
            for (int s = 0; s + 1 < line.Length; s++)
            {
                Vector3 sa = line[s] * scale, sab = (line[s + 1] * scale) - sa;
                float t = Math.Clamp(Vector3.Dot(sp - sa, sab) / Math.Max(1e-6f, sab.LengthSquared()), 0f, 1f);
                float d = Vector3.Distance(sp, sa + (sab * t));
                Vector2 ha = new(line[s].X, line[s].Z), hab = new Vector2(line[s + 1].X, line[s + 1].Z) - ha, hp = new(p.X, p.Z);
                float th = Math.Clamp(Vector2.Dot(hp - ha, hab) / Math.Max(1e-6f, hab.LengthSquared()), 0f, 1f);
                float across = Vector2.Distance(hp, ha + (hab * th));
                if (d < nearest)
                {
                    nearest = d;
                    // The ramp and the tendril's last segment are carved, so they meet with no lip; the rest of the tendril
                    // is the world's own bore, only floored here.
                    ramp = s >= line.Length - 3;
                }

                if (across < nearestAcross)
                {
                    nearestAcross = across;
                    above = line[s] + ((line[s + 1] - line[s]) * th);
                }
            }

            float open = halfHeight - nearest;
            float floorPlane = above.Y - floorBelowCentre;
            float aside = nearestAcross;
            int at = Index(i, j, k);
            bool isAir = materials[at] == air;
            if (ramp && open > 0f && p.Y >= floorPlane)
            {
                float magnitude = Math.Max(MathF.Min(open, p.Y - floorPlane), MinimumMagnitude);
                densities[at] = isAir ? Math.Max(densities[at], magnitude) : magnitude;
                if (!isAir)
                {
                    materials[at] = air;
                    opened.Add(at);
                }
            }
            else if (p.Y < floorPlane && p.Y >= floorPlane - FloorThickness && aside <= halfWidth)
            {
                // The ramp's and the tendril's floor, laid where they cross a sink, a chamber or a passage, so the way is walked.
                float magnitude = Math.Max(floorPlane - p.Y, MinimumMagnitude);
                densities[at] = isAir ? magnitude : MathF.Min(densities[at], magnitude);
                materials[at] = isAir ? rock : materials[at];
            }
            else if (ramp && !isAir && open > -WallReach)
            {
                // Rock round the bore: its surface lies at the bore's wall.
                densities[at] = MathF.Min(densities[at], Math.Max(-open, MinimumMagnitude));
            }
        }

        List<Vector3> way = [];
        float length = Vector3.Distance(a, b);
        for (float d = 0f; d < length; d += RouteStep)
        {
            Vector3 on = a + ((b - a) * (d / length));
            way.Add(on with { Y = on.Y - floorBelowCentre + RouteAboveFloor });
        }

        way.AddRange(route.Skip(target));
        return new Carved(way, opened, a, b);
    }

    /// <summary>
    /// The cave's dressing with what the ramp cut through let go: a ruin's kit pieces standing in or beside it (they were
    /// fitted against rock the ramp took away, and they collide), and the growths, overgrowth and moss cards left hanging in
    /// its air. Positions are the cave's own (canonical) metres, as the carve's.
    /// </summary>
    internal static DungeonLayout Clear(DungeonLayout layout, Carved down, float halfWidth, float halfHeight, float floorBelowCentre)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(down);
        Vector3 a = down.From, b = down.To;
        (float Across, Vector3 Above) Near(Vector3 p)
        {
            Vector2 ha = new(a.X, a.Z), hab = new Vector2(b.X, b.Z) - ha, hp = new(p.X, p.Z);
            float t = Math.Clamp(Vector2.Dot(hp - ha, hab) / Math.Max(1e-6f, hab.LengthSquared()), 0f, 1f);
            return (Vector2.Distance(hp, ha + (hab * t)), a + ((b - a) * t));
        }

        bool KitInWay(KitPlacement piece)
        {
            (float across, Vector3 above) = Near(piece.Origin);
            float floor = above.Y - floorBelowCentre;
            return across <= halfWidth + KitClearMetres && piece.Origin.Y >= floor - CarveAndStamp.StoreyHeight && piece.Origin.Y <= floor + (2f * halfHeight) + KitClearMetres;
        }

        bool InAir(Vector3 at)
        {
            (float across, Vector3 above) = Near(at);
            float floor = above.Y - floorBelowCentre;
            return across <= halfWidth + DressingClearMetres && at.Y >= floor - DressingClearMetres && at.Y <= floor + (2f * halfHeight) + DressingClearMetres;
        }

        return layout with
        {
            Kit = [.. layout.Kit.Where(piece => !KitInWay(piece))],
            Overgrowth = [.. layout.Overgrowth.Where(o => !InAir(o.At))],
            MossCards = [.. layout.MossCards.Where(m => !InAir(m.At))],
            Ecology = [.. layout.Ecology.Where(e => !InAir(e.At))],
        };
    }
}
