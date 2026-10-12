using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.Weather;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The river stage (#9878), on the default continent: deterministic for a time, a year about each reach's
/// mean, frozen in winter more than in summer, dry only where a small stream fails, floods rare; and the
/// water owner standing a river at its reach's stage, bare when dry, frozen when iced.
/// </summary>
internal static class StageChecks
{
    /// <summary>The year is walked in steps this long (hours).</summary>
    private const double YearStepHours = 6;

    /// <summary>Where the checks start: a year in, so the calibration year lies wholly after the clock's start.</summary>
    private const double StartHours = Seasons.YearDays * 24;

    internal static void TheYearTurns()
    {
        double midsummer = 0.3 * Seasons.YearDays * 24, midwinter = 0.8 * Seasons.YearDays * 24;
        Check.That(Math.Abs(Seasons.Offset(midsummer) - Seasons.Swing) < 1e-9 && Math.Abs(Seasons.Offset(midwinter) + Seasons.Swing) < 1e-9,
            "the air is warmest at midsummer and coldest half a year on");
        Check.That(Seasons.At(midsummer) == Season.Summer && Seasons.At(midwinter) == Season.Winter
            && Seasons.At(0.55 * Seasons.YearDays * 24) == Season.Autumn && Seasons.At(0.05 * Seasons.YearDays * 24) == Season.Spring,
            "the seasons follow the year: spring, summer, autumn, winter");
        Check.That(Seasons.Offset(midsummer + (Seasons.YearDays * 24)) == Seasons.Offset(midsummer), "each year turns the same");
    }

    internal static void Run(WorldMap continent, TerrainRecipe recipe)
    {
        WaterNetwork network = MapRegions.For(continent).Drainage.Network;
        RiverStage stage = new(network, continent, WeatherField.For(continent));
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        stage.Start(StartHours);
        double startMs = clock.Elapsed.TotalMilliseconds;
        Check.That(stage.Hours == StartHours, "the stage stands at the time it was started for");

        // A second stage started for the same time agrees with the first exactly: nothing but the seed, the map and the clock.
        RiverStage again = new(network, continent, WeatherField.For(continent));
        again.Start(StartHours);
        Check.That(Enumerable.Range(0, network.Reaches.Count).All(r => stage.At(r) == again.At(r)), "the stage is the same for the same world and time");

        Reach great = network.Reaches.MaxBy(r => r.Flow)!;
        int count = network.Reaches.Count, steps = (int)(Seasons.YearDays * 24 / YearStepHours);
        double greatMean = 0, mostFlood = 0;
        double winterIce = 0, summerIce = 0;
        int winterDays = 0, summerDays = 0;
        bool anyDry = false, anyFlood = false, smallOnlyDry = true, stageFollowsFlow = true;
        clock.Restart();
        for (int step = 1; step <= steps; step++)
        {
            stage.AdvanceTo(StartHours + (step * YearStepHours));
            greatMean += stage.At(great.Id).Ratio / steps;
            int iced = 0, flooded = 0;
            for (int r = 0; r < count; r++)
            {
                ReachStage s = stage.At(r);
                if (s.Ice) iced++;
                if (s.Overbank) { flooded++; anyFlood = true; }
                if (s.Dry) { anyDry = true; smallOnlyDry &= network.Reaches[r].Flow < RiverStage.DryStreamFlow; }
                if (Math.Abs(s.Ratio - 1) > 0.05) stageFollowsFlow &= Math.Sign(s.Stage) == Math.Sign(s.Ratio - 1);
            }

            mostFlood = Math.Max(mostFlood, flooded / (double)count);
            switch (Seasons.At(stage.Hours))
            {
                case Season.Winter: winterIce += iced / (double)count; winterDays++; break;
                case Season.Summer: summerIce += iced / (double)count; summerDays++; break;
            }
        }

        double stepMs = clock.Elapsed.TotalMilliseconds / (steps * YearStepHours);
        winterIce /= Math.Max(1, winterDays);
        summerIce /= Math.Max(1, summerDays);
        Console.WriteLine($"river stage: {count} reaches, {stage.CellCount} cells; start {startMs:F0} ms, {stepMs:F2} ms a game hour; great river {greatMean:F2}x its mean over the year; ice winter {winterIce:P0} summer {summerIce:P0}; most in flood {mostFlood:P1}");
        Check.That(greatMean is > 0.7 and < 1.4, $"over a year the great river runs about its mean ({greatMean:F2}x)");
        Check.That(winterIce > summerIce + 0.05, $"more of the network is frozen in winter than in summer ({winterIce:P0} vs {summerIce:P0})");
        Check.That(anyDry && smallOnlyDry, "some streams run dry, and only small ones");
        Check.That(anyFlood && mostFlood < 0.2, $"floods come, but never to most of the network at once (at most {mostFlood:P1})");
        Check.That(stageFollowsFlow, "a reach stands above its usual surface when it runs above its mean, and below when below");

        WaterOwnerFollowsTheStage(network, recipe);
    }

    /// <summary>The water owner puts a river at its reach's stage, bares it when dry and freezes it when iced.</summary>
    private static void WaterOwnerFollowsTheStage(WaterNetwork network, TerrainRecipe recipe)
    {
        WaterModel water = new(recipe);
        (long X, long Z)? column = null;
        int reach = -1;
        foreach (Reach candidate in network.Reaches.Where(r => r.Flow > WaterNetwork.CanoeFlow).OrderByDescending(r => r.Flow).Take(40))
        {
            int node = candidate.Nodes[candidate.Nodes.Length / 2];
            long cx = (long)network.Grid.X(node), cz = (long)network.Grid.Z(node);
            for (int d = 0; d < 400 && column is null; d++)
            {
                long x = cx + (d % 20) - 10, z = cz + (d / 20) - 10;
                (double ground, MapSample geography) = recipe.GroundAt(x, z);
                if (geography.RiverReach >= 0 && WaterRules.Body(geography, ground) == WaterBody.River) { column = (x, z); reach = geography.RiverReach; }
            }

            if (column is not null) break;
        }

        Check.That(column is not null, "a river column is found to stage");
        if (column is not (long X, long Z) at) return;
        WaterSample still = water.Sample(at.X, at.Z);
        Check.That(still.Stage == 0 && !still.Ice && still.Wet, "with no stage a river stands at its usual surface");
        StubStage stub = new(reach);
        water.Stages = stub;
        stub.Now = new(10, 2, 0.7, 0.6, false, false);
        WaterSample high = water.Sample(at.X, at.Z);
        Check.That(Math.Abs(high.Top - (still.Surface + 0.6)) < 1e-9 && high.Depth > still.Depth, "a river stands at its reach's stage over its usual surface");
        stub.Now = new(0.01, 0.01, 0.002, -0.2, false, true);
        WaterSample dry = water.Sample(at.X, at.Z);
        Check.That(!dry.Wet && dry.Depth == 0 && dry.Body == WaterBody.River, "a dry reach leaves its bed bare");
        stub.Now = new(2, 0.4, 0.15, -0.1, true, false);
        Check.That(water.Sample(at.X, at.Z).Ice, "a frozen reach's water is ice");
        stub.Now = null;
        Check.That(water.Sample(at.X, at.Z) == still, "a reach with no stage yet stands as usual");
    }

    private sealed class StubStage(int reach) : IWaterStage
    {
        internal ReachStage? Now { get; set; }

        public ReachStage? At(int asked) => asked == reach ? Now : null;

        public bool LakeIce(Lake lake) => false;
    }
}
