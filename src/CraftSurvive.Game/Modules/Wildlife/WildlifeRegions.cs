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
        Settle(day, recoveryDays);
        if (Lost >= Founding) return;
        Lost++;
    }

    /// <summary>Banks what has recovered by a world day, so the loss reads as it stands that day.</summary>
    internal void Settle(double day, double recoveryDays)
    {
        int recovered = Recovered(day, recoveryDays);
        Lost -= recovered;
        RecoveringSince = Lost == 0 ? day : RecoveringSince + (recovered * recoveryDays);
    }

    /// <summary>Takes up a saved loss; a herd seeded smaller since keeps none past its founding size.</summary>
    internal void Restore(HerdLoss loss)
    {
        Lost = Math.Min(loss.Lost, Founding);
        RecoveringSince = loss.RecoveringSince;
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

    /// <summary>How many grid steps from water a home may stand, so a kind that lives by it stays within reach of it.</summary>
    private const int MostStepsToDryGround = 4;

    /// <param name="biomeAt">The biome under a world point (X, Z).</param>
    /// <param name="excluded">Ground no herd may call home: the towns' sanctuary. Null excludes nothing.</param>
    /// <param name="waterAt">Whether the map has a river or lake at a point: a kind that takes to water lives beside one. Null places it as any other.</param>
    internal static RegionPopulation Seed(WildlifeRegion region, ulong seed, WildlifeCatalog catalog,
        Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded = null, Func<double, double, bool>? waterAt = null)
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
                Vector2? found = kind.Pressed == PressedResponse.TakeToWater && waterAt is not null
                    ? HomeByWater(herd, cornerX, cornerZ, size, kind, seed, placement.WaterHomeSamplesPerSide, biomeAt, excluded, waterAt)
                    : Home(herd, cornerX, cornerZ, size, kind, seed, placement.HomeAttempts, biomeAt, excluded);
                if (found is not Vector2 home) continue;
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

    /// <summary>A home on dry ground just off water in the region, from a grid walked from a drawn start; null where the region has no such place.</summary>
    private static Vector2? HomeByWater(string scope, double cornerX, double cornerZ, double size, WildlifeKind kind, ulong seed, int side,
        Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded, Func<double, double, bool> waterAt)
    {
        int cells = side * side;
        int start = KeyedDraws.Draw(seed, scope + ":water", 0, cells - 1);
        double step = size / side;
        (double East, double South)[] headings = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        for (int i = 0; i < cells; i++)
        {
            int cell = (start + i) % cells;
            double x = cornerX + (((cell % side) + 0.5) * step), z = cornerZ + (((cell / side) + 0.5) * step);
            if (!waterAt(x, z)) continue;
            foreach ((double east, double south) in headings)
            {
                for (int outward = 1; outward <= MostStepsToDryGround; outward++)
                {
                    double homeX = x + (east * step * outward), homeZ = z + (south * step * outward);
                    if (waterAt(homeX, homeZ)) continue;
                    if (kind.HabitatWeight(biomeAt(homeX, homeZ)) > 0 && excluded?.Invoke(homeX, homeZ) != true) return new Vector2((float)homeX, (float)homeZ);
                    break;
                }
            }
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

    /// <summary>Saved losses of herds whose regions have not been seeded this session; each is taken up when its region is.</summary>
    private readonly Dictionary<ulong, HerdLoss> waiting = [];
    private readonly WildlifeCatalog catalog;
    private readonly ulong seed;
    private readonly Func<double, double, MapBiome> biomeAt;
    private readonly Func<double, double, bool>? excluded;
    private readonly Func<double, double, bool>? waterAt;

    internal WildlifeLedger(WildlifeCatalog catalog, ulong seed, Func<double, double, MapBiome> biomeAt, Func<double, double, bool>? excluded = null, Func<double, double, bool>? waterAt = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.seed = seed;
        this.biomeAt = biomeAt ?? throw new ArgumentNullException(nameof(biomeAt));
        this.excluded = excluded;
        this.waterAt = waterAt;
    }

    internal IEnumerable<RegionPopulation> Regions => regions.Values;

    internal IReadOnlyList<HerdRecord> Placed => placed;

    internal RegionPopulation Region(WildlifeRegion region)
    {
        if (regions.TryGetValue(region, out RegionPopulation? population)) return population;
        regions[region] = population = WildlifeSeeding.Seed(region, seed, catalog, biomeAt, excluded, waterAt);
        foreach (HerdRecord herd in population.Herds)
        {
            if (waiting.Remove(HerdLoss.IdOf(herd.Key), out HerdLoss loss)) herd.Restore(loss);
        }

        return population;
    }

    /// <summary>Takes up saved losses. Call before any region is seeded.</summary>
    internal void Restore(IEnumerable<HerdLoss> losses)
    {
        ArgumentNullException.ThrowIfNull(losses);
        foreach (HerdLoss loss in losses) waiting[loss.Herd] = loss;
    }

    /// <summary>
    /// Every seeded herd still short of animals on a world day, with the saved losses of regions not
    /// seeded this session, in the order a save keeps them. Placed herds belong to no population.
    /// </summary>
    internal IReadOnlyList<HerdLoss> Losses(double day)
    {
        List<HerdLoss> losses = [.. waiting.Values];
        foreach (HerdRecord herd in regions.Values.SelectMany(region => region.Herds))
        {
            herd.Settle(day, catalog.Kind(herd.KindId).Population.RecoveryDays);
            if (herd.Lost > 0) losses.Add(new HerdLoss(HerdLoss.IdOf(herd.Key), herd.Lost, herd.RecoveringSince));
        }

        return [.. losses.OrderBy(loss => loss.Herd).Take(HerdLoss.MostHerds)];
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
}
