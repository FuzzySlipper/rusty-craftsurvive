using System.Collections;

namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Which craft a reach carries: a canoe, a laden river boat, or a great river's traffic.</summary>
internal enum Navigability { None, Canoe, Boat, Great }

/// <summary>A reach's channel form, from its gradient and flow (#9875), for drawing and siting.</summary>
internal enum ChannelType { Incised, Braided, Meandering, Estuary }

/// <summary>
/// One reach of the network, between confluences or from a source, upstream node first. Flows are
/// mean (m³/s) at its outlet. The routing figures are what a reservoir routing of the stage needs
/// (#9878): the bankfull flow, the bank it implies above the mean water, and the storage time (length
/// over mean velocity).
/// </summary>
internal sealed record Reach(
    int Id,
    int[] Nodes,
    int Downstream,
    bool Mouth,
    double LengthMetres,
    double CatchmentSquareKilometres,
    double Flow,
    double Gradient,
    double Width,
    double Depth,
    Navigability Class,
    double BankfullFlow,
    double BankHeight,
    double Velocity,
    double StorageHours,
    ChannelType Channel)
{
    internal int Source => Nodes[0];
    internal int Outlet => Nodes[^1];
}

/// <summary>Where reaches join: the node, the reaches flowing in and the one flowing on.</summary>
internal sealed record Confluence(int Node, int[] Inflows, int Outflow);

/// <summary>
/// The water network as data (#9875): the drainage a walker meets (<see cref="MapDrainage"/>) kept as
/// reaches with their flow, size, navigability, bankfull stage and storage, plus its mouths, confluences,
/// the heads of navigation and the floodplain. Built once with the drainage, deterministically, and
/// never saved. Everything that asks where water runs and how much - siting, travel, the stage, the
/// water surfaces - reads it rather than re-deriving it.
/// </summary>
internal sealed class WaterNetwork
{
    /// <summary>Least mean flow (m³/s) and steepest gradient each craft uses; steeper is rapids.</summary>
    internal const double CanoeFlow = 5, BoatFlow = 40, GreatFlow = 150;
    private const double CanoeGradient = 0.01, BoatGradient = 0.002, GreatGradient = 0.001;

    /// <summary>
    /// Bankfull flow (about the one-and-a-half-year flood) over the mean: a wet climate's rivers swell
    /// less against their mean than a dry one's. Moisture 1 gives the first, 0 the first plus the second.
    /// </summary>
    private const double WetBankfullRatio = 3, DryBankfullExtra = 7;

    /// <summary>A reach steeper than this cuts down into rock; between it and the braiding line, flow and gradient decide.</summary>
    private const double IncisedGradient = 0.01;

    /// <summary>Leopold and Wolman's meander-braid threshold: braided above 0.012·Q^-0.44.</summary>
    private const double BraidCoefficient = 0.012, BraidExponent = -0.44;

    /// <summary>
    /// The floodplain: ground no higher than the bankfull water plus this storm margin (m), within this
    /// many channel widths of a canoe river (at least the lattice spacing).
    /// </summary>
    private const double StormMargin = 1, FloodplainWidths = 20;

    private const double SecondsPerHour = 3600;

    private WaterNetwork(MapGrid grid, IReadOnlyList<Reach> reaches, IReadOnlyList<Confluence> confluences, BitArray floodplain,
        IReadOnlyList<int> boatHeads, IReadOnlyList<int> canoeHeads, bool[] boatToSea, bool[] canoeToSea)
    {
        Grid = grid;
        Reaches = reaches;
        Confluences = confluences;
        this.floodplain = floodplain;
        BoatHeads = boatHeads;
        CanoeHeads = canoeHeads;
        this.boatToSea = boatToSea;
        this.canoeToSea = canoeToSea;
    }

    private readonly BitArray floodplain;
    private readonly bool[] boatToSea, canoeToSea;

    internal MapGrid Grid { get; }
    internal IReadOnlyList<Reach> Reaches { get; }
    internal IReadOnlyList<Confluence> Confluences { get; }

    /// <summary>Reaches that reach the sea.</summary>
    internal IEnumerable<Reach> Mouths => Reaches.Where(reach => reach.Mouth);

    /// <summary>Reach ids where a boat (or a canoe) coming up from the sea can go no farther: the heads of navigation.</summary>
    internal IReadOnlyList<int> BoatHeads { get; }
    internal IReadOnlyList<int> CanoeHeads { get; }

    /// <summary>Whether a boat (or a canoe) can come up from the sea all the way to this reach.</summary>
    internal bool BoatFromSea(int reach) => boatToSea[reach];
    internal bool CanoeFromSea(int reach) => canoeToSea[reach];

    /// <summary>Whether a lattice node lies on the floodplain: ground the bankfull water and a storm would cover.</summary>
    internal bool OnFloodplain(int node) => floodplain[node];

    /// <summary>The reach a reach finally drains to the sea by (itself if it is a mouth), or -1 if it leaves by the map's edge.</summary>
    internal int MouthOf(int reach)
    {
        int r = reach;
        for (int guard = 0; guard < Reaches.Count && r >= 0; guard++)
        {
            if (Reaches[r].Mouth) return r;
            r = Reaches[r].Downstream;
        }

        return -1;
    }

    /// <param name="paths">The drainage's reaches as node paths (<see cref="MapRivers.Trace"/>).</param>
    /// <param name="moistureAt">The climate's moisture at a world point, for how much a river swells.</param>
    internal static WaterNetwork Build(MapGrid grid, MapFlow flow, float[] elevation, float[] discharge, IReadOnlyList<int[]> paths,
        Func<double, double, double> moistureAt)
    {
        Dictionary<int, int> startingAt = [];
        for (int r = 0; r < paths.Count; r++) startingAt[paths[r][0]] = r;

        Reach[] reaches = new Reach[paths.Count];
        for (int r = 0; r < paths.Count; r++)
        {
            int[] nodes = paths[r];
            int outlet = nodes[^1];
            bool mouth = elevation[outlet] < GenerationConstants.WaterLevel;
            int downstream = !mouth && startingAt.TryGetValue(outlet, out int next) && next != r ? next : -1;
            double length = 0, catchment = 0;
            for (int k = 0; k < nodes.Length - 1; k++)
            {
                length += flow.Distance[nodes[k]] * grid.Spacing;
                catchment = Math.Max(catchment, MapRivers.CatchmentSquareKilometres(grid, discharge[nodes[k]]));
            }

            double q = WaterBudget.Flow(catchment);
            double drop = Math.Max(elevation[nodes[0]], GenerationConstants.WaterLevel) - Math.Max(elevation[outlet], GenerationConstants.WaterLevel);
            double gradient = length > 0 ? Math.Max(0, drop) / length : 0;
            double depth = RiverGeometry.Depth(q);
            int middle = nodes[nodes.Length / 2];
            double moisture = Math.Clamp(moistureAt(grid.X(middle), grid.Z(middle)), 0, 1);
            double bankfull = q * (WetBankfullRatio + (DryBankfullExtra * (1 - moisture)));
            double velocity = RiverGeometry.Velocity(depth, gradient);
            Navigability navigability = q >= GreatFlow && gradient <= GreatGradient ? Navigability.Great
                : q >= BoatFlow && gradient <= BoatGradient ? Navigability.Boat
                : q >= CanoeFlow && gradient <= CanoeGradient ? Navigability.Canoe
                : Navigability.None;
            ChannelType channel = mouth && navigability >= Navigability.Boat ? ChannelType.Estuary
                : gradient > IncisedGradient ? ChannelType.Incised
                : q > 0 && gradient > BraidCoefficient * Math.Pow(q, BraidExponent) ? ChannelType.Braided
                : ChannelType.Meandering;
            reaches[r] = new(r, nodes, downstream, mouth, length, catchment, q, gradient, 2 * RiverGeometry.HalfWidth(q), depth, navigability,
                bankfull, Math.Max(0, RiverGeometry.Depth(bankfull) - depth), velocity, length / velocity / SecondsPerHour, channel);
        }

        List<int>[] inflows = new List<int>[grid.Count];
        foreach (Reach reach in reaches)
            if (reach.Downstream >= 0) (inflows[reach.Outlet] ??= []).Add(reach.Id);
        List<Confluence> confluences = [];
        for (int node = 0; node < grid.Count; node++)
            if (inflows[node] is { Count: >= 2 } joined && startingAt.TryGetValue(node, out int outflow))
                confluences.Add(new(node, [.. joined], outflow));

        bool[] boatToSea = FromSea(reaches, Navigability.Boat), canoeToSea = FromSea(reaches, Navigability.Canoe);
        return new(grid, reaches, confluences, Floodplain(grid, elevation, reaches),
            Heads(reaches, boatToSea), Heads(reaches, canoeToSea), boatToSea, canoeToSea);
    }

    /// <summary>Which reaches a craft can reach from the sea: navigable all the way down to a mouth.</summary>
    private static bool[] FromSea(Reach[] reaches, Navigability least)
    {
        int[] state = new int[reaches.Length]; // 0 unknown, 1 yes, 2 no
        bool Resolve(int start)
        {
            List<int> chain = [];
            int r = start;
            bool result = false;
            while (true)
            {
                if (state[r] != 0) { result = state[r] == 1; break; }
                chain.Add(r);
                if (reaches[r].Class < least) { result = false; break; }
                if (reaches[r].Mouth) { result = true; break; }
                if (reaches[r].Downstream < 0) { result = false; break; }
                r = reaches[r].Downstream;
            }

            // Every reach on the chain shares the answer, except where the chain broke on a reach a craft cannot use.
            foreach (int c in chain) state[c] = result && reaches[c].Class >= least ? 1 : 2;
            return state[start] == 1;
        }

        bool[] reachable = new bool[reaches.Length];
        for (int r = 0; r < reaches.Length; r++) reachable[r] = Resolve(r);
        return reachable;
    }

    /// <summary>Reachable reaches with no reachable reach above them: where navigation from the sea ends.</summary>
    private static int[] Heads(Reach[] reaches, bool[] reachable)
    {
        bool[] fed = new bool[reaches.Length];
        foreach (Reach reach in reaches)
            if (reachable[reach.Id] && reach.Downstream >= 0) fed[reach.Downstream] = true;
        return [.. reaches.Where(reach => reachable[reach.Id] && !fed[reach.Id]).Select(reach => reach.Id)];
    }

    /// <summary>
    /// Ground the bankfull water plus a storm margin would cover, within some channel widths of a canoe
    /// river: towns stand off it; marshes and oxbows belong in it.
    /// </summary>
    private static BitArray Floodplain(MapGrid grid, float[] elevation, Reach[] reaches)
    {
        BitArray plain = new(grid.Count);
        foreach (Reach reach in reaches)
        {
            if (reach.Flow < CanoeFlow) continue;
            int cells = Math.Max(1, (int)Math.Ceiling(Math.Max(grid.Spacing, FloodplainWidths * reach.Width) / grid.Spacing));
            foreach (int node in reach.Nodes)
            {
                if (elevation[node] < GenerationConstants.WaterLevel) continue;
                double level = elevation[node] + reach.BankHeight + StormMargin;
                int cx = node % grid.Side, cz = node / grid.Side;
                for (int dz = -cells; dz <= cells; dz++)
                for (int dx = -cells; dx <= cells; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x > grid.Segments || z > grid.Segments || (dx * dx) + (dz * dz) > cells * cells) continue;
                    int i = (z * grid.Side) + x;
                    if (elevation[i] >= GenerationConstants.WaterLevel && elevation[i] <= level) plain[i] = true;
                }
            }
        }

        return plain;
    }
}
