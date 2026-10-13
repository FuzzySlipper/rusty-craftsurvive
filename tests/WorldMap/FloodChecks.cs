using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// Floods (#9880): a reach over its banks puts water over its own floodplain - the floodplain #9875 maps,
/// node for node - at a level that rises with its overflow and stands only over ground below it; nothing
/// floods while the reach keeps to its banks or is frozen, and the water owner shows the flood where it
/// stands over a column.
/// </summary>
internal static class FloodChecks
{
    internal static void Run(WorldMap continent, TerrainRecipe recipe)
    {
        MapDrainage drainage = MapRegions.For(continent).Drainage;
        WaterNetwork network = drainage.Network;
        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        FloodPlains plains = new(network, drainage.ElevationAt);
        double buildMs = clock.Elapsed.TotalMilliseconds;
        int mapped = Enumerable.Range(0, network.Grid.Count).Count(network.OnFloodplain);
        Check.That(plains.Nodes.Length == mapped && plains.Nodes.All(network.OnFloodplain),
            $"the floodplains are the network's own, node for node ({plains.Nodes.Length} of {mapped})");
        Check.That(plains.Nodes.All(n => network.Reaches[plains.ReachOf(n)].Flow >= WaterNetwork.CanoeFlow), "every floodplain node floods from a canoe river");

        Reach reach = plains.Nodes.GroupBy(plains.ReachOf).MaxBy(g => g.Count())!.Select(n => network.Reaches[plains.ReachOf(n)]).First();
        Check.That(FloodPlains.Rise(reach, reach.BankfullFlow) == 0 && FloodPlains.Rise(reach, 2 * reach.BankfullFlow) > 0
            && FloodPlains.Rise(reach, 4 * reach.BankfullFlow) > FloodPlains.Rise(reach, 2 * reach.BankfullFlow),
            "the flood rises with the flow over bankfull, from nothing at the bank");

        ReachStage Stage(double flow, bool ice = false) => RiverStage.Read(reach, flow, ice ? double.MaxValue : 0);
        ReachStage Usual(int r) => RiverStage.Read(network.Reaches[r], network.Reaches[r].Flow, 0);
        FloodLevels banked = plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 0.9) : Usual(r));
        FloodLevels flood = plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 3) : Usual(r));
        FloodLevels frozen = plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 3, ice: true) : Usual(r));
        int[] own = [.. plains.Nodes.Where(n => plains.ReachOf(n) == reach.Id)];
        int[] wet = [.. own.Where(n => !double.IsNaN(flood.LevelAt(n)))];
        Console.WriteLine($"floods: floodplains {plains.Nodes.Length} nodes built in {buildMs:F0} ms; reach {reach.Id} ({reach.Flow:F0} m3/s) at three times bankfull floods {wet.Length} of its {own.Length} nodes");
        Check.That(banked.Count == 0 && frozen.Count == 0, "nothing floods while every river keeps to its banks, nor from a frozen one");
        Check.That(wet.Length > 0 && flood.Count == wet.Length, "a reach over its banks floods its own floodplain and no other");
        Check.That(wet.All(n => flood.LevelAt(n) > plains.Ground(n)), "a flood stands only over ground below it");

        // Once the river falls back within its banks the plain drains, no faster than its recession.
        FloodLevels draining = plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 0.9) : Usual(r), flood, 1);
        FloodLevels drained = plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 0.9) : Usual(r), flood, 1000);
        Check.That(draining.Count > 0 && wet.All(n => double.IsNaN(draining.LevelAt(n))
                ? flood.LevelAt(n) - FloodPlains.RecessionMetresPerHour <= plains.Ground(n)
                : Math.Abs(draining.LevelAt(n) - (flood.LevelAt(n) - FloodPlains.RecessionMetresPerHour)) < 1e-3)
            && drained.Count == 0 && plains.Levels(r => r == reach.Id ? Stage(reach.BankfullFlow * 3, ice: true) : Usual(r), flood, 1).Count == 0,
            "after the river falls back the plain drains a little an hour until the ground shows; a freeze ends the flood");

        int node = wet.MaxBy(n => flood.LevelAt(n) - plains.Ground(n));
        double x = network.Grid.X(node), z = network.Grid.Z(node);
        Check.That(flood.At(x, z) is (double level, int from) && Math.Abs(level - flood.LevelAt(node)) < 1e-3 && from == reach.Id
            && flood.Flooded(x, z) && flood.Floodable(x, z), "at a flooded node the flood reads its level and reach, and its ground is flooded");
        Check.That(flood.At(x + (10 * network.Grid.Spacing * network.Grid.Side), z) is null, "far from any flood there is none");

        // The water owner: where the flood stands over a column's ground, the column is under it.
        WaterModel water = new(recipe);
        StubFloods stub = new(flood);
        water.Stages = stub;
        (long X, long Z)? column = null;
        for (int dz = -60; dz <= 60 && column is null; dz += 6)
        for (int dx = -60; dx <= 60 && column is null; dx += 6)
        {
            long cx = (long)Math.Floor(x) + dx, cz = (long)Math.Floor(z) + dz;
            water.Stages = null;
            WaterSample dry = water.Sample(cx, cz);
            water.Stages = stub;
            if (!dry.Wet && flood.At(cx + 0.5, cz + 0.5) is (double over, _) && over > dry.Bed + 0.2) column = (cx, cz);
        }

        Check.That(column is not null, "the flood stands over walking ground near its deepest node");
        if (column is (long X, long Z) c)
        {
            WaterSample flooded = water.Sample(c.X, c.Z);
            Check.That(flooded.Wet && flooded.Body == WaterBody.River && flooded.BodyId == reach.Id && Math.Abs(flooded.Top - flood.At(c.X + 0.5, c.Z + 0.5)!.Value.Level) < 1e-9,
                "the water owner shows the flood over that column, a river's water at the flood's level");
            stub.Floods = banked;
            Check.That(!water.Sample(c.X, c.Z).Wet, "and the ground again once the river is back within its banks");
        }
    }

    private sealed class StubFloods(FloodLevels floods) : IWaterStage
    {
        internal FloodLevels Floods { get; set; } = floods;

        public ReachStage? At(int reach) => null;

        public bool LakeIce(Lake lake) => false;

        public (double Level, int Reach)? FloodAt(double x, double z) => Floods.At(x, z);

        public bool Floodable(double x, double z) => Floods.Floodable(x, z);
    }
}
