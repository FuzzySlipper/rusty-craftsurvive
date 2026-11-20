using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>How a <see cref="WildlifeBreath"/> is drawn (#10882): where the mouth is as the jaws are posed, and the burst of haze leaving it.</summary>
internal static class WildlifeBreathLook
{
    /// <summary>A joint's own length runs along its local +Y, as the GLB export lays a chain.</summary>
    private static readonly Vector3 Along = Vector3.UnitY;

    /// <summary>The mouth's point and the way the jaws point, from their joints as posed, in the scene's frame.</summary>
    internal static (Vector3 Mouth, Vector3 Forward) Mouth(WildlifeBreath breath, JointPose upper, JointPose lower, float scale)
    {
        Vector3 upperForward = Vector3.Normalize(Vector3.Transform(Along, upper.World.Rotation));
        Vector3 lowerForward = Vector3.Normalize(Vector3.Transform(Along, lower.World.Rotation));
        Vector3 upperTip = upper.World.Translation + (upperForward * breath.UpperReachMetres * scale);
        Vector3 lowerTip = lower.World.Translation + (lowerForward * breath.LowerReachMetres * scale);
        Vector3 forward = upperForward + lowerForward;
        return ((upperTip + lowerTip) / 2, forward.LengthSquared() > 1e-6f ? Vector3.Normalize(forward) : upperForward);
    }

    /// <summary>One burst of haze leaving a mouth at a point, the way it faces, from a seed.</summary>
    internal static PresentationParticleDescriptor Describe(WildlifeBreath breath, Vector3 mouth, Vector3 forward, RenderResourceReference sprite, ulong seed, float scale)
    {
        Vector3 spread = new(breath.SpreadMetresPerSecond * scale);
        Vector3 slow = forward * breath.SpeedMin * scale, fast = forward * breath.SpeedMax * scale;
        BurstStyle style = new(WildlifeBreath.Signal, BurstLook.Puff, breath.Count, breath.LifetimeMinSeconds, breath.LifetimeMaxSeconds,
            Vector3.Min(slow, fast) - spread, Vector3.Max(slow, fast) + spread, new Vector3(0f, breath.RiseMetresPerSecondSquared, 0f),
            [.. breath.Sizes.Select(key => (key.Age, key.Size * scale))],
            [.. breath.Colours.Select(key => (key.Age, new Color(key.Colour.X, key.Colour.Y, key.Colour.Z, key.Colour.W)))]);
        return Bursts.Describe(style, mouth, sprite, seed);
    }
}
