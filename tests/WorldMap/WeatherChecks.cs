using System.Diagnostics;
using System.Numerics;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The weather field (#9738, Den <c>design/weather-and-environment</c>): deterministic fronts born only
/// where their kind's climate allows, drifting along the prevailing flow, fading out of their climate,
/// and a forecast that is the future's sample.
/// </summary>
internal static class WeatherChecks
{
    private const ulong Seed = 0xC0FFEE;
    private const double WindAngle = 0.4;
    private const double ContinentMetres = 390_000, Peak = 1_800;
    /// <summary>Two weeks of game time, sampled every six hours.</summary>
    private const int CensusHours = 14 * 24, CensusStep = 6;
    /// <summary>A season on the continent: long enough for the rare glass storm to show.</summary>
    private const int ContinentCensusDays = 90;

    /// <summary>Desert west of x = 0, temperate forest east of it: two climates side by side.</summary>
    private static MapSample Halves(double x, double z) =>
        x < 0 ? new MapSample(40, 0.55, 0.1, 0.1, 0) : new MapSample(40, 0.55, 0.7, 0.1, 0);

    internal static void Synthetic()
    {
        Check.That(MapBiomes.Classify(Halves(-1, 0)) == MapBiome.Desert && MapBiomes.Classify(Halves(1, 0)) == MapBiome.TemperateForest,
            "the synthetic world is desert to the west and forest to the east");
        WeatherField field = new(Seed, WindAngle, ContinentMetres, Peak, Halves);
        WeatherField again = new(Seed, WindAngle, ContinentMetres, Peak, Halves);

        // Births: every front stands where its kind may form; both climates get their weather.
        Dictionary<string, int> born = [];
        List<WeatherFront> seen = [];
        for (int hour = 0; hour < CensusHours; hour += CensusStep)
        {
            foreach (WeatherFront front in field.Alive(0, 0, hour, 150_000).DistinctBy(front => front.Key))
            {
                if (seen.Any(known => known.Key == front.Key)) continue;
                seen.Add(front);
                born[front.Kind.Id] = born.GetValueOrDefault(front.Kind.Id) + 1;
            }
        }

        Console.WriteLine($"weather census (synthetic, {CensusHours} h within 150 km): " + string.Join(" ", born.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
        Check.That(born.GetValueOrDefault("rain") > 0 && born.GetValueOrDefault("sand") > 0, "rain fronts form over the forest and sandstorms over the desert");
        Check.That(!born.ContainsKey("snow") && !born.ContainsKey("glass"), "no snowstorm forms where nothing is cold, and no glass storm without uplands");
        Check.That(seen.All(front => front.Kind.Birth(field.Ground(front.Centre(front.BirthHours).X, front.Centre(front.BirthHours).Y)) > 0),
            "every front was born where its kind's climate allows");

        // Drift: along the flow, at the kind's speed, about the prevailing wind's way.
        WeatherFront any = seen[0];
        Vector2 travelled = any.Centre(any.EndHours) - any.Centre(any.BirthHours);
        double expected = any.Kind.MetresPerHour * field.Scale.Speeds * Math.Ceiling(any.Kind.LifeHours);
        double heading = Math.Atan2(travelled.Y, travelled.X);
        Check.That(Math.Abs(travelled.Length() - expected) < expected * 0.01 && Math.Abs(Math.IEEERemainder(heading - WindAngle, 2 * Math.PI)) < 0.6 + 0.05,
            $"a {any.Kind.Name.ToLowerInvariant()} drifts {travelled.Length() / 1000:F1} km downwind over its life, heading {heading:F2} rad against a wind of {WindAngle}");

        // Climate: a sandstorm that drifts over the forest weakens.
        WeatherFront? leaving = seen.FirstOrDefault(front => front.Kind == WeatherKinds.Sandstorm
            && front.Centre(front.BirthHours).X < 0 && front.Centre(front.EndHours).X > 2_000);
        if (leaving is not null)
        {
            double crossing = Enumerable.Range(0, (int)leaving.Kind.LifeHours).Select(h => leaving.BirthHours + h)
                .First(hours => leaving.Centre(hours).X > 0);
            double after = Enumerable.Range(0, (int)leaving.Kind.LifeHours).Select(h => leaving.BirthHours + h)
                .Where(hours => leaving.Centre(hours).X > 1_000 && hours < leaving.EndHours - (leaving.Kind.LifeHours * 0.3)).DefaultIfEmpty(crossing).First();
            Check.That(leaving.Strength(after) < 0.2, $"a sandstorm blown over the forest dies away (strength {leaving.Strength(after):F2})");
        }

        // Determinism and forecast: the same field gives the same weather, whatever was asked of it first.
        (double X, double Z)[] probes = [(0, 0), (-30_000, 12_000), (45_000, -8_000), (5_000, 70_000)];
        bool agree = true;
        foreach ((double x, double z) in probes)
        {
            for (int hour = 0; hour < CensusHours; hour += 7)
            {
                EnvironmentSample now = field.Sample(x, z, hour + 0.5);
                EnvironmentSample other = again.Sample(x, z, hour + 0.5);
                agree &= now.Channels == other.Channels && now.Effects == other.Effects && now.Fronts.Count == other.Fronts.Count;
            }
        }

        Check.That(agree, "two fields of one seed agree at every place and hour, asked in a different order: the forecast is the future");
        WeatherField reseeded = new(Seed + 1, WindAngle, ContinentMetres, Peak, Halves);
        Check.That(Enumerable.Range(0, 40).Any(hour => reseeded.Sample(0, 0, hour * 6).Fronts.Count != field.Sample(0, 0, hour * 6).Fronts.Count),
            "another seed brings other weather");

        // Effects: a place under a rain front travels slower and gets wet; a clear place is neutral.
        (double X, double Z, double Hours)? rained = null;
        foreach (WeatherFront front in seen.Where(front => front.Kind == WeatherKinds.RainFront))
        {
            double mid = (front.BirthHours + front.EndHours) / 2;
            Vector2 centre = front.Centre(mid);
            if (front.Strength(mid) > 0.5) { rained = (centre.X, centre.Y, mid); break; }
        }

        Check.That(rained is not null, "some rain front reaches full strength");
        if (rained is { } wet)
        {
            EnvironmentSample under = field.Sample(wet.X, wet.Z, wet.Hours);
            Check.That(under.Effects.TravelCost > 1.2 && under.Effects.Wetting > 0.3 && under.Channels.Precipitation > 0.5 && under.Dominant?.Front.Kind == WeatherKinds.RainFront,
                $"under a rain front travel is slower and the open is wet ({under.Describe()}, cost {under.Effects.TravelCost:F2}, wetting {under.Effects.Wetting:F2})");
        }

        Check.That(EnvironmentSample.Clear.Effects == EnvironmentEffects.Neutral && EnvironmentSample.Clear.Describe() == "Clear", "a clear sky changes nothing");

        // Summoned fronts stand over the place now, at full strength, until cleared.
        WeatherFront glass = field.Summon(WeatherKinds.GlassStorm, 1_000, 1_000, 100);
        EnvironmentSample struck = field.Sample(1_000, 1_000, 100);
        Check.That(glass.Strength(100) > 0.95 && struck.Effects.Harm > 5 && struck.Channels.Arcane > 0.9,
            $"a summoned glass storm stands over the place at full strength and wounds the unsheltered ({struck.Describe()})");
        Check.That(field.ClearSummoned() == 1 && field.Sample(1_000, 1_000, 100).Effects.Harm == 0, "clearing summoned fronts lifts them");

        // A small world shrinks fronts to fit.
        WeatherField small = new(Seed, WindAngle, 10_240, Peak, Halves);
        Check.That(small.Scale.Lengths < 0.25 && small.Summon(WeatherKinds.RainFront, 0, 0, 0).RadiusMetres < 3_000,
            $"a 10 km world shrinks a rain front to {small.Scale.Lengths * WeatherKinds.RainFront.RadiusMetres / 1000:F1} km");

        // Cost: a sample anywhere is cheap once the neighbourhood is known.
        Stopwatch clock = Stopwatch.StartNew();
        for (int i = 0; i < 2_000; i++) field.Sample((i % 50) * 400, (i / 50) * 400, 200 + (i * 0.01));
        double perSample = clock.Elapsed.TotalMilliseconds / 2_000;
        Console.WriteLine($"weather sample: {perSample * 1000:F1} us each, {field.Remembered} lattice places remembered");
        Check.That(perSample < 0.5, $"a weather sample costs well under a millisecond ({perSample:F3} ms)");
    }

    /// <summary>A census on a generated continent: the kinds that form, where, and that each forms in its climate.</summary>
    internal static void Continent(WorldMap map)
    {
        WeatherField field = WeatherField.For(map);
        Dictionary<string, int> born = [];
        HashSet<FrontKey> seen = [];
        bool inClimate = true;
        Stopwatch clock = Stopwatch.StartNew();
        for (int hour = 0; hour < ContinentCensusDays * 24; hour += 12)
        {
            foreach (WeatherFront front in field.Alive(0, 0, hour, map.Radius))
            {
                if (!seen.Add(front.Key)) continue;
                born[front.Kind.Id] = born.GetValueOrDefault(front.Kind.Id) + 1;
                Vector2 at = front.Centre(front.BirthHours);
                inClimate &= front.Kind.Birth(field.Ground(at.X, at.Y)) > 0;
            }
        }

        Console.WriteLine($"weather census (continent, {ContinentCensusDays} days): " + string.Join(" ", born.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))
            + $" in {clock.Elapsed.TotalMilliseconds:F0} ms");
        Check.That(inClimate, "on the continent every front was born in its kind's climate");
        Check.That(born.GetValueOrDefault("rain") > 0 && born.Count >= 3, "a continent has rain fronts and at least two other kinds of weather in a season");
        Check.That(born.GetValueOrDefault("glass") is > 0 and < 30, $"glass storms are rare but do come: {born.GetValueOrDefault("glass")} in a season");
    }
}
