using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>A square region of the world that wildlife is seeded in, by its index along X and Z.</summary>
internal readonly record struct WildlifeRegion(int X, int Z)
{
    internal static WildlifeRegion At(double x, double z, double regionMetres) =>
        new((int)Math.Floor(x / regionMetres), (int)Math.Floor(z / regionMetres));

    internal (double X, double Z) Corner(double regionMetres) => (X * regionMetres, Z * regionMetres);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{X},{Z}");
}

/// <summary>
/// One herd of a region's population: who it is, where its range is centred, how many it was
/// founded with, and what it has lost. Losses come back one animal per the kind's recovery days,
/// counted on the world's clock, so a hunted herd recovers slowly and nothing is replaced around
/// the player.
/// </summary>
internal sealed class HerdRecord
{
    internal HerdRecord(string key, string kindId, Vector2 home, int founding)
    {
        Key = key;
        KindId = kindId;
        Home = home;
        Founding = founding;
    }

    internal string Key { get; }

    internal string KindId { get; }

    internal Vector2 Home { get; }

    internal int Founding { get; }

    /// <summary>Animals lost and not yet recovered, as of <see cref="RecoveringSince"/>.</summary>
    internal int Lost { get; private set; }

    /// <summary>The world day (with its fraction) the next recovery is counted from.</summary>
    internal double RecoveringSince { get; private set; }

    /// <summary>How many animals the herd has on a world day.</summary>
    internal int Alive(double day, double recoveryDays) => Founding - Lost + Recovered(day, recoveryDays);

    private int Recovered(double day, double recoveryDays) =>
        Lost == 0 || recoveryDays <= 0 ? Lost : Math.Min(Lost, (int)Math.Floor(Math.Max(0, day - RecoveringSince) / recoveryDays));

    /// <summary>One animal killed on a world day: what has recovered by then is banked first, then the loss counts from now.</summary>
    internal void Kill(double day, double recoveryDays)
    {
        int recovered = Recovered(day, recoveryDays);
        Lost -= recovered;
        RecoveringSince = Lost == 0 ? day : RecoveringSince + (recovered * recoveryDays);
        if (Lost >= Founding) return;
        Lost++;
    }
}

/// <summary>A region's seeded wildlife: the biomes its samples found and its herds.</summary>
internal sealed record RegionPopulation(WildlifeRegion Region, IReadOnlyDictionary<MapBiome, int> Biomes, IReadOnlyList<HerdRecord> Herds);

/// <summary>
/// Seeds regions with herds (#10567): pure over the world seed, a biome lookup and the kinds, so the
/// same world always holds the same herds in the same places, at the density the kinds give.
/// </summary>
internal static class WildlifeSeeding
{
    private const double SquareMetresPerSquareKilometre = 1_000_000;

    /// <param name="biomeAt">The biome under a world point (X, Z).</param>
    /// <param name="excluded">Ground no herd may call home: the towns' sanctuary. Null excludes nothing.</param>
    internal static RegionPopulation Seed(WildlifeRegion region, ulong seed, WildlifeCatalog catalog,
        Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(biomeAt);
        WildlifePlacement placement = catalog.Placement;
        double size = placement.RegionMetres;
        (double cornerX, double cornerZ) = region.Corner(size);
        int side = placement.BiomeSamplesPerSide;
        Dictionary<MapBiome, int> biomes = [];
        for (int i = 0; i < side; i++)
        {
            for (int j = 0; j < side; j++)
            {
                MapBiome biome = biomeAt(cornerX + ((i + 0.5) * size / side), cornerZ + ((j + 0.5) * size / side));
                biomes[biome] = biomes.GetValueOrDefault(biome) + 1;
            }
        }

        double samples = side * side;
        double squareKilometres = size * size / SquareMetresPerSquareKilometre;
        List<HerdRecord> herds = [];
        foreach (WildlifeKind kind in catalog.Kinds)
        {
            double share = biomes.Sum(pair => kind.HabitatWeight(pair.Key) * pair.Value) / samples;
            double expected = kind.Population.HerdsPerSquareKilometre * squareKilometres * share;
            if (expected <= 0) continue;
            string scope = string.Create(CultureInfo.InvariantCulture, $"wildlife:region:{region}:{kind.Id}");
            int count = (int)Math.Floor(expected) + (WildlifeRules.Unit(seed, scope + ":count") < expected - Math.Floor(expected) ? 1 : 0);
            for (int n = 0; n < count; n++)
            {
                string herd = $"{scope}:{n}";
                if (Home(herd, cornerX, cornerZ, size, kind, seed, placement.HomeAttempts, biomeAt, excluded) is not Vector2 home) continue;
                int founding = KeyedDraws.Draw(seed, herd + ":size", kind.GroupMinimum, kind.GroupMaximum);
                herds.Add(new HerdRecord(string.Create(CultureInfo.InvariantCulture, $"{kind.Id}@{region}#{n}"), kind.Id, home, founding));
            }
        }

        return new RegionPopulation(region, biomes, herds);
    }

    /// <summary>A home in the region on ground the kind lives on and outside any sanctuary; null when none is found in the attempts.</summary>
    private static Vector2? Home(string scope, double cornerX, double cornerZ, double size, WildlifeKind kind, ulong seed, int attempts,
        Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            string at = string.Create(CultureInfo.InvariantCulture, $"{scope}:home:{attempt}");
            double x = cornerX + (size * WildlifeRules.Unit(seed, at + ":x"));
            double z = cornerZ + (size * WildlifeRules.Unit(seed, at + ":z"));
            if (kind.HabitatWeight(biomeAt(x, z)) <= 0 || excluded?.Invoke(x, z) == true) continue;
            return new Vector2((float)x, (float)z);
        }

        return null;
    }
}

/// <summary>
/// The population ledger: every region seeded so far and what its herds have lost. Regions are
/// seeded the first time the player comes near and kept, so a hunted herd stays hunted. Herds
/// placed by live debug are kept apart from the seeded ones.
/// </summary>
internal sealed class WildlifeLedger
{
    private readonly Dictionary<WildlifeRegion, RegionPopulation> regions = [];
    private readonly List<HerdRecord> placed = [];
    private readonly WildlifeCatalog catalog;
    private readonly ulong seed;
    private readonly Func<double, double, MapBiome> biomeAt;
    private readonly Func<double, double, bool>? excluded;

    internal WildlifeLedger(WildlifeCatalog catalog, ulong seed, Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.seed = seed;
        this.biomeAt = biomeAt ?? throw new ArgumentNullException(nameof(biomeAt));
        this.excluded = excluded;
    }

    internal IEnumerable<RegionPopulation> Regions => regions.Values;

    internal IReadOnlyList<HerdRecord> Placed => placed;

    internal RegionPopulation Region(WildlifeRegion region)
    {
        if (!regions.TryGetValue(region, out RegionPopulation? population))
            regions[region] = population = WildlifeSeeding.Seed(region, seed, catalog, biomeAt, excluded);
        return population;
    }

    /// <summary>Every herd whose home lies within a distance of a point, seeding the regions that reach it; placed herds too.</summary>
    internal IEnumerable<HerdRecord> HomesWithin(double x, double z, double metres)
    {
        double size = catalog.Placement.RegionMetres;
        WildlifeRegion low = WildlifeRegion.At(x - metres, z - metres, size);
        WildlifeRegion high = WildlifeRegion.At(x + metres, z + metres, size);
        Vector2 at = new((float)x, (float)z);
        for (int i = low.X; i <= high.X; i++)
        {
            for (int j = low.Z; j <= high.Z; j++)
            {
                foreach (HerdRecord herd in Region(new WildlifeRegion(i, j)).Herds)
                {
                    if (Vector2.Distance(herd.Home, at) <= metres) yield return herd;
                }
            }
        }

        foreach (HerdRecord herd in placed)
        {
            if (Vector2.Distance(herd.Home, at) <= metres) yield return herd;
        }
    }

    /// <summary>A herd placed by live debug, not part of any region's population.</summary>
    internal HerdRecord Place(WildlifeKind kind, Vector2 home, int size)
    {
        HerdRecord herd = new(string.Create(CultureInfo.InvariantCulture, $"{kind.Id}@placed#{placed.Count}"), kind.Id, home, size);
        placed.Add(herd);
        return herd;
    }

    internal void Clear()
    {
        regions.Clear();
        placed.Clear();
    }
}
