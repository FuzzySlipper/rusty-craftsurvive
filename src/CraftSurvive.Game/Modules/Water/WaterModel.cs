using CraftSurvive.Game.Modules.WorldGen;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// The water at one place (#9877): its surface and the bed under it (metres), the horizontal current
/// (m/s; still for now - the network's reach velocity later), which body it belongs to, the stage
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

/// <summary>
/// The one owner of where water stands in the walking world (#9877): the sea, rivers and lakes as
/// surfaces over the generated ground, from <see cref="WaterRules"/>. It answers a point or a batch;
/// the near water surfaces, the swim volume and anything else that asks read it, never voxels.
/// </summary>
internal sealed class WaterModel(TerrainRecipe recipe)
{
    /// <summary>The water at a column (whole metres, as the ground is generated).</summary>
    internal WaterSample Sample(long x, long z)
    {
        (double ground, MapSample geography) = recipe.GroundAt(x, z);
        return new(WaterRules.Surface(geography), ground, 0, 0, WaterRules.Body(geography, ground), 0, false);
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
