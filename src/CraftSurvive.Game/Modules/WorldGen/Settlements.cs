using System.Runtime.CompilerServices;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a settlement is: the start's sea port, a river town, or the mountain hold at the neck.</summary>
internal enum SettlementKind { Port, Town, Hold }

/// <summary>Why a site was chosen, for the census and the place's description.</summary>
internal enum SettlementReason { Mouth, Harbour, Confluence, BoatHead, CanoeHead }

/// <summary>
/// A chosen settlement: where it stands (world metres), on which reach, why and its score. A port also
/// carries its shelter from the open sea and the flat land about it (0 to 1), and whether its river is
/// the largest of the harbours it was chosen from.
/// </summary>
internal sealed record Settlement(string Name, SettlementKind Kind, double X, double Z, int Reach, SettlementReason Reason, double Score,
    double Shelter = 0, double Flat = 0, bool LargestFlow = true);

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

    /// <summary>
    /// A port's shelter: one less its exposure, the mean fetch of open water from its mouth over every
    /// bearing as a share of this cap. Its
    /// flat land: the share of land within a few cells no steeper than a gentle gradient. The score is the
    /// flow, doubled at most for shelter, and taken down to a share of itself on broken ground.
    /// </summary>
    private const int ShelterBearings = 32;
    private const double OpenSeaMetres = 40_000;
    private const int FlatCells = 8;
    private const double FlatGradient = 0.03, ShelterWeight = 1, FlatFloor = 0.4;

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

        bool Dry(int i) => drainage.ElevationAt(i) >= GenerationConstants.WaterLevel + DryMetres && !network.OnFloodplain(i);
        // Exposure: the mean fetch (open water before land, up to a cap) over every bearing, as a share of
        // the cap. An open coast is about a half; a bay, an estuary or water behind islands much less.
        double Exposure(int node)
        {
            double fetch = 0;
            for (int b = 0; b < ShelterBearings; b++)
            {
                double angle = 2 * Math.PI * b / ShelterBearings, dx = Math.Cos(angle), dz = Math.Sin(angle), d = grid.Spacing;
                for (; d <= OpenSeaMetres; d += grid.Spacing)
                {
                    double x = grid.X(node) + (dx * d), z = grid.Z(node) + (dz * d);
                    if (Math.Abs(x) > grid.Radius || Math.Abs(z) > grid.Radius) { d = OpenSeaMetres; break; }
                    // The continent's broad sea, without river channels: the drainage lattice's heights carry local
                    // relief over the water, and a sample's carved channel would wall the mouth in.
                    if (continent.Broad(x, z).Elevation >= GenerationConstants.WaterLevel) break;
                }

                fetch += Math.Min(d, OpenSeaMetres);
            }

            return fetch / ShelterBearings / OpenSeaMetres;
        }

        double Flatness(int node)
        {
            int cx = node % grid.Side, cz = node / grid.Side, land = 0, flat = 0;
            int[] offsets = grid.Offsets();
            for (int dz = -FlatCells; dz <= FlatCells; dz++)
            for (int dx = -FlatCells; dx <= FlatCells; dx++)
            {
                int x = cx + dx, z = cz + dz;
                if (x <= 0 || z <= 0 || x >= grid.Segments || z >= grid.Segments || (dx * dx) + (dz * dz) > FlatCells * FlatCells) continue;
                int i = (z * grid.Side) + x;
                if (drainage.ElevationAt(i) < GenerationConstants.WaterLevel) continue;
                land++;
                double steepest = offsets.Max(o => Math.Abs(drainage.ElevationAt(i + o) - drainage.ElevationAt(i)));
                if (steepest / grid.Spacing <= FlatGradient) flat++;
            }

            return land == 0 ? 0 : flat / (double)land;
        }

        // The port: a calm, playable river mouth, scored on its river's flow, its shelter from the open
        // sea and the flat land about it for a city (#9875 review).
        List<(Reach Reach, double Shelter, double Flat, double Score)> harbours = [.. network.Mouths
            .Where(reach => reach.Class >= Navigability.Canoe && Playable(grid.X(Shore(reach)), grid.Z(Shore(reach)))
                && Temperature(Shore(reach)) is >= CalmCoolest and <= CalmWarmest)
            .Select(reach =>
            {
                double shelter = 1 - Exposure(reach.SeaNode), flat = Flatness(Shore(reach));
                return (reach, shelter, flat, reach.Flow * (1 + (ShelterWeight * shelter)) * (FlatFloor + ((1 - FlatFloor) * flat)));
            })
            .OrderByDescending(h => h.Item4).ThenBy(h => h.reach.Id)];
        if (harbours.Count == 0)
            harbours = [.. network.Mouths.Where(reach => Playable(grid.X(Shore(reach)), grid.Z(Shore(reach))))
                .OrderByDescending(reach => reach.Flow).Select(reach => (reach, 0.0, 0.0, reach.Flow))];
        double largestFlow = harbours.Count == 0 ? 0 : harbours.Max(h => h.Reach.Flow);

        ulong seed = continent.Configuration.Contract.GeographyNoiseSeed ^ NameSalt;
        List<Settlement> chosen = [];
        // The design's hold stands apart too: no river town crowds it.
        (double X, double Z)[] holds = [.. (FrontierBound.For(continent)?.Sites ?? []).Where(site => site.Kind == nameof(SettlementKind.Hold)).Select(site => (site.X, site.Z))];
        bool Crowded(double x, double z) =>
            chosen.Any(s => Math.Sqrt(((s.X - x) * (s.X - x)) + ((s.Z - z) * (s.Z - z))) < SpacingMetres)
            || holds.Any(h => Math.Sqrt(((h.X - x) * (h.X - x)) + ((h.Z - z) * (h.Z - z))) < SpacingMetres);
        // Every rule holds where a settlement finally stands, not only at the river it was drawn to (#9875 review).
        bool Usable(int i) => Dry(i) && Playable(grid.X(i), grid.Z(i)) && !Crowded(grid.X(i), grid.Z(i));

        Reach? port = null;
        foreach ((Reach reach, double shelter, double flat, double score) in harbours)
        {
            if (Stand(grid, Shore(reach), Usable) is not int site) continue;
            chosen.Add(new(Pick(PortNames, seed, 0), SettlementKind.Port, grid.X(site), grid.Z(site), reach.Id, SettlementReason.Mouth, score,
                shelter, flat, reach.Flow >= largestFlow));
            port = reach;
            break;
        }

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

        int named = 0;
        // Towns spread up the port's own river first, as many as its spacing allows; only then elsewhere.
        foreach ((int node, int reach, SettlementReason reason, double score) in candidates
            .OrderByDescending(c => network.MouthOf(c.Reach) == port.Id).ThenByDescending(c => c.Score).ThenBy(c => c.Node))
        {
            if (chosen.Count >= TownCount + 1) break;
            if (Temperature(node) < TownCoolest || Stand(grid, node, Usable) is not int site) continue;
            chosen.Add(new(Pick(TownNames, seed, ++named), SettlementKind.Town, grid.X(site), grid.Z(site), reach, reason, score));
        }

        return chosen;
    }

    /// <summary>The nearest lattice node to a candidate (within a few cells) where a settlement may stand, or null if none is near.</summary>
    internal static int? Stand(MapGrid grid, int node, Func<int, bool> usable)
    {
        int cx = node % grid.Side, cz = node / grid.Side, best = -1;
        int bestDistance = int.MaxValue;
        for (int dz = -StandSearchCells; dz <= StandSearchCells; dz++)
        for (int dx = -StandSearchCells; dx <= StandSearchCells; dx++)
        {
            int x = cx + dx, z = cz + dz, distance = (dx * dx) + (dz * dz);
            if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments || distance >= bestDistance) continue;
            int i = (z * grid.Side) + x;
            if (!usable(i)) continue;
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
