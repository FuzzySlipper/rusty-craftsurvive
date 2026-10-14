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
    /// <summary>The bankfull stage and where a settlement stands, as pure rules (#9875 review).</summary>
    internal static void Rules()
    {
        // A canoe river at the least depth the sizing allows still rises to its bank in a flood.
        double canoe = WaterNetwork.CanoeFlow, width = 2 * RiverGeometry.HalfWidth(canoe);
        double bank = RiverGeometry.Stage(3 * canoe, width, 0.001) - RiverGeometry.Stage(canoe, width, 0.001);
        Check.That(RiverGeometry.Depth(canoe) == RiverGeometry.MinimumDepth && bank > 0.3,
            $"a clamped-depth canoe river still has a bank to its bankfull stage ({bank:F2} m)");
        double gentle = RiverGeometry.Stage(800, 64, 0.0003) - RiverGeometry.Stage(200, 64, 0.0003);
        double steep = RiverGeometry.Stage(800, 64, 0.003) - RiverGeometry.Stage(200, 64, 0.003);
        double wide = RiverGeometry.Stage(800, 128, 0.0003) - RiverGeometry.Stage(200, 128, 0.0003);
        Check.That(steep < gentle && wide < gentle, $"a steeper or wider channel rises less to the same flood ({gentle:F2} m, steeper {steep:F2} m, wider {wide:F2} m)");

        // A settlement moved off its river keeps every rule where it ends up: the nearest dry cell lies
        // inside another settlement's spacing, so it stands on the nearest cell outside it instead.
        MapGrid grid = new(20, 250, 2500);
        int candidate = (10 * grid.Side) + 10;
        double crowdX = grid.X(candidate) - 300, crowdZ = grid.Z(candidate);
        bool Usable(int i) => i != candidate && Math.Sqrt(Math.Pow(grid.X(i) - crowdX, 2) + Math.Pow(grid.Z(i) - crowdZ, 2)) >= 500;
        int? stood = Settlements.Stand(grid, candidate, Usable);
        Check.That(stood is int site && Usable(site) && site == candidate + 1,
            $"a relocated settlement stands on the nearest cell where every rule holds ({stood})");
    }

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
        Check.That(ports.Length == 1 && network.Reaches[ports[0].Reach].Mouth && network.Reaches[ports[0].Reach].SeaNode >= 0, "one sea port stands at a river's mouth on the open sea");
        Check.That(towns.Length == Settlements.TownCount, $"the continent has its {Settlements.TownCount} river towns ({towns.Length})");
        double radius = continent.Radius;
        Check.That(settlements.All(s => !design.InNeck(s.X / radius, s.Z / radius) && !design.BeyondFrontier(s.X / radius, s.Z / radius)
            && design.LandAt(s.X / radius, s.Z / radius) is not { Forbidden: true }), "every settlement stands on the playable peninsula, out of the neck");
        // Settlements are chosen on dry ground off the floodplain, spaced apart; each town then stands at a
        // seat beside the water it serves (#9895), so the choice rules hold at the chosen sites.
        IReadOnlyList<Settlement> chosen = Settlements.Choose(continent, design, drainage);
        Check.That(chosen.All(s => !network.OnFloodplain(grid.Nearest(s.X, s.Z)) && drainage.ElevationAt(grid.Nearest(s.X, s.Z)) >= GenerationConstants.WaterLevel),
            "every settlement is chosen on dry ground off the floodplain");
        bool spaced = true;
        for (int a = 0; a < chosen.Count; a++)
            for (int b = a + 1; b < chosen.Count; b++)
                spaced &= Math.Sqrt(Math.Pow(chosen[a].X - chosen[b].X, 2) + Math.Pow(chosen[a].Z - chosen[b].Z, 2)) >= Settlements.SpacingMetres;
        foreach (PlacedSite hold in FrontierBound.For(continent)!.Sites.Where(site => site.Kind == nameof(SettlementKind.Hold)))
            spaced &= chosen.Where(t => t.Kind == SettlementKind.Town).All(t => Math.Sqrt(Math.Pow(t.X - hold.X, 2) + Math.Pow(t.Z - hold.Z, 2)) >= Settlements.SpacingMetres);
        Check.That(spaced, $"settlements, and the hold, are chosen at least {Settlements.SpacingMetres / 1000:F0} km apart");
        Check.That(settlements.Select(s => s.Name).Distinct().Count() == settlements.Count, "every settlement has its own name");
        Check.That(Settlements.Choose(continent, design, drainage).SequenceEqual(chosen), "settlements are chosen the same way every time");
        Check.That(settlements.Select(s => s with { X = 0, Z = 0, Ground = default }).SequenceEqual(chosen.Select(s => s with { X = 0, Z = 0, Ground = default })),
            "seating moves a settlement without changing which it is");
        bool seated = true;
        foreach ((Settlement site, Settlement seat) in chosen.Zip(settlements))
        {
            TownGround ground = seat.Ground;
            RiverPoint[] course = drainage.Shaped(seat.Reach);
            double fromSite = Math.Sqrt(Math.Pow(seat.X - site.X, 2) + Math.Pow(seat.Z - site.Z, 2));
            // The waterfront, a waterfront's width toward the water from the seat, is at the river's bank.
            double frontX = seat.X + (ground.FacingX * TownGround.Waterfront), frontZ = seat.Z + (ground.FacingZ * TownGround.Waterfront);
            RiverPoint nearest = course.MinBy(p => Math.Pow(p.X - frontX, 2) + Math.Pow(p.Z - frontZ, 2));
            double toCentre = Math.Sqrt(Math.Pow(nearest.X - frontX, 2) + Math.Pow(nearest.Z - frontZ, 2));
            seated &= ground.X == seat.X && ground.Z == seat.Z && Math.Abs(Math.Sqrt((ground.FacingX * ground.FacingX) + (ground.FacingZ * ground.FacingZ)) - 1) < 1e-9
                && fromSite <= Settlements.StandReachMetres + MapDrainage.Spacing + nearest.HalfWidth && toCentre <= nearest.HalfWidth + TownGround.FlatMetres
                && ground.Height >= GenerationConstants.WaterLevel + TownGround.QuayMetres - 1e-9;
        }

        Check.That(seated, "each town is seated on its reach's bank, facing the water, its pad a quay's height over it");
        Check.That(towns.Count(t => network.MouthOf(t.Reach) == ports[0].Reach) >= 1, "towns spread up the port's own river first");
        Check.That(ports[0].Shelter is >= 0 and <= 1 && ports[0].Flat is >= 0 and <= 1 && ports[0].Score > 0, "the port is scored on its shelter and flat land as well as its flow");
        Console.WriteLine($"network: {network.Reaches.Count} reaches, {network.Mouths.Count()} mouths, {network.Confluences.Count} confluences, heads boat {network.BoatHeads.Count} canoe {network.CanoeHeads.Count}; "
            + string.Join("; ", settlements.Select(s => $"{s.Name} {s.Kind} {s.Reason} {network.Reaches[s.Reach].Flow:F0} m3/s at ({s.X / 1000:F0},{s.Z / 1000:F0}) km")));
    }
}
