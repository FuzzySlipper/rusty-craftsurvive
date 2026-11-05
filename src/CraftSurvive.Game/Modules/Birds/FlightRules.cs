using System.Numerics;

namespace CraftSurvive.Game.Modules.Birds;

/// <summary>
/// One flight (#10630): a line across a circle around where the player was, which it misses by some way,
/// walked at the kind's drift. A circling kind turns about a point moving along that line. It is a formula
/// of time, so nothing about it is simulated, kept or saved.
/// </summary>
internal sealed record Flight(BirdKind Kind, Vector2 Start, Vector2 Heading, double Seconds, double Radius, double TurnRadiansPerSecond, double Height, double Phase, IReadOnlyList<FlightSlot> Slots)
{
    internal double Age { get; set; }
}

/// <summary>A bird's place in its flight: how far round the circle, how far out, how much higher, and when it flaps.</summary>
internal readonly record struct FlightSlot(double Phase, double RadiusShare, double Height, double FlapOffset);

/// <summary>Where a bird is at a moment: world metres, and which way it faces (yaw 0 is north) and leans.</summary>
internal readonly record struct BirdPose(Vector3 Position, float Yaw, float Bank)
{
    /// <summary>The point on the map under it.</summary>
    internal Vector2 Ground => new(Position.X, Position.Z);
}

/// <summary>Where flights go. Pure: the module hands in its draws and the ground.</summary>
internal static class FlightRules
{
    private const int SlotPhaseTurns = 1;
    private const double FullTurn = 2 * Math.PI;

    /// <summary>The kind to send from those that fly here and now, by how much each likes the ground, or null for none.</summary>
    internal static BirdKind? Choose(IReadOnlyList<BirdKind> flying, WorldGen.MapBiome biome, double draw)
    {
        double whole = flying.Sum(kind => kind.Habitat.GetValueOrDefault(biome));
        double left = draw * whole;
        foreach (BirdKind kind in flying)
        {
            left -= kind.Habitat.GetValueOrDefault(biome);
            if (left < 0) return kind;
        }

        return flying.Count > 0 ? flying[^1] : null;
    }

    /// <summary>
    /// A new flight: it comes in from the edge of the catalog's reach, out of the player's sight, on a line
    /// that passes the player at a distance and leaves by the far side, high above the highest ground on its way.
    /// </summary>
    /// <param name="draw">A draw from 0 to 1, as many as it asks for.</param>
    /// <param name="groundAt">The ground's height at a point.</param>
    internal static Flight Plan(BirdKind kind, BirdCatalog catalog, Vector2 player, Func<double> draw, Func<double, double, double> groundAt)
    {
        double bearing = draw() * FullTurn;
        Vector2 heading = new((float)Math.Cos(bearing), (float)Math.Sin(bearing));
        Vector2 side = new(-heading.Y, heading.X);
        double miss = catalog.MissMetres.At(draw()) * (draw() < 0.5 ? -1 : 1);
        double half = Math.Sqrt((catalog.ReachMetres * catalog.ReachMetres) - (miss * miss));
        Vector2 start = player + (side * (float)miss) - (heading * (float)half);
        double highest = double.NegativeInfinity;
        for (double along = 0; along <= 2 * half; along += catalog.GroundEveryMetres)
        {
            Vector2 at = start + (heading * (float)along);
            highest = Math.Max(highest, groundAt(at.X, at.Y));
        }

        BirdFlight flight = kind.Flight;
        double radius = flight.RadiusMetres.At(draw());
        double turn = FullTurn / flight.TurnSeconds.At(draw()) * (draw() < 0.5 ? -1 : 1);
        int count = kind.Flock.Least + (int)Math.Floor(draw() * (kind.Flock.Most - kind.Flock.Least + 1));
        count = Math.Min(count, kind.Flock.Most);
        double height = highest + flight.HeightMetres.At(draw());
        double phase = draw() * FullTurn;
        FlightSlot[] slots = [.. Enumerable.Range(0, count).Select(order => new FlightSlot(
            order * SlotPhaseTurns * FullTurn / count,
            1 + (((2 * draw()) - 1) * kind.Flock.RadiusShare),
            order * kind.Flock.HeightMetres,
            draw() * flight.FlapEverySeconds))];
        return new Flight(kind, start, heading, 2 * half / flight.DriftMetresPerSecond, radius, turn, height, phase, slots);
    }

    /// <summary>Where a bird is this far into its flight. It faces along its way, and leans into its turn.</summary>
    internal static BirdPose At(Flight flight, FlightSlot slot, double age)
    {
        double drift = flight.Kind.Flight.DriftMetresPerSecond;
        double radius = flight.Radius * slot.RadiusShare;
        double angle = flight.Phase + slot.Phase + (flight.TurnRadiansPerSecond * age);
        Vector2 round = new((float)Math.Cos(angle), (float)Math.Sin(angle));
        Vector2 position = flight.Start + (flight.Heading * (float)(drift * age)) + (round * (float)radius);
        Vector2 velocity = (flight.Heading * (float)drift) + (new Vector2(-round.Y, round.X) * (float)(radius * flight.TurnRadiansPerSecond));
        // Yaw 0 faces north (-Z) and a quarter turn east (+X).
        float yaw = MathF.Atan2(velocity.X, -velocity.Y);
        float bank = radius > 0 ? (float)(Math.Sign(flight.TurnRadiansPerSecond) * flight.Kind.Flight.BankDegrees * Math.PI / 180) : 0;
        return new BirdPose(new Vector3(position.X, (float)(flight.Height + slot.Height), position.Y), yaw, bank);
    }

    /// <summary>Whether a bird is flapping now: it soars, and works its wings for a few seconds now and then.</summary>
    internal static bool Flapping(Flight flight, FlightSlot slot, double age)
    {
        BirdFlight kind = flight.Kind.Flight;
        return ((age + slot.FlapOffset) % kind.FlapEverySeconds) < kind.FlapSeconds;
    }

    /// <summary>How near the player's ground a flight's line comes, in metres along the ground.</summary>
    internal static double Passes(Flight flight, Vector2 player)
    {
        Vector2 toPlayer = player - flight.Start;
        Vector2 along = flight.Heading * Vector2.Dot(toPlayer, flight.Heading);
        return (toPlayer - along).Length();
    }
}
