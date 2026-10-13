using System.Numerics;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Tests;

/// <summary>Overland travel rules: cell costs, routes, journey time and night travel (#9467).</summary>
internal static class TravelChecks
{
    /// <summary>A cross-continent route must plan within this, on a slow shared runner too (#9552).</summary>
    private const double CrossContinentPlanBudgetMs = 1000;
    /// <summary>
    /// A crossing from the frontier peninsula's west interior to its south-east interior (#9815): both deep
    /// inside its drawn coast, so land on every seed, about 233 km apart.
    /// </summary>
    private static readonly Vector2 CrossFrom = new(-135_000, 20_000), CrossTo = new(90_000, 80_000);
    private const int RefineWaitMs = 90_000;
    private const float TravelProbeEast = 20_000, TravelProbeNorth = 8_000;
    private const int FastestSpeed = 4;
    private const double TickSeconds = 0.05;
    /// <summary>A refined leg spans a diagonal region cell, or reaches from one to the kilometre node it joins.</summary>
    private const float RefinedLegCells = 2.5f;

    /// <summary>
    /// Travel across a continent (#9552): a route from one side to the other plans quickly over the
    /// kilometre lattice, previews in days, refines its head over region ground, and a travelling
    /// party re-refines as it reaches the end of that head without losing its place.
    /// </summary>
    internal static void Continental()
    {
        WorldMap continent = WorldMapGenerator.Generate(new TerrainConfiguration(12345, MapScale.DefaultContinentalSize));
        MapRegions regions = MapRegions.For(continent);
        regions.Tile((0, 0));
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        TravelCostModel cost = new(continent);
        double costMs = clock.Elapsed.TotalMilliseconds;
        clock.Restart();
        TravelRoute? across = TravelRouter.Plan(cost, CrossFrom, CrossTo, "far shore");
        double planMs = clock.Elapsed.TotalMilliseconds;
        Console.WriteLine($"continent travel: costMs={costMs:F0} planMs={planMs:F0} km={(across?.Metres ?? 0) / 1000:F0} days={TravelCalendar.Days(across?.Hours ?? 0):F0} daylightHoursPerDay={TravelCalendar.DaylightHoursPerDay:F1}");
        Check.That(across is not null && across.Metres >= Vector2.Distance(CrossFrom, CrossTo), "a route crosses the continent");
        Check.That(planMs < CrossContinentPlanBudgetMs, $"a cross-continent route plans within its budget ({planMs:F0} ms)");
        Check.That(across is not null && TravelCalendar.Describe(across.Metres, across.Hours).Contains("days", StringComparison.Ordinal)
            && TravelCalendar.Days(across.Hours) >= 30, "a crossing previews in days, and takes at least a month");

        // From the arrival tile the head follows 32 m ground: legs of a region cell, the last joining the kilometre node.
        TravelRoute? near = TravelRouter.Plan(cost, new(0, 0), new(TravelProbeEast, TravelProbeNorth), "east");
        Check.That(near is { RefinedLegs: > 0 } && near.Points.Take(near.RefinedLegs).Zip(near.Points.Skip(1).Take(near.RefinedLegs))
            .All(p => Vector2.Distance(p.First, p.Second) <= MapGrid.TargetSpacing * RefinedLegCells),
            $"a route's head is refined over the region tile the party stands in ({near?.RefinedLegs ?? 0} legs)");
        Check.That(near is not null && near.Points.Zip(near.Points.Skip(1)).All(p => float.IsFinite(p.First.X) && Vector2.Distance(p.First, p.Second) <= continent.Spacing * 1.5f + 1),
            "the refined head and the kilometre route join without a gap");

        // A travelling party at the fastest speed, paced in real time as the product's updates pace it,
        // does what AdvanceTravel does each update: prefetch ahead, then refine. Nothing it reads
        // may build a tile on its own thread, and its head is re-refined as it goes.
        PartyTravel party = new(new(0, 0));
        Check.That(party.Plan(cost, new(TravelProbeEast, TravelProbeNorth), "east") && party.Begin(), "a party sets out across the continent");
        int refinements = 0, synchronousBefore = regions.SynchronousBuilds;
        clock.Restart();
        while (party.State == TravelState.Travelling && clock.ElapsedMilliseconds < RefineWaitMs)
        {
            Vector2 before = party.Position;
            party.Advance(PartyTravel.HoursPerSecond * FastestSpeed * TickSeconds, _ => false);
            regions.PrefetchAhead(party.Position, party.Route?.Points);
            if (party.Refine(cost))
            {
                refinements++;
                Check.That(Vector2.Distance(party.Route!.Points[0], party.Position) < 1, "refinement starts where the party stands");
            }
            Thread.Sleep((int)(TickSeconds * 1000));
        }
        Console.WriteLine($"continental journey: state={party.State} refinements={refinements} synchronousBuilds={regions.SynchronousBuilds - synchronousBefore} ms={clock.ElapsedMilliseconds}");
        Check.That(party.State == TravelState.Arrived && refinements >= 1, $"the party arrives, refining its route as it travels ({refinements})");
        Check.That(regions.SynchronousBuilds == synchronousBefore, "ordinary travel never waits on a region tile: every tile it reads was built ahead");
        // A walk down to the water is refined once, and the party reaches the water (#9879): refining the same short
        // walk again at its last leg would set the party back to its nearest region node, over and over.
        TravelRoute toWater = new("downriver", [new(0, 0), new(400, 0), new(1400, 0), new(2400, 0)], [0.8, 1, 1], 0,
            [LegMode.Walk, LegMode.Drift, LegMode.Drift]);
        PartyTravel boating = new(new(0, 0));
        boating.Take(toWater);
        Check.That(boating.Begin(), "a party sets out for the water");
        int walkRefinements = 0;
        for (int tick = 0; tick < 400 && !boating.Afloat && boating.State == TravelState.Travelling; tick++)
        {
            boating.Advance(PartyTravel.HoursPerSecond * TickSeconds, _ => false);
            if (boating.Refine(cost)) walkRefinements++;
        }

        Check.That(boating.Afloat && walkRefinements <= 1, $"a party walking to its boat reaches the water, its walk refined once ({walkRefinements})");
    }

    /// <summary>
    /// The two scales agree (#9553): first-person walking tires the expedition at the map's rate per
    /// distance, ground the map will not route still tires a walker, and a preview gives both paces.
    /// </summary>
    internal static void Walking()
    {
        PartyTravel walker = new(Vector2.Zero);
        walker.Walk(TravelCostModel.MetresPerHour, 1);
        Check.That(Math.Abs(walker.Fatigue - PartyTravel.FatiguePerHour) < 1e-9, "walking an hour's map distance on open ground tires as an hour's march does");
        walker.Walk(TravelCostModel.MetresPerHour, 2);
        Check.That(Math.Abs(walker.Fatigue - 3 * PartyTravel.FatiguePerHour) < 1e-9, "rough country tires a walker as it slows a march");
        walker.Rest(PartyTravel.FullRestHours);
        Check.That(walker.Fatigue == 0, "a night's rest restores a walker as it does a march");

        WorldMap map = WorldMapGenerator.Generate(new TerrainConfiguration(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize));
        TravelCostModel cost = new(map);
        int sea = Enumerable.Range(0, map.Grid.Count).First(i => !cost.Passable(i));
        double atSea = cost.MultiplierAt(new((float)map.Grid.X(sea), (float)map.Grid.Z(sea)));
        Check.That(double.IsFinite(atSea) && atSea > 1, "ground the map does not route across still tires a walker");
        int land = Enumerable.Range(0, map.Grid.Count).First(cost.Passable);
        Check.That(cost.MultiplierAt(new((float)map.Grid.X(land), (float)map.Grid.Z(land))) == cost.Multiplier(land), "the cost under a walker is the map's cost there");

        string shortTrip = TravelCalendar.Describe(3_000, 6), longTrip = TravelCalendar.Describe(300_000, 700);
        Console.WriteLine($"previews: {shortTrip} | {longTrip}");
        Check.That(shortTrip.Contains("h by map", StringComparison.Ordinal) && shortTrip.Contains("h on foot", StringComparison.Ordinal)
            && longTrip.Contains("days by map", StringComparison.Ordinal) && longTrip.Contains("days on foot", StringComparison.Ordinal), "a route previews both paces");
    }

    internal static void Run()
    {
        WorldMap map = WorldMapGenerator.Generate(new TerrainConfiguration(TerrainConstants.DefaultSeed, TerrainConstants.DefaultSize));
        TravelCostModel cost = new(map);
        MapGrid grid = map.Grid;

        int sea = Enumerable.Range(0, grid.Count).First(i => map.Fields.Elevation[i] < GenerationConstants.WaterLevel);
        Check.That(!cost.Passable(sea), "the sea is not crossed overland");
        Check.That(TravelCostModel.Environment(MapBiome.TemperateForest) > TravelCostModel.Environment(MapBiome.Grassland)
            && TravelCostModel.Environment(MapBiome.Alpine) > TravelCostModel.Environment(MapBiome.Desert), "rough country costs more than open country");
        Check.That(Enumerable.Range(0, grid.Count).Where(cost.Passable).All(i => cost.Multiplier(i) >= 1), "no cell is faster than open ground");

        // A route between two representative sites: contiguous, overland, and never cheaper than the straight line.
        MapSite from = map.Sites[0], to = map.Sites[^1];
        TravelRoute? route = TravelRouter.Plan(cost, new((float)from.X, (float)from.Z), new((float)to.X, (float)to.Z), to.Name);
        Check.That(route is not null, $"a route joins {from.Name} and {to.Name}");
        if (route is not null)
        {
            Check.That(route.Points.Zip(route.Points.Skip(1)).All(p => Vector2.Distance(p.First, p.Second) <= grid.Spacing * 1.5 + 1
                || p.First == route.Points[0] || p.Second == route.Points[^1]), "route legs join neighbouring cells");
            Check.That(route.Points.Skip(1).SkipLast(1).All(p => cost.Passable(TravelRouter.Nearest(cost, p))), "the route stays on passable ground");
            double straight = Vector2.Distance(route.Points[0], route.Points[^1]) / TravelCostModel.MetresPerHour;
            Check.That(route.Hours >= straight * 0.95, "journey time is never less than the straight line at open-ground pace");
            Console.WriteLine($"route {from.Name} -> {to.Name}: {route.Metres / 1000:F1} km, {route.Hours:F1} h (straight {straight:F1} h), {route.Points.Length} points");

            // Night is allowed but very slow: the same hours of night cover a third of the ground.
            PartyTravel day = new(route.Points[0]), night = new(route.Points[0]);
            day.Plan(cost, route.Points[^1], to.Name); night.Plan(cost, route.Points[^1], to.Name);
            day.Begin(); night.Begin();
            double daySpent = Rested(day, route.Hours + 1e-6, night: false);
            Check.That(day.State == TravelState.Arrived && Math.Abs(daySpent - day.Route!.Hours) < 1e-6, "daylight travel arrives after the route's hours");
            Rested(night, route.Hours, night: true);
            Check.That(night.State == TravelState.Travelling && night.RemainingHours > route.Hours * 0.6, "night travel covers about a third of the ground");
            Rested(night, route.Hours * 3, night: true);
            Check.That(night.State == TravelState.Arrived && night.Position == route.Points[^1], "night travel still arrives, three times slower");

            PartyTravel paused = new(route.Points[0]);
            paused.Plan(cost, route.Points[^1], to.Name);
            paused.Begin();
            paused.Advance(route.Hours / 2, _ => false);
            Vector2 midway = paused.Position;
            paused.Pause();
            Check.That(paused.Advance(1, _ => false) == 0 && paused.Position == midway, "a paused party does not move");
            paused.Stop();
            Check.That(paused.State == TravelState.Idle && paused.Route is null && paused.Position == midway, "halting keeps the party where it stood");

            // Fatigue (#9469): a long march exhausts the expedition and slows it; camping restores it.
            PartyTravel tired = new(route.Points[0]), fresh = new(route.Points[0]);
            TravelRoute far = Crossing(cost, grid) ?? route;
            tired.Plan(cost, far.Points[^1], "far"); fresh.Plan(cost, far.Points[^1], "far");
            tired.Begin(); fresh.Begin();
            double march = PartyTravel.ExhaustedAt / PartyTravel.FatiguePerHour;
            tired.Advance(march, _ => false);
            Check.That(tired.Exhausted && Math.Abs(tired.Fatigue - PartyTravel.ExhaustedAt) < 1e-6, "about ten hours of marching exhausts the expedition");
            Check.That(tired.EventRisk > new PartyTravel(route.Points[0]).EventRisk, "an exhausted expedition meets more trouble than a fresh one");
            double before = tired.RemainingHours;
            tired.Advance(1, _ => false);
            double exhaustedProgress = before - tired.RemainingHours;
            Check.That(Math.Abs(exhaustedProgress - 1 / PartyTravel.ExhaustedMultiplier) < 1e-3, "an exhausted expedition moves slower");
            tired.Rest(4);
            Check.That(tired.State == TravelState.Paused && tired.Fatigue < PartyTravel.ExhaustedAt, "a daytime camp halts and recovers part of the fatigue");
            tired.Rest(0.5);
            Check.That(tired.Fatigue > 0, "half an hour before dawn is not a night's sleep");
            tired.Rest(PartyTravel.FullRestHours);
            Check.That(tired.Fatigue == 0, "a night's camp restores the expedition fully");
            fresh.Advance(PartyTravel.MaximumFatigue / PartyTravel.FatiguePerHour * 4, _ => false);
            Check.That(fresh.Fatigue <= PartyTravel.MaximumFatigue, "fatigue is bounded");
        }

        // A full-world crossing lands near the owner's two-day starting point (Den design/overland-travel-mode).
        TravelRoute? crossing = Crossing(cost, grid);
        if (crossing is not null)
        {
            Console.WriteLine($"crossing: {crossing.Metres / 1000:F1} km in {crossing.Hours:F1} h of daylight travel");
            Check.That(crossing.Hours is > 12 and < 72, "crossing a 10 km world takes roughly one to three days of travel");
        }
        int west = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + x).First(cost.Passable);
        // Travel events (#9470): deterministic per seed, with a cooldown, likelier for a tired party
        // and far from home, encounters likelier at night.
        TravelEventFacts open = new(MapBiome.Grassland, Night: false, River: RiverState.None, HomeKilometres: 0, Risk: 1);
        (int Count, double MinimumGap, int Encounters) Simulate(TravelEventFacts facts, ulong seed)
        {
            TravelEventDirector director = new(seed);
            int count = 0, encounters = 0;
            double hour = 0, last = double.NegativeInfinity, minimumGap = double.PositiveInfinity;
            const double Step = 0.25;
            for (int i = 0; i < 40_000; i++)
            {
                hour += Step;
                if (director.Roll(Step, facts) is not TravelEventKind kind) continue;
                count++;
                if (kind == TravelEventKind.Encounter) encounters++;
                minimumGap = Math.Min(minimumGap, hour - last);
                last = hour;
            }
            return (count, minimumGap, encounters);
        }
        var calm = Simulate(open, 7);
        Check.That(calm == Simulate(open, 7), "travel events roll the same for the same seed");
        Check.That(calm.MinimumGap >= TravelEventDirector.CooldownHours, $"no event follows another within the cooldown (closest {calm.MinimumGap:F2} h)");
        double perHundred = calm.Count * 100d / 10_000;
        Check.That(perHundred is > 3 and < 12, $"a fresh party near home meets a handful of events per hundred travel hours ({perHundred:F1})");
        var tiredParty = Simulate(open with { Risk = PartyTravel.ExhaustedEventRisk }, 7);
        Check.That(tiredParty.Count > calm.Count, $"exhaustion raises event risk ({tiredParty.Count} vs {calm.Count} events)");
        Check.That(Simulate(open with { HomeKilometres = 8 }, 7).Count > calm.Count, "the country far from home is more dangerous");
        var dark = Simulate(open with { Night = true }, 7);
        Check.That(dark.Encounters * calm.Count > calm.Encounters * dark.Count, "encounters make up more of the night's events");
        double[] alpine = TravelEventDirector.Weights(open with { Biome = MapBiome.Alpine, River = RiverState.High });
        double[] meadow = TravelEventDirector.Weights(open);
        Check.That(alpine[(int)TravelEventKind.Hazard] > meadow[(int)TravelEventKind.Hazard], "mountains and fords bring more hazards");
        // A ford follows its river's stage (#9878): likelier trouble as it rises and floods, none over ice or a dry bed.
        double Hazard(RiverState river) => TravelEventDirector.Weights(open with { River = river })[(int)TravelEventKind.Hazard];
        Check.That(Hazard(RiverState.None) < Hazard(RiverState.Running) && Hazard(RiverState.Running) < Hazard(RiverState.High) && Hazard(RiverState.High) < Hazard(RiverState.Flood),
            "a ford's hazard rises with its river: running, high, in flood");
        Check.That(Hazard(RiverState.Frozen) == Hazard(RiverState.None) && Hazard(RiverState.Dry) == Hazard(RiverState.None),
            "a frozen or dry river is no ford: crossing it is as the open ground");
        TravelEvent swollen = TravelEvents.Hazard(MapBiome.Grassland, RiverState.High), flooded = TravelEvents.Hazard(MapBiome.Grassland, RiverState.Flood);
        Check.That(swollen.Title == "Swollen ford" && flooded.Title == "River in flood" && TravelEvents.Hazard(MapBiome.Grassland, RiverState.Frozen).Title == "Broken ground"
            && TravelEvents.Hazard(MapBiome.Grassland, RiverState.Running).Title == "Cold ford",
            "a hazard at a river names its ford as the stage has it, and ice or a dry bed is no ford");
        Check.That(TravelEvents.DetourFor(RiverState.Flood) > TravelEvents.DetourFor(RiverState.High) && flooded.Choices[0].Label.Contains($"{TravelEvents.FloodDetourHours:F0} h", StringComparison.Ordinal),
            "a river in flood has no ford: going round it is a long way");
        Check.That(RiverStates.Of(null) == RiverState.Running && RiverStates.Of(new(1, 1, 0.3, 0, false, false)) == RiverState.Running
            && RiverStates.Of(new(9, 2, RiverStates.HighFullness, 0.4, false, false)) == RiverState.High
            && RiverStates.Of(new(90, 4, 1.2, 2, false, false)) == RiverState.Flood
            && RiverStates.Of(new(9, 4, 1.2, 1, true, false)) == RiverState.Frozen
            && RiverStates.Of(new(0.01, 0.01, 0.002, -0.3, false, true)) == RiverState.Dry,
            "a reach's stage reads as a crossing: dry before frozen before flood before high");
        Check.That(alpine[(int)TravelEventKind.Weather] == 0 && meadow[(int)TravelEventKind.Weather] == 0, "weather is never rolled: it comes with the fronts (#9739)");
        TravelEvent glass = TravelEvents.Weather("Glass storm", "The air rings.", 100, wounds: true);
        Check.That(glass.Choices[0].Label.Contains($"{TravelEvents.LongestShelterHours:F0} h", StringComparison.Ordinal) && glass.Choices[1].Label.Contains("wounds", StringComparison.Ordinal),
            "a front's event offers to camp until it passes, at most two days at a time, and says when pressing on wounds");
        foreach (TravelEvent shown in new[] { TravelEvents.Encounter(MapBiome.Grassland), TravelEvents.Discovery("Ruin", 1.2), TravelEvents.Weather("Rain front", "Rain sweeps in.", 6, wounds: false), TravelEvents.Hazard(MapBiome.Alpine, RiverState.High) })
            Check.That(shown.Choices.Count == 2 && shown.Choices.All(c => !c.Label.Contains(',') && !c.Label.Contains('|') && !c.Id.Contains(':'))
                && !shown.Title.Contains('|') && !shown.Text.Contains('|'), $"{shown.Kind} offers two answers that publish cleanly");

        Check.That(TravelEventDirector.DangerName(TravelEventDirector.Danger(0)) == "low"
            && TravelEventDirector.DangerName(TravelEventDirector.Danger(5)) == "moderate"
            && TravelEventDirector.DangerName(TravelEventDirector.Danger(20)) == "high"
            && TravelEventDirector.Danger(100) == TravelEventDirector.MaximumDanger, "the route preview names danger by distance from home, up to a ceiling");

        // River travel (#9879): a route says how each leg is made; the water carries the load and rests the party.
        TravelRoute mixed = new("the port", [Vector2.Zero, new(100, 0), new(1100, 0), new(1200, 0)], [1, 4, 1], 0, [LegMode.Walk, LegMode.Drift, LegMode.Walk]);
        Check.That(mixed.ByBoat && !mixed.Afloat(0) && mixed.Afloat(1) && mixed.Mode(2) == LegMode.Walk && mixed.Mode(7) == LegMode.Walk
            && !new TravelRoute("x", [Vector2.Zero, Vector2.One], [1]).ByBoat, "a route says how each leg is made, and a route without modes is walked");
        TravelRoute rest = mixed.From(1, new(50, 0), 0.5);
        Check.That(rest.Modes!.SequenceEqual([LegMode.Drift, LegMode.Walk]) && rest.LegHours[0] == 2 && rest.Points[0] == new Vector2(50, 0),
            "the rest of a route from where the party stands keeps how its legs are made");
        PartyTravel loaded = PartyTravel.Rehearsing(mixed, 0, loadMultiplier: 2);
        double first = loaded.Advance(1.5, _ => false);
        Check.That(Math.Abs(first - 1.5) < 1e-9 && loaded.Leg == 0, "a load slows the walked legs");
        PartyTravel boating = PartyTravel.Rehearsing(mixed, 0, loadMultiplier: 2);
        boating.Advance(2, _ => false);
        Check.That(boating.Leg == 1 && boating.Afloat, "the party is afloat on a leg made by boat");
        double fatigueAboard = boating.Fatigue;
        double afloatHours = boating.Advance(2, _ => false);
        Check.That(boating.Leg == 1 && Math.Abs(boating.Fatigue - fatigueAboard - (afloatHours * PartyTravel.FatiguePerHour * PartyTravel.Exertion(LegMode.Drift))) < 1e-9
            && PartyTravel.Exertion(LegMode.Drift) < PartyTravel.Exertion(LegMode.Walk) && PartyTravel.Exertion(LegMode.Portage) > PartyTravel.Exertion(LegMode.Walk),
            "on the water the load rides free and drifting rests the party; portaging tires it more than walking");

        // The sled (#9473): finite cargo, glides on snow and drags on rock, slower when loaded.
        Sled sled = new(Vector2.Zero);
        Check.That(sled.Stow(CraftSurvive.Game.Modules.Inventory.ItemCatalog.Ration, Sled.Capacity + 50) == Sled.Capacity && sled.Free == 0
            && sled.Stow(CraftSurvive.Game.Modules.Inventory.ItemCatalog.Meat, 1) == 0, "the sled holds only its capacity");
        Check.That(sled.Take(CraftSurvive.Game.Modules.Inventory.ItemCatalog.Ration, 10) == 10 && sled.Load == Sled.Capacity - 10
            && sled.Take(CraftSurvive.Game.Modules.Inventory.ItemCatalog.Meat, 1) == 0, "taking from the sled gives back only what it holds");
        Check.That(sled.Within(new(Sled.ReachMetres - 1, 0)) && !sled.Within(new(Sled.ReachMetres + 1, 0)), "the sled is reached only from beside it");
        Check.That(SledTravel.Terrain(MapBiome.IceField) < 1 && SledTravel.Terrain(MapBiome.Alpine) > 1
            && SledTravel.LoadMultiplier(1) > SledTravel.LoadMultiplier(0) && SledTravel.LoadMultiplier(0) == 1, "runners glide on ice, drag on rock, and a load slows the march");
        TravelCostModel hauling = new(map, SledTravel.Terrain);
        int iceNode = Enumerable.Range(0, grid.Count).FirstOrDefault(i => cost.Passable(i) && WorldMap.Biome(map.Node(i)) == MapBiome.IceField, -1);
        if (iceNode >= 0) Check.That(hauling.Multiplier(iceNode) < cost.Multiplier(iceNode), "a sled crosses ice field faster than a party on foot");

        // Weather where the party walks slows each leg (#9739); time lost still tires.
        PartyTravel slowed = new(Vector2.Zero), steady = new(Vector2.Zero);
        TravelRoute? across = Crossing(cost, grid);
        if (across is not null)
        {
            foreach (PartyTravel p in new[] { slowed, steady }) { p.Plan(cost, across.Points[^1], "far"); p.Relocate(across.Points[0]); p.Plan(cost, across.Points[^1], "far"); p.Begin(); }
            const double hours = 4, storm = 2;
            slowed.Advance(hours, _ => false, _ => storm);
            steady.Advance(hours, _ => false);
            Check.That(slowed.RemainingHours > steady.RemainingHours, "pressing on through weather covers less ground");
            double before = steady.Fatigue;
            steady.Tire(2);
            Check.That(steady.Fatigue > before, "hours lost to a detour still tire the party");
        }

        TravelRoute? shore = TravelRouter.Plan(cost, new((float)grid.X(west), (float)grid.Z(west)), new((float)grid.X(sea), (float)grid.Z(sea)), "sea");
        Check.That(shore is not null && map.Sample(shore.Points[^1].X, shore.Points[^1].Y).Elevation >= GenerationConstants.WaterLevel - 0.5,
            "a destination at sea routes to the nearest shore and ends on land");
    }

    /// <summary>West to east across the middle of the world.</summary>
    private static TravelRoute? Crossing(TravelCostModel cost, MapGrid grid)
    {
        int west = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + x).First(cost.Passable);
        int east = Enumerable.Range(0, grid.Side).Select(x => (grid.Side / 2) * grid.Side + grid.Segments - x).First(cost.Passable);
        return TravelRouter.Plan(cost, new((float)grid.X(west), (float)grid.Z(west)), new((float)grid.X(east), (float)grid.Z(east)), "far side");
    }

    /// <summary>Travel in marches short enough never to tire, camping overnight between them, so pace is the terrain's and the night's alone.</summary>
    private static double Rested(PartyTravel party, double hours, bool night)
    {
        const double March = 5;
        double spent = 0;
        while (spent < hours && party.State == TravelState.Travelling)
        {
            spent += party.Advance(Math.Min(March, hours - spent), _ => night);
            if (party.State != TravelState.Travelling) break;
            party.Rest(PartyTravel.FullRestHours);
            party.Begin();
        }
        return spent;
    }
}
