using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The water at one place (#9877): its surface and the bed under it (metres), the horizontal current
/// (m/s; a river's reach velocity along its channel, #9879), which body it belongs to, the stage
/// offset over its usual surface (0 until the stage is routed, #9878) and whether it is frozen. The
/// fields match what an Engine water field reports for a region (rusty-engine #9884), so a flood
/// region can be composed over the static surfaces (#9880) without changing a consumer.
/// </summary>
internal readonly record struct WaterSample(double Surface, double Bed, double CurrentX, double CurrentZ, WaterBody Body, double Stage, bool Ice)
{
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
    /// <summary>The routed stage, or null while every body stands at its usual surface.</summary>
    internal IWaterStage? Stages { get; set; }

    /// <summary>A river's speed follows its flow to this power (Manning's velocity in a wide channel).</summary>
    private const double VelocityExponent = 0.4;

    /// <summary>The water at a column (whole metres, as the ground is generated).</summary>
    internal WaterSample Sample(long x, long z)
    {
        (double ground, MapSample geography) = recipe.GroundAt(x, z);
        double surface = WaterRules.Surface(geography);
        WaterBody body = WaterRules.Body(geography, ground);
        if (body == WaterBody.River)
        {
            ReachStage? reach = Stages?.At(geography.RiverReach);
            // The current (#9879): the reach's mean velocity, faster as it carries more, along its channel.
            double speed = MeanVelocity(geography.RiverReach) * Math.Pow(reach?.Ratio ?? 1, VelocityExponent);
            if (reach is { Dry: true }) speed = 0;
            (double currentX, double currentZ) = (geography.RiverFlowX * speed, geography.RiverFlowZ * speed);
            // A dry reach leaves its bed bare: the stage takes the water down to the ground; ice stills the current.
            if (reach is ReachStage now)
                return new(surface, ground, now.Ice ? 0 : currentX, now.Ice ? 0 : currentZ, body, now.Dry ? ground - surface : now.Stage, now.Ice && !now.Dry);
            return new(surface, ground, currentX, currentZ, body, 0, false);
        }

        if (body == WaterBody.Lake && Stages is IWaterStage stages && recipe.Map.LakeAt(x + 0.5, z + 0.5) is Lake lake)
            return new(surface, ground, 0, 0, body, 0, stages.LakeIce(lake));
        return new(surface, ground, 0, 0, body, 0, false);
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
