using System.Numerics;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// A river's current on a swimmer (#9879). Moving water carries a swimmer with it (the push, passed to
/// the character controller as external velocity), and in it a swimmer's stroke is only as strong as they
/// are (<see cref="Rpg.CharacterRules.SwimStroke"/>), so a current stronger than a swimmer's stroke sweeps
/// them away. The force builds: for <see cref="GraceSeconds"/> after entering moving water only
/// <see cref="GraceShare"/> of it pulls - time to turn back to the bank - and it then grows to full over
/// <see cref="RampSeconds"/>; out of the current the exposure drains <see cref="RecoverRate"/> times as fast.
/// Still water (a lake, ice, a pool) leaves swimming as it was. Engine-free; tuning lives here.
/// </summary>
internal sealed class SwimCurrent
{
    /// <summary>Water slower than this (m/s) is still.</summary>
    internal const double StillWater = 0.05;

    /// <summary>How long a swimmer has before the current takes hold (seconds), and the share of it that pulls meanwhile.</summary>
    internal const double GraceSeconds = 1.0, GraceShare = 0.2;

    /// <summary>How long the current then takes to bear with full force (seconds).</summary>
    internal const double RampSeconds = 2.5;

    /// <summary>How many times faster the exposure drains out of the current than it builds in it.</summary>
    internal const double RecoverRate = 2;

    /// <summary>How long the swimmer has been in moving water, as the force reads it (seconds).</summary>
    internal double Exposure { get; private set; }

    /// <summary>The share of the current bearing on the swimmer now (0 to 1).</summary>
    internal double Share => ShareAt(Exposure);

    /// <summary>The current carrying the swimmer now (m/s, ground plane).</summary>
    internal Vector2 Push { get; private set; }

    /// <summary>The swimmer's stroke now (m/s): their own swimming speed, falling to their stroke in a current as it takes hold.</summary>
    internal float Stroke { get; private set; } = PlayerConstants.WaterSpeed;

    /// <summary>The current at the swimmer's place, as last read (m/s).</summary>
    internal Vector2 Current { get; private set; }

    /// <summary>The share of the current bearing after so long in it.</summary>
    internal static double ShareAt(double exposure) => exposure <= GraceSeconds
        ? GraceShare
        : GraceShare + ((1 - GraceShare) * Smooth(Math.Min(1, (exposure - GraceSeconds) / RampSeconds)));

    /// <summary>
    /// One controller step: the current at the swimmer (null when not swimming) and their stroke in a full
    /// current (m/s). Builds or drains the exposure, and sets the push and the stroke for this step.
    /// </summary>
    internal void Step(double seconds, Vector2? current, double strokeInCurrent)
    {
        Vector2 flowing = current is Vector2 water && water.Length() > StillWater ? water : Vector2.Zero;
        Current = flowing;
        Exposure = flowing == Vector2.Zero ? Math.Max(0, Exposure - (seconds * RecoverRate)) : Exposure + seconds;
        double share = flowing == Vector2.Zero ? 0 : Share;
        Push = flowing * (float)share;
        double stroke = Math.Min(PlayerConstants.WaterSpeed, strokeInCurrent);
        Stroke = (float)(PlayerConstants.WaterSpeed + ((stroke - PlayerConstants.WaterSpeed) * share));
    }

    private static double Smooth(double t) => t * t * (3 - (2 * t));
}
