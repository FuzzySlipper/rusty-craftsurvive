namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// A river channel's size and speed from its flow Q (m³/s; <see cref="WaterBudget"/>), by hydraulic
/// geometry (#9874): width 4.5·Q^0.5 and depth 0.35·Q^0.4 metres, so a canoe river of 5 m³/s is about
/// 10 m wide and a great river of 250 m³/s about 70 m; every channel is at least a stream's size.
/// Speed follows Manning's rule for a natural channel (#9875).
/// </summary>
internal static class RiverGeometry
{
    private const double WidthPerRootFlow = 4.5;
    private const double DepthPerFlow = 0.35;
    private const double DepthFlowExponent = 0.4;
    internal const double MinimumHalfWidth = 1.6;
    internal const double MinimumDepth = 1.2;

    /// <summary>Manning's roughness for a winding natural channel with some weeds and stones.</summary>
    private const double Roughness = 0.035;

    /// <summary>Mean speeds are kept between a slack lowland river's and a mountain torrent's (m/s).</summary>
    private const double SlowestVelocity = 0.2, FastestVelocity = 3;

    internal static double HalfWidth(double flow) => Math.Max(MinimumHalfWidth, WidthPerRootFlow * Math.Sqrt(Math.Max(0, flow)) / 2);

    internal static double Depth(double flow) => Math.Max(MinimumDepth, DepthPerFlow * Math.Pow(Math.Max(0, flow), DepthFlowExponent));

    /// <summary>Mean velocity (m/s) at a depth and gradient, the channel taken as wide and shallow (its radius its depth).</summary>
    internal static double Velocity(double depth, double gradient) =>
        Math.Clamp(Math.Pow(depth, 2.0 / 3.0) * Math.Sqrt(Math.Max(0, gradient)) / Roughness, SlowestVelocity, FastestVelocity);
}
