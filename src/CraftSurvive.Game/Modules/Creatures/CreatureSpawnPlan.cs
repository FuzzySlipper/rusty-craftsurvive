namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// Where a starting set of creatures may appear around a point: on rings beyond the distance a
/// hostile creature sees, spread over compass headings, and kept apart from each other. The plan
/// only proposes columns; the encounter rules decide whether each one takes a creature.
/// </summary>
internal static class CreatureSpawnPlan
{
    /// <summary>Beyond a hostile creature's sight, so a world spawn starts unaware.</summary>
    internal const int MinimumDistanceMetres = 56;

    internal const int MaximumDistanceMetres = 96;

    internal const int RingStepMetres = 4;

    /// <summary>Headings tried on each ring.</summary>
    internal const int Headings = 12;

    /// <summary>How far apart two placed creatures must stand.</summary>
    internal const double MinimumSeparationMetres = 24;

    /// <summary>
    /// Candidate columns, nearest ring first. Each ring's headings are offset by half a step from
    /// the previous ring's, so successive rings do not repeat the same bearings.
    /// </summary>
    internal static IEnumerable<(long X, long Z)> Candidates(long originX, long originZ) =>
        Candidates(originX, originZ, MinimumDistanceMetres, MaximumDistanceMetres);

    /// <summary>A travel ambush closes in: inside a hostile creature's sight and the ground already streamed around the player.</summary>
    internal const int AmbushMinimumDistanceMetres = 20;
    internal const int AmbushMaximumDistanceMetres = 32;

    internal static IEnumerable<(long X, long Z)> Candidates(long originX, long originZ, int minimumMetres, int maximumMetres)
    {
        int ringIndex = 0;
        for (int ring = minimumMetres; ring <= maximumMetres; ring += RingStepMetres, ringIndex++)
        {
            double offset = (ringIndex % 2) * 0.5;
            for (int heading = 0; heading < Headings; heading++)
            {
                double angle = 2 * Math.PI * (heading + offset) / Headings;
                yield return (
                    originX + (long)Math.Round(ring * Math.Cos(angle)),
                    originZ + (long)Math.Round(ring * Math.Sin(angle)));
            }
        }
    }

    /// <summary>
    /// Choose up to <paramref name="count"/> more columns from the candidates, in order, each far
    /// enough from everything in <paramref name="placed"/> - which may already hold earlier choices
    /// of the same placement, made on earlier updates - and accepted by <paramref name="accept"/>.
    /// Each chosen column is added to <paramref name="placed"/> and returned; a column already placed
    /// is never offered to <paramref name="accept"/> again.
    /// </summary>
    internal static List<(long X, long Z)> Choose(IEnumerable<(long X, long Z)> candidates, List<(long X, long Z)> placed, int count,
        Func<(long X, long Z), bool> accept)
    {
        ArgumentNullException.ThrowIfNull(placed);
        List<(long X, long Z)> chosen = [];
        foreach ((long X, long Z) candidate in candidates)
        {
            if (chosen.Count >= count) break;
            if (!FarEnoughFrom(placed, candidate) || !accept(candidate)) continue;
            placed.Add(candidate);
            chosen.Add(candidate);
        }

        return chosen;
    }

    internal static bool FarEnoughFrom(IEnumerable<(long X, long Z)> placed, (long X, long Z) candidate)
    {
        ArgumentNullException.ThrowIfNull(placed);
        foreach ((long x, long z) in placed)
        {
            double dx = candidate.X - x;
            double dz = candidate.Z - z;
            if ((dx * dx) + (dz * dz) < MinimumSeparationMetres * MinimumSeparationMetres)
            {
                return false;
            }
        }

        return true;
    }
}
