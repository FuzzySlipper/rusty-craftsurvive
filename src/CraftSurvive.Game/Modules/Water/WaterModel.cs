using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The water at one place (#9877): its surface and the bed under it (metres), the horizontal current
/// (m/s; a river's reach velocity along its channel, #9879), which body it belongs to (its kind, and
/// <see cref="BodyId"/>: the walked network's reach, the lake's id, or 0 for the one sea), the stage
/// offset over its usual surface (0 until the stage is routed, #9878) and whether it is frozen. The
/// fields match what an Engine water field reports for a region (rusty-engine #9884), so a flood
/// region can be composed over the static surfaces (#9880) without changing a consumer.
/// </summary>
internal readonly record struct WaterSample(double Surface, double Bed, double CurrentX, double CurrentZ, WaterBody Body, double Stage, bool Ice,
    int BodyId = WaterModel.NoBody)
{
    /// <summary>Which body this is: its kind and its id, so two lakes or two reaches are told apart.</summary>
    internal WaterBodyId Identity => new(Body, BodyId);

    /// <summary>The water's top with its stage.</summary>
    internal double Top => Surface + Stage;

    internal double Depth => Body == WaterBody.None ? 0 : Math.Max(0, Top - Bed);

    internal bool Wet => Depth > 0;
}

/// <summary>The water's state now as the stage owner routes it (#9878): a reach's stage, and whether a lake is frozen.</summary>
internal interface IWaterStage
{
    /// <summary>A reach's water now, or null where the reach stands at its mean (no stage yet, or no such reach).</summary>
    ReachStage? At(int reach);

    /// <summary>Whether a lake is frozen now.</summary>
    bool LakeIce(Lake lake);

    /// <summary>A flood's level at a world point and the reach it comes from (#9880), or null where none is near.</summary>
    (double Level, int Reach)? FloodAt(double x, double z) => null;

    /// <summary>Whether a world point lies by a floodplain, where water may come and go with the stage (#9880).</summary>
    bool Floodable(double x, double z) => false;
}

/// <summary>
/// The one owner of where water stands in the walking world (#9877): the sea, rivers and lakes as
/// surfaces over the generated ground, from <see cref="WaterRules"/>. It answers a point or a batch;
/// the near water surfaces, the swim volume and anything else that asks read it, never voxels. Given a
/// stage (#9878), a river stands at its reach's stage over its usual surface - none at all when the
/// reach has run dry - and a river or lake is frozen as the stage says; the sea keeps its level.
/// </summary>
internal sealed class WaterModel(TerrainRecipe recipe)
{
    /// <summary>The body id of no water, and of the one sea.</summary>
    internal const int NoBody = -1, TheSea = 0;

    /// <summary>The routed stage, or null while every body stands at its usual surface.</summary>
    internal IWaterStage? Stages { get; set; }

    /// <summary>A river's speed follows its flow to this power (Manning's velocity in a wide channel).</summary>
    private const double VelocityExponent = 0.4;

    /// <summary>
    /// The water at a column (whole metres, as the ground is generated). A flood (#9880) stands over it where
    /// its level is above the ground and above any water already there, unless that water is frozen.
    /// </summary>
    internal WaterSample Sample(long x, long z)
    {
        WaterSample usual = Usual(x, z);
        if (Stages?.FloodAt(x + 0.5, z + 0.5) is not (double level, int reach) || usual.Ice || level <= usual.Bed || (usual.Wet && level <= usual.Top)) return usual;
        return new(level, usual.Bed, usual.Wet ? usual.CurrentX : 0, usual.Wet ? usual.CurrentZ : 0, WaterBody.River, 0, false, reach);
    }

    /// <summary>The flood's level over a world point (#9880), or negative infinity where there is none: for what draws water from geography alone (the far field).</summary>
    internal double FloodLevel(double x, double z) => Stages?.FloodAt(x, z)?.Level ?? double.NegativeInfinity;

    /// <summary>Whether water at a world point may come and go with a flood (#9880).</summary>
    internal bool Floodable(double x, double z) => Stages?.Floodable(x, z) ?? false;

    /// <summary>The water at a column without any flood: the sea, a river at its stage, a lake.</summary>
    private WaterSample Usual(long x, long z)
    {
        (double ground, MapSample geography) = recipe.GroundAt(x, z);
        double surface = WaterRules.Surface(geography);
        WaterBody body = WaterRules.Body(geography, ground);
        // The lake is the one whose level the surface came from (WorldMap.LakeAt follows LakeLevelAt).
        Lake? lake = body == WaterBody.Lake ? recipe.Map.LakeAt(x + 0.5, z + 0.5) : null;
        int id = body switch { WaterBody.River => geography.RiverReach, WaterBody.Lake => lake?.Id ?? NoBody, WaterBody.Sea => TheSea, _ => NoBody };
        if (body == WaterBody.River)
        {
            ReachStage? reach = Stages?.At(geography.RiverReach);
            // The current (#9879): the reach's mean velocity, faster as it carries more, along its channel.
            double speed = MeanVelocity(geography.RiverReach) * Math.Pow(reach?.Ratio ?? 1, VelocityExponent);
            if (reach is { Dry: true }) speed = 0;
            (double currentX, double currentZ) = (geography.RiverFlowX * speed, geography.RiverFlowZ * speed);
            // A dry reach leaves its bed bare: the stage takes the water down to the ground; ice stills the current.
            if (reach is ReachStage now)
                return new(surface, ground, now.Ice ? 0 : currentX, now.Ice ? 0 : currentZ, body, now.Dry ? ground - surface : now.Stage, now.Ice && !now.Dry, id);
            return new(surface, ground, currentX, currentZ, body, 0, false, id);
        }

        if (lake is not null && Stages is IWaterStage stages)
            return new(surface, ground, 0, 0, body, 0, stages.LakeIce(lake), id);
        return new(surface, ground, 0, 0, body, 0, false, id);
    }

    /// <summary>The open sea's fetch (metres): long enough that its waves are as high as the wind can raise.</summary>
    internal const double SeaFetchMetres = 100_000;

    /// <summary>A river's fetch is this many of its widths (a wind finds a reach of open channel), but never less than <see cref="LeastRiverFetchMetres"/>.</summary>
    internal const double RiverFetchWidths = 4, LeastRiverFetchMetres = 20;

    /// <summary>
    /// What the waves (#9902) make of a sampled point whatever the wind: a lake's fetch from its area, a
    /// river's from its channel, the sea's long and swept by swell; none where there is no water or it is
    /// frozen, and less toward a shore (<see cref="WaterWaveSettings.Coverage"/>).
    /// </summary>
    internal WaveExposure Exposure(WaterSample sample)
    {
        double coverage = WaterWaveSettings.Coverage(sample.Depth, sample.Ice);
        if (coverage <= 0) return WaveExposure.Still;
        IReadOnlyList<Lake> lakes = recipe.Map.Lakes;
        return sample.Body switch
        {
            WaterBody.Sea => new(SeaFetchMetres, 1, coverage),
            WaterBody.Lake when sample.BodyId >= 0 && sample.BodyId < lakes.Count
                => new(SeaWaves.LakeFetchMetres(lakes[sample.BodyId].AreaSquareKilometres), 0, coverage),
            WaterBody.River => new(Math.Max(LeastRiverFetchMetres, RiverFetchWidths * Width(sample.BodyId)), 0, coverage),
            _ => WaveExposure.Still,
        };
    }

    /// <summary>A walked reach's channel width (metres), or none where there is no such reach.</summary>
    private double Width(int reach)
    {
        IReadOnlyList<Reach>? reaches = recipe.Regions?.Drainage.Network.Reaches;
        return reaches is not null && reach >= 0 && reach < reaches.Count ? reaches[reach].Width : 0;
    }

    /// <summary>
    /// The walked reaches narrower than <paramref name="widestMetres"/> (their mean channel) whose network nodes lie
    /// near a rectangle of the world (#10191), in id order; none where the world has no walked network.
    /// </summary>
    internal IEnumerable<int> StreamsWithin(double minX, double minZ, double maxX, double maxZ, double widestMetres)
    {
        if (recipe.Regions?.Drainage is not MapDrainage drainage) return [];
        IReadOnlyList<Reach> reaches = drainage.Network.Reaches;
        return drainage.ReachesWithin(minX, minZ, maxX, maxZ).Where(r => r < reaches.Count && reaches[r].Width < widestMetres);
    }

    /// <summary>A walked reach's centreline as the region tiles carve its channel (#10191).</summary>
    internal RiverPoint[] Centreline(int reach) =>
        recipe.Regions?.Drainage is MapDrainage drainage ? drainage.Shaped(reach) : [];

    /// <summary>
    /// A walked reach's channel half width (metres) at its flow now (#10191), by hydraulic geometry: its mean flow's
    /// until the stage is routed. A dry reach keeps its mean's.
    /// </summary>
    internal double HalfWidthNow(int reach)
    {
        IReadOnlyList<Reach>? reaches = recipe.Regions?.Drainage.Network.Reaches;
        if (reaches is null || reach < 0 || reach >= reaches.Count) return 0;
        return RiverGeometry.HalfWidth(Stages?.At(reach) is { Dry: false } now ? now.Flow : reaches[reach].Flow);
    }

    /// <summary>A walked reach's mean velocity (m/s), or still water where there is no such reach.</summary>
    private double MeanVelocity(int reach)
    {
        IReadOnlyList<Reach>? reaches = recipe.Regions?.Drainage.Network.Reaches;
        return reaches is not null && reach >= 0 && reach < reaches.Count ? reaches[reach].Current : 0;
    }

    /// <summary>
    /// Whether the water a place's geography stands under is frozen, or has run dry, now - for what draws
    /// water from geography alone (the far field) rather than from a column's sample.
    /// </summary>
    internal (bool Ice, bool Dry) State(MapSample geography, double x, double z)
    {
        if (Stages is not IWaterStage stages) return (false, false);
        double surface = WaterRules.Surface(geography);
        if (geography.InLake && geography.LakeSurface >= surface)
            return (recipe.Map.LakeAt(x, z) is Lake lake && stages.LakeIce(lake), false);
        if (geography.InRiver && geography.RiverSurface >= surface && stages.At(geography.RiverReach) is ReachStage reach)
            return (reach.Ice && !reach.Dry, reach.Dry);
        return (false, false);
    }

    /// <summary>The water at a point: the column it lies in.</summary>
    internal WaterSample Sample(double x, double z) => Sample((long)Math.Floor(x), (long)Math.Floor(z));

    /// <summary>The water at many columns at once.</summary>
    internal void Sample(ReadOnlySpan<(long X, long Z)> columns, Span<WaterSample> samples)
    {
        if (samples.Length < columns.Length) throw new ArgumentException("There must be a sample for every column.", nameof(samples));
        for (int i = 0; i < columns.Length; i++) samples[i] = Sample(columns[i].X, columns[i].Z);
    }
}
