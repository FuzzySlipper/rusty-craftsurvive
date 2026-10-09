using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Sky;
using CraftSurvive.Game.Modules.Survival;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The owner of the world's weather (#9738, Den <c>design/weather-and-environment</c>): the field for
/// this world, and the weather over the player, refreshed as the clock runs and the player moves.
/// Map travel and presentation ask the field directly for other places and times. It saves nothing:
/// the field follows from the seed, the map and the clock.
/// </summary>
internal sealed class WeatherModule : IProductModule
{
    private const double HoursPerDay = 24;
    /// <summary>How far around the player fronts are listed, and how far ahead the forecast looks, by default.</summary>
    internal const double DefaultListKilometres = 120, DefaultForecastHours = 48;
    private const double ForecastStepHours = 3;
    private const double MetresPerKilometre = 1000;
    private static readonly string[] Compass = ["N", "NE", "E", "SE", "S", "SW", "W", "NW"];

    /// <summary>The look eases toward the weather's with this time constant, in game hours: conditions blend in over game minutes.</summary>
    private const double LookEaseHours = 0.25;
    /// <summary>The ground wets as fast as the look eases, and dries over a couple of game hours once the rain stops.</summary>
    private const double DryingHours = 2;
    /// <summary>Puddles gather once the ground is this wet, and are as deep as it is wetter.</summary>
    private const float PuddlesAbove = 0.5f;
    private float groundWetness;

    private readonly Func<WorldTime> clock;
    private readonly Func<Vector3> playerWorld;
    private readonly Func<bool> inTheOpen;
    private readonly Func<bool> covered;
    private readonly DayNightSky sky;
    private readonly WeatherHere here;
    private double lookedHours = double.NaN;

    /// <param name="inTheOpen">Whether the player is under the open sky: not in a dungeon, not under water.</param>
    /// <param name="covered">Whether something solid stands over the player's head: a roof, an overhang.</param>
    internal WeatherModule(WorldMap map, DayNightSky sky, Func<WorldTime> clock, Func<Vector3> playerWorld,
        Func<bool> inTheOpen, Func<bool> covered)
    {
        this.covered = covered ?? throw new ArgumentNullException(nameof(covered));
        Field = WeatherField.For(map ?? throw new ArgumentNullException(nameof(map)));
        this.sky = sky ?? throw new ArgumentNullException(nameof(sky));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.playerWorld = playerWorld ?? throw new ArgumentNullException(nameof(playerWorld));
        this.inTheOpen = inTheOpen ?? throw new ArgumentNullException(nameof(inTheOpen));
        here = new WeatherHere(Field, () => Hours, () =>
        {
            Vector3 at = playerWorld();
            return new Vector2(at.X, at.Z);
        });
    }

    internal WeatherField Field { get; }

    /// <summary>The weather over the player now: sampled again on reading once the clock or the player has moved.</summary>
    internal EnvironmentSample Here => here.Current;

    /// <summary>
    /// What the weather over the player does to them (#9741): its wetting, chill and harm, and whether
    /// they are sheltered - under a roof or an overhang, or out of the open altogether (a dungeon).
    /// </summary>
    internal WeatherExposure Exposure { get; private set; } = WeatherExposure.None;

    /// <summary>The clock as absolute game hours, the weather's time.</summary>
    internal double Hours => Of(clock());

    internal static double Of(WorldTime time) => (time.Day * HoursPerDay) + (time.DayFraction * HoursPerDay);

    public void Start()
    {
        here.Fresh();
        Present();
    }

    public void Update(ProductStep step)
    {
        Present();
    }

    public void Restart()
    {
        lookedHours = double.NaN;
        here.Fresh();
        Present();
    }

    public void Dispose()
    {
    }

    /// <summary>
    /// The sky and what falls ease toward the weather over the player (#9740): a step of the way each
    /// update by the game time passed, so a front blends in over game minutes and a long rest or a
    /// journey arrives at the weather as it is.
    /// </summary>
    private void Present()
    {
        bool open = inTheOpen();
        EnvironmentEffects effects = Here.Effects;
        // Shelter is the cover overhead, whatever the sky: a wet player under a roof dries as one under
        // cover after the rain has passed, not as one in the open.
        Exposure = new WeatherExposure(effects.Wetting, effects.Chill, effects.Harm, !open || covered());
        double hours = Hours;
        Vector3 at = playerWorld();
        WeatherLook target = WeatherLook.From(Here, Field.Flow(at.X, at.Z));
        double passed = double.IsNaN(lookedHours) ? double.PositiveInfinity : Math.Max(0, hours - lookedHours);
        float share = (float)(1 - Math.Exp(-passed / LookEaseHours));
        sky.Weather(sky.Look.Toward(target, share));
        // The ground wets in rain and dries slowly after it (rusty-engine #9744 draws it).
        float raining = target.Fall == WeatherFall.Rain ? target.FallDensity : 0f;
        float dryShare = (float)(1 - Math.Exp(-passed / DryingHours));
        groundWetness = raining >= groundWetness ? groundWetness + ((raining - groundWetness) * share) : groundWetness + ((raining - groundWetness) * dryShare);
        sky.Wetness(groundWetness, Math.Max(0f, (groundWetness - PuddlesAbove) / (1f - PuddlesAbove)));
        lookedHours = hours;
    }

    internal string Readout()
    {
        Vector3 at = playerWorld();
        // Exactly the weather of the time and place it is labelled with, even when updates were skipped.
        EnvironmentSample now = here.Fresh();
        EnvironmentChannels c = now.Channels;
        EnvironmentEffects e = now.Effects;
        return string.Create(CultureInfo.InvariantCulture,
            $"weather={now.Describe()} hours={Hours:F2} at={at.X:F0},{at.Z:F0} flow={Heading(Field.Flow(at.X, at.Z))} ")
            + string.Create(CultureInfo.InvariantCulture,
            $"precipitation={c.Precipitation:F2} cloud={c.Cloud:F2} wind={c.Wind:F2} cold={c.Cold:F2} murk={c.Murk:F2} arcane={c.Arcane:F2} ")
            + string.Create(CultureInfo.InvariantCulture,
            $"travelCost={e.TravelCost:F2} supplyUse={e.SupplyUse:F2} eventRisk={e.EventRisk:F2} wetting={e.Wetting:F2}/h chill={e.Chill:F2}/h harm={e.Harm:F2}/h sight={e.Sight:F2} ")
            + $"sheltered={Exposure.Sheltered} fronts={now.Fronts.Count} remembered={Field.Remembered} scale={Field.Scale.Lengths:F2} "
            + string.Create(CultureInfo.InvariantCulture, $"look=cloud:{sky.Look.Cloud:F2},murk:{sky.Look.Murk:F2},fall:{sky.Look.Fall}@{sky.Look.FallDensity:F2} wetGround={groundWetness:F2} falling={sky.Falling.Drops} underWater={sky.ViewSubmerged}");
    }

    /// <summary>The fronts within reach of the player: where, which way they go, how strong, and when they would arrive.</summary>
    internal string FrontsReadout(double kilometres)
    {
        Vector3 at = playerWorld();
        double hours = Hours;
        List<string> rows = [];
        foreach (WeatherFront front in Field.Alive(at.X, at.Z, hours, kilometres * MetresPerKilometre)
            .OrderBy(front => Vector2.Distance(front.Centre(hours), new Vector2(at.X, at.Z))))
        {
            Vector2 centre = front.Centre(hours);
            Vector2 offset = centre - new Vector2(at.X, at.Z);
            double arrives = Arrival(front, at.X, at.Z, hours);
            rows.Add(string.Create(CultureInfo.InvariantCulture,
                $"{front.Kind.Id} {offset.Length() / MetresPerKilometre:F1}km {Heading(offset)} heading={Heading(front.Velocity(hours))} speed={front.Velocity(hours).Length() / MetresPerKilometre:F2}km/h radius={front.RadiusMetres / MetresPerKilometre:F1}km strength={front.Strength(hours):F2} ")
                + (double.IsNaN(arrives) ? "misses" : arrives <= 0 ? "here" : string.Create(CultureInfo.InvariantCulture, $"arrives+{arrives:F0}h"))
                + string.Create(CultureInfo.InvariantCulture, $" ends+{front.EndHours - hours:F0}h"));
        }

        return rows.Count == 0 ? "no fronts" : $"{rows.Count} fronts: " + string.Join(" | ", rows);
    }

    /// <summary>The weather over the player for the hours ahead: the forecast is the field later.</summary>
    internal string ForecastReadout(double ahead)
    {
        Vector3 at = playerWorld();
        double hours = Hours;
        List<string> steps = [];
        for (double later = 0; later <= ahead; later += ForecastStepHours)
        {
            EnvironmentSample sample = Field.Sample(at.X, at.Z, hours + later);
            steps.Add(string.Create(CultureInfo.InvariantCulture, $"+{later:F0}h {sample.Describe()}"));
        }

        return string.Join(" | ", steps);
    }

    /// <summary>For diagnosis: a front of a kind standing over the player now, or <paramref name="upwindKilometres"/> upwind of them.</summary>
    internal string Summon(string id, double upwindKilometres)
    {
        if (WeatherKinds.Find(id) is not WeatherKind kind) return $"unknown weather '{id}'; kinds: {string.Join(", ", WeatherKinds.All.Select(k => k.Id))}";
        Vector3 at = playerWorld();
        Vector2 back = Field.Flow(at.X, at.Z) * (float)(upwindKilometres * MetresPerKilometre);
        WeatherFront front = Field.Summon(kind, at.X - back.X, at.Z - back.Y, Hours);
        here.Fresh();
        return string.Create(CultureInfo.InvariantCulture, $"summoned {kind.Name} {upwindKilometres:F1}km upwind, radius {front.RadiusMetres / MetresPerKilometre:F1}km; {Readout()}");
    }

    internal string ClearSummoned()
    {
        int cleared = Field.ClearSummoned();
        here.Fresh();
        return $"cleared {cleared} summoned front(s); {Readout()}";
    }

    /// <summary>Hours until a front first covers a place, 0 if it already does, or NaN if it passes by.</summary>
    private static double Arrival(WeatherFront front, double x, double z, double hours)
    {
        for (double later = 0; hours + later <= front.EndHours; later += 1)
        {
            if (front.Cover(x, z, hours + later) > 0) return later;
        }

        return double.NaN;
    }

    /// <summary>The compass point a world direction points to: north is -Z, east +X.</summary>
    internal static string Heading(Vector2 direction)
    {
        if (direction.LengthSquared() < 1e-6f) return "-";
        double clockwise = Math.Atan2(direction.X, -direction.Y);
        int sector = (int)Math.Round(clockwise / (Math.PI / 4));
        return Compass[((sector % Compass.Length) + Compass.Length) % Compass.Length];
    }
}
