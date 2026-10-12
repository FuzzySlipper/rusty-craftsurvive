using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The water network as data and the settlements chosen on it (#9875), on the default continent: reaches
/// join downstream with their flow growing, navigability follows the thresholds, the heads of navigation end
/// what the sea reaches, the floodplain is land; one sea port and the river towns stand on the playable
/// peninsula off the floodplain, spaced apart, the same every time.
/// </summary>
internal static class NetworkChecks
{
    internal static void Run(WorldMap continent)
    {
        MapDrainage drainage = MapRegions.For(continent).Drainage;
        WaterNetwork network = drainage.Network;
        MapGrid grid = network.Grid;
        Check.That(network.Reaches.Count > 0 && network.Mouths.Any(), $"the network has reaches ({network.Reaches.Count}) and mouths");

        bool joined = true, growing = true, classes = true, routing = true;
        foreach (Reach reach in network.Reaches)
        {
            if (reach.Downstream >= 0)
            {
                Reach next = network.Reaches[reach.Downstream];
                joined &= next.Source == reach.Outlet && !reach.Mouth;
                growing &= next.Flow + 1e-9 >= reach.Flow;
            }

            joined &= !reach.Mouth || drainage.ElevationAt(reach.Outlet) < GenerationConstants.WaterLevel;
            classes &= reach.Class == Navigability.None || reach.Flow >= WaterNetwork.CanoeFlow;
            classes &= reach.Class < Navigability.Boat || reach.Flow >= WaterNetwork.BoatFlow;
            classes &= reach.Class < Navigability.Great || reach.Flow >= WaterNetwork.GreatFlow;
            routing &= reach.BankfullFlow >= reach.Flow && reach.BankHeight >= 0 && reach.StorageHours > 0 && reach.Velocity > 0 && reach.LengthMetres > 0;
        }

        Check.That(joined, "each reach flows into the reach starting at its outlet, and a mouth's outlet is the sea");
        Check.That(growing, "flow never falls downstream");
        Check.That(classes, "navigability follows the least flows a canoe, a boat and a great river need");
        Check.That(routing, "every reach carries what routing the stage needs: bankfull flow over the mean, a bank, a speed and a storage time");

        bool heads = network.BoatHeads.All(h => network.BoatFromSea(h) && network.CanoeFromSea(h))
            && network.Reaches.Where(r => r.Downstream >= 0 && network.BoatFromSea(r.Id)).All(r => network.BoatFromSea(r.Downstream))
            && network.BoatHeads.All(h => !network.Reaches.Any(r => r.Downstream == h && network.BoatFromSea(r.Id)));
        Check.That(heads, "a boat reach is reached from the sea through boat reaches, and a head of navigation has none above it");
        bool plainIsLand = true;
        for (int i = 0; i < grid.Count; i++) plainIsLand &= !network.OnFloodplain(i) || drainage.ElevationAt(i) >= GenerationConstants.WaterLevel;
        Check.That(plainIsLand, "the floodplain is land");

        IReadOnlyList<Settlement> settlements = Settlements.For(continent);
        ContinentDesign design = ContinentDesign.For(continent.Configuration.Size)!;
        Settlement[] ports = [.. settlements.Where(s => s.Kind == SettlementKind.Port)], towns = [.. settlements.Where(s => s.Kind == SettlementKind.Town)];
        Check.That(ports.Length == 1 && network.Reaches[ports[0].Reach].Mouth, "one sea port stands at a river's mouth");
        Check.That(towns.Length == Settlements.TownCount, $"the continent has its {Settlements.TownCount} river towns ({towns.Length})");
        double radius = continent.Radius;
        Check.That(settlements.All(s => !design.InNeck(s.X / radius, s.Z / radius) && !design.BeyondFrontier(s.X / radius, s.Z / radius)
            && design.LandAt(s.X / radius, s.Z / radius) is not { Forbidden: true }), "every settlement stands on the playable peninsula, out of the neck");
        Check.That(settlements.All(s => !network.OnFloodplain(grid.Nearest(s.X, s.Z)) && drainage.ElevationAt(grid.Nearest(s.X, s.Z)) >= GenerationConstants.WaterLevel),
            "every settlement stands on dry ground off the floodplain");
        bool spaced = true;
        for (int a = 0; a < settlements.Count; a++)
            for (int b = a + 1; b < settlements.Count; b++)
                spaced &= Math.Sqrt(Math.Pow(settlements[a].X - settlements[b].X, 2) + Math.Pow(settlements[a].Z - settlements[b].Z, 2)) >= Settlements.SpacingMetres;
        foreach (PlacedSite hold in FrontierBound.For(continent)!.Sites.Where(site => site.Kind == nameof(SettlementKind.Hold)))
            spaced &= towns.All(t => Math.Sqrt(Math.Pow(t.X - hold.X, 2) + Math.Pow(t.Z - hold.Z, 2)) >= Settlements.SpacingMetres);
        Check.That(spaced, $"settlements, and the hold, stand at least {Settlements.SpacingMetres / 1000:F0} km apart");
        Check.That(settlements.Select(s => s.Name).Distinct().Count() == settlements.Count, "every settlement has its own name");
        Check.That(Settlements.Choose(continent, design, drainage).SequenceEqual(settlements), "settlements are chosen the same way every time");
        Check.That(towns.Count(t => network.MouthOf(t.Reach) == ports[0].Reach) >= 1, "towns spread up the port's own river first");
        Console.WriteLine($"network: {network.Reaches.Count} reaches, {network.Mouths.Count()} mouths, {network.Confluences.Count} confluences, heads boat {network.BoatHeads.Count} canoe {network.CanoeHeads.Count}; "
            + string.Join("; ", settlements.Select(s => $"{s.Name} {s.Kind} {s.Reason} {network.Reaches[s.Reach].Flow:F0} m3/s at ({s.X / 1000:F0},{s.Z / 1000:F0}) km")));
    }
}
