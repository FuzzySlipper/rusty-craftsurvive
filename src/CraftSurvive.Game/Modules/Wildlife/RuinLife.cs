using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>The layers of ruin life (den-wiki wandering-years/ruins-wildlife, "Layers").</summary>
internal enum RuinLayer
{
    Fixture,
    Tender,
    Skitterer,
    Collector,
    Special,
}

/// <summary>A kind of ruin life (#10955): its layer, where it lives, and for a tender, the fixture it lives by.</summary>
internal sealed record RuinKind(string Kind, RuinLayer Layer, RuinSite Site, string? Near);

/// <summary>
/// How much life a ruin holds by its size (#10955), as settled with Patch on the wiki's Ruins wildlife page ("How far a ruin's
/// life spreads"): how many homes of each layer a ruin of each size holds, fewest to most, and the kinds of each.
/// </summary>
internal sealed record WildlifeRuinLife(IReadOnlyDictionary<(RuinLayer Layer, RuinExtent Extent), (int Fewest, int Most)> Counts, IReadOnlyList<RuinKind> Kinds)
{
    internal static WildlifeRuinLife None { get; } = new(new Dictionary<(RuinLayer, RuinExtent), (int, int)>(), []);

    internal (int Fewest, int Most) Count(RuinLayer layer, RuinExtent extent) => Counts.TryGetValue((layer, extent), out (int, int) count) ? count : (0, 0);

    internal static WildlifeRuinLife Read(JsonElement block, Func<string, bool> isKind)
    {
        Dictionary<(RuinLayer, RuinExtent), (int, int)> counts = [];
        foreach (JsonProperty layer in block.GetProperty("layers").EnumerateObject())
        {
            if (layer.Name.StartsWith('$')) continue;
            RuinLayer which = Enum.Parse<RuinLayer>(layer.Name, ignoreCase: true);
            foreach (JsonProperty size in layer.Value.EnumerateObject())
            {
                RuinExtent extent = Enum.Parse<RuinExtent>(size.Name, ignoreCase: true);
                int fewest = size.Value[0].GetInt32(), most = size.Value[1].GetInt32();
                if (fewest < 0 || most < fewest) throw new InvalidDataException($"ruin life: {layer.Name} in a {size.Name} is a range from none or more");
                counts[(which, extent)] = (fewest, most);
            }
        }

        List<RuinKind> kinds = [];
        foreach (JsonElement entry in block.GetProperty("kinds").EnumerateArray())
        {
            string kind = entry.GetProperty("kind").GetString()!;
            if (!isKind(kind)) throw new InvalidDataException($"ruin life: no kind '{kind}'");
            RuinSite site = Enum.Parse<RuinSite>(entry.GetProperty("site").GetString()!.Replace("-", "", StringComparison.Ordinal), ignoreCase: true);
            string? near = entry.TryGetProperty("near", out JsonElement by) ? by.GetString() : null;
            kinds.Add(new RuinKind(kind, Enum.Parse<RuinLayer>(entry.GetProperty("layer").GetString()!, ignoreCase: true), site, near));
        }

        foreach (RuinKind tender in kinds.Where(kind => kind.Site == RuinSite.Near))
        {
            if (tender.Near is not string fixture || !kinds.Any(kind => kind.Kind == fixture && kind.Layer == RuinLayer.Fixture))
                throw new InvalidDataException($"ruin life: {tender.Kind} lives near a fixture the ruin life names");
        }

        foreach ((RuinLayer layer, RuinExtent _) in counts.Keys)
        {
            if (!kinds.Any(kind => kind.Layer == layer)) throw new InvalidDataException($"ruin life: the {layer} layer has no kinds");
        }

        return new WildlifeRuinLife(counts, kinds);
    }
}

/// <summary>
/// Where a cave's ruin life lives (#10955): the homes of each layer a ruin of its size holds, at the sites its kinds take, a
/// tender by its own fixture, a special at the vault. Pure: the same cave gives the same homes in every session.
/// </summary>
internal static class RuinFauna
{
    /// <summary>Two homes in a ruin stand at least this far apart; a tender stands this near its fixture at most.</summary>
    internal const float HomesApartMetres = 3f, TenderReachMetres = 10f;

    /// <summary>Cave life keeps this far from a ruin's mouth, as well as out of its rooms.</summary>
    internal const float MouthClearMetres = 12f;

    internal static IReadOnlyList<HerdRecord> Homes(string caveKey, RuinSites ruin, WildlifeRuinLife life, WildlifeCatalog catalog, ulong seed)
    {
        ArgumentNullException.ThrowIfNull(ruin);
        ArgumentNullException.ThrowIfNull(life);
        ArgumentNullException.ThrowIfNull(catalog);
        List<(RuinKind Kind, Vector3 Site)> placed = [];
        bool Free(Vector3 site, float apart) => placed.All(other => Vector3.Distance(other.Site, site) >= apart);

        // The special first, so its vault is its own; then each fixture before the tenders that live by it.
        foreach (RuinLayer layer in (RuinLayer[])[RuinLayer.Special, RuinLayer.Fixture, RuinLayer.Tender, RuinLayer.Skitterer, RuinLayer.Collector])
        {
            (int fewest, int most) = life.Count(layer, ruin.Extent);
            int count = Draw($"{caveKey}/ruin/{layer}", seed, fewest, most);
            RuinKind[] kinds = [.. life.Kinds.Where(kind => kind.Layer == layer)];
            if (count == 0 || kinds.Length == 0) continue;
            int start = Draw($"{caveKey}/ruin/{layer}/first", seed, 0, kinds.Length - 1);
            for (int n = 0, tries = 0; n < count && tries < count * kinds.Length * 2; tries++)
            {
                RuinKind kind = kinds[(start + tries) % kinds.Length];
                Vector3? site = kind.Site == RuinSite.Near ? ByFixture(kind, ruin, placed, Free)
                    : Pick(ruin.Of(kind.Site), $"{caveKey}/ruin/{kind.Kind}/{n}", seed, site => Free(site, HomesApartMetres))
                    ?? Pick(ruin.OrElse(kind.Site), $"{caveKey}/ruin/{kind.Kind}/{n}/else", seed, site => Free(site, HomesApartMetres));
                if (site is not Vector3 at) continue;
                placed.Add((kind, at));
                n++;
            }
        }

        List<HerdRecord> homes = [];
        int index = 0;
        foreach ((RuinKind kind, Vector3 site) in placed)
        {
            if (!catalog.TryKind(kind.Kind, out WildlifeKind wild)) continue;
            string key = string.Create(CultureInfo.InvariantCulture, $"{kind.Kind}@{caveKey}/ruin#{index++}");
            homes.Add(new HerdRecord(key, kind.Kind, new Vector2(site.X, site.Z), Draw(key, seed, wild.GroupMinimum, wild.GroupMaximum)) { Floor = site.Y });
        }

        return homes;
    }

    /// <summary>A tender's place: by a placed fixture of its own that no tender yet keeps, the nearest free place within reach of it.</summary>
    private static Vector3? ByFixture(RuinKind tender, RuinSites ruin, List<(RuinKind Kind, Vector3 Site)> placed, Func<Vector3, float, bool> free)
    {
        foreach ((RuinKind fixture, Vector3 at) in placed.Where(entry => entry.Kind.Kind == tender.Near))
        {
            if (placed.Any(entry => entry.Kind.Site == RuinSite.Near && entry.Kind.Near == fixture.Kind && Vector3.Distance(entry.Site, at) <= TenderReachMetres)) continue;
            Vector3? best = ruin.AnyPlace.Where(site => Vector3.Distance(site, at) <= TenderReachMetres && free(site, HomesApartMetres))
                .OrderBy(site => Vector3.Distance(site, at)).Cast<Vector3?>().FirstOrDefault();
            if (best is not null) return best;
        }

        return null;
    }

    /// <summary>A free site from a list, from a keyed start.</summary>
    private static Vector3? Pick(IReadOnlyList<Vector3> sites, string key, ulong seed, Func<Vector3, bool> free)
    {
        if (sites.Count == 0) return null;
        int start = Draw(key, seed, 0, sites.Count - 1);
        for (int i = 0; i < sites.Count; i++)
        {
            Vector3 site = sites[(start + i) % sites.Count];
            if (free(site)) return site;
        }

        return null;
    }

    /// <summary>A keyed draw between a fewest and a most.</summary>
    internal static int Draw(string key, ulong seed, int fewest, int most)
    {
        if (most <= fewest) return fewest;
        ulong hash = seed ^ 0x5EED_0955UL;
        foreach (char c in key) hash = (hash ^ c) * 0x100_0000_01B3UL;
        return fewest + (int)(hash % (ulong)(most - fewest + 1));
    }
}
