using System.Runtime.CompilerServices;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a settlement is: the start's sea port, a river town, or the mountain hold at the neck.</summary>
internal enum SettlementKind { Port, Town, Hold }

/// <summary>Why a site was chosen, for the census and the place's description.</summary>
internal enum SettlementReason { Mouth, Harbour, Confluence, BoatHead, CanoeHead }

/// <summary>A chosen settlement: where it stands (world metres), on which reach, and why.</summary>
internal sealed record Settlement(string Name, SettlementKind Kind, double X, double Z, int Reach, SettlementReason Reason, double Score);

/// <summary>
/// Where the frontier's people live (#9875; owner direction 2026-10-12): civilisation follows the
/// waterways. The start is a major sea port at the mouth of the great river, on the calm west coast; a
/// few towns spread up the rivers from it - where rivers meet and where boats and canoes must turn back -
/// spaced a few days apart, off the floodplain. The mountains are away from civilisation: the neck keeps
/// only its hold, a site its design reserves. Sites are a pure function of the map (nothing is saved).
/// </summary>
internal static class Settlements
{
    /// <summary>How many river towns, besides the port.</summary>
    internal const int TownCount = 5;

    /// <summary>The least distance between any two settlements (m): a day or more on foot.</summary>
    internal const double SpacingMetres = 25_000;

    /// <summary>A port wants calm climate (temperature 0 polar to 1 tropical); a town avoids the frozen north.</summary>
    private const double CalmCoolest = 0.4, CalmWarmest = 0.9, TownCoolest = 0.2;

    /// <summary>Weights on a candidate's flow: the kind of place, and whether it can be reached from the sea by canoe.</summary>
    private const double ConfluenceWeight = 1, BoatHeadWeight = 0.8, CanoeHeadWeight = 0.5, HarbourWeight = 0.6;
    private const double FromSeaWeight = 1.5;

    /// <summary>A settlement stands on dry ground off the floodplain within this many lattice cells of its candidate.</summary>
    private const int StandSearchCells = 8;
    private const double DryMetres = 1;

    private const ulong NameSalt = 0x3C6EF372FE94F82BUL;
    private const int NameStep = 7;

    private static readonly string[] PortNames =
        ["Saltmouth", "Westharbour", "Greyhaven", "Brackwater", "Tidesend", "Gullhaven", "Merrow Quay", "Seagate", "Anchorage", "Kelpmouth"];

    private static readonly string[] TownNames =
        ["Ashford", "Two Waters", "Millbrook", "Fennick", "Kingsferry", "Reedmarsh", "Highwater", "Oxbow", "Stonebridge", "Willowbank",
         "Coldwater", "Hartsford", "Alder Reach", "Larkspur", "Wending", "Otterholt", "Barrow Landing", "Sallow", "Fallow Ford", "Weirton"];

    private static readonly ConditionalWeakTable<WorldMap, IReadOnlyList<Settlement>> Chosen = [];

    /// <summary>A designed continent's settlements, chosen once per map; none for a world without a design.</summary>
    internal static IReadOnlyList<Settlement> For(WorldMap map) =>
        map.RegionFrame is null && map.Scale.Continental && ContinentDesign.For(map.Configuration.Size) is ContinentDesign design
            ? Chosen.GetValue(map, continent => Choose(continent, design, MapRegions.For(continent).Drainage))
            : [];

    internal static IReadOnlyList<Settlement> Choose(WorldMap continent, ContinentDesign? design, MapDrainage drainage)
    {
        WaterNetwork network = drainage.Network;
        MapGrid grid = network.Grid;
        double radius = continent.Radius;
        bool Playable(double x, double z) => design is null
            || (design.LandAt(x / radius, z / radius) is not { Forbidden: true } && !design.BeyondFrontier(x / radius, z / radius) && !design.InNeck(x / radius, z / radius));
        double Temperature(int node) => continent.Sample(grid.X(node), grid.Z(node)).Temperature;
        // A reach's last land node: its outlet is the sea at a mouth, the confluence otherwise.
        int Shore(Reach reach) => reach.Nodes.Length > 1 && reach.Mouth ? reach.Nodes[^2] : reach.Outlet;

        Reach? port = network.Mouths
            .Where(reach => reach.Class >= Navigability.Canoe && Playable(grid.X(Shore(reach)), grid.Z(Shore(reach)))
                && Temperature(Shore(reach)) is >= CalmCoolest and <= CalmWarmest)
            .OrderByDescending(reach => reach.Flow).FirstOrDefault()
            ?? network.Mouths.Where(reach => Playable(grid.X(Shore(reach)), grid.Z(Shore(reach)))).OrderByDescending(reach => reach.Flow).FirstOrDefault();
        if (port is null) return [];

        List<(int Node, int Reach, SettlementReason Reason, double Score)> candidates = [];
        double Weigh(int reach, double weight) =>
            weight * network.Reaches[reach].Flow * (network.CanoeFromSea(reach) ? FromSeaWeight : 1);
        foreach (Confluence confluence in network.Confluences)
            if (confluence.Inflows.Count(r => network.Reaches[r].Class >= Navigability.Canoe) >= 2)
                candidates.Add((confluence.Node, confluence.Outflow, SettlementReason.Confluence, Weigh(confluence.Outflow, ConfluenceWeight)));
        foreach (int head in network.BoatHeads)
            candidates.Add((network.Reaches[head].Source, head, SettlementReason.BoatHead, Weigh(head, BoatHeadWeight)));
        foreach (int head in network.CanoeHeads.Except(network.BoatHeads))
            candidates.Add((network.Reaches[head].Source, head, SettlementReason.CanoeHead, Weigh(head, CanoeHeadWeight)));
        foreach (Reach mouth in network.Mouths)
            if (mouth.Id != port.Id && mouth.Class >= Navigability.Boat)
                candidates.Add((Shore(mouth), mouth.Id, SettlementReason.Harbour, Weigh(mouth.Id, HarbourWeight)));

        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed ^ NameSalt;
        List<Settlement> chosen = [];
        // The design's hold stands apart too: no river town crowds it.
        (double X, double Z)[] holds = [.. (FrontierBound.For(continent)?.Sites ?? []).Where(site => site.Kind == nameof(SettlementKind.Hold)).Select(site => (site.X, site.Z))];
        bool Crowded(double x, double z) =>
            chosen.Any(s => Math.Sqrt(((s.X - x) * (s.X - x)) + ((s.Z - z) * (s.Z - z))) < SpacingMetres)
            || holds.Any(h => Math.Sqrt(((h.X - x) * (h.X - x)) + ((h.Z - z) * (h.Z - z))) < SpacingMetres);
        if (Stand(network, drainage, Shore(port)) is int portSite)
            chosen.Add(new(Pick(PortNames, seed, 0), SettlementKind.Port, grid.X(portSite), grid.Z(portSite), port.Id, SettlementReason.Mouth, port.Flow));
        int named = 0;
        // Towns spread up the port's own river first, as many as its spacing allows; only then elsewhere.
        foreach ((int node, int reach, SettlementReason reason, double score) in candidates
            .OrderByDescending(c => network.MouthOf(c.Reach) == port.Id).ThenByDescending(c => c.Score).ThenBy(c => c.Node))
        {
            if (chosen.Count >= TownCount + 1) break;
            double x = grid.X(node), z = grid.Z(node);
            if (!Playable(x, z) || Temperature(node) < TownCoolest) continue;
            if (Crowded(x, z)) continue;
            if (Stand(network, drainage, node) is not int site) continue;
            chosen.Add(new(Pick(TownNames, seed, ++named), SettlementKind.Town, grid.X(site), grid.Z(site), reach, reason, score));
        }

        return chosen;
    }

    /// <summary>The nearest dry lattice node off the floodplain to a candidate, or null if there is none near.</summary>
    private static int? Stand(WaterNetwork network, MapDrainage drainage, int node)
    {
        MapGrid grid = network.Grid;
        int cx = node % grid.Side, cz = node / grid.Side, best = -1;
        int bestDistance = int.MaxValue;
        for (int dz = -StandSearchCells; dz <= StandSearchCells; dz++)
        for (int dx = -StandSearchCells; dx <= StandSearchCells; dx++)
        {
            int x = cx + dx, z = cz + dz, distance = (dx * dx) + (dz * dz);
            if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments || distance >= bestDistance) continue;
            int i = (z * grid.Side) + x;
            if (drainage.ElevationAt(i) < GenerationConstants.WaterLevel + DryMetres || network.OnFloodplain(i)) continue;
            best = i;
            bestDistance = distance;
        }

        return best < 0 ? null : best;
    }

    /// <summary>A name from a list, drawn from the seed without repeating within a world.</summary>
    private static string Pick(string[] names, ulong seed, int index)
    {
        // A step of 7 is coprime to both lists' lengths, so a world never draws the same name twice.
        int offset = Math.Clamp((int)(MapNoise.Unit(seed, 0, 0) * names.Length), 0, names.Length - 1);
        return names[(offset + (index * NameStep)) % names.Length];
    }
}
