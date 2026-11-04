using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Plants a posed creature's feet on the ground under them: the two-bone IK the style test proved (#10568) and wildlife uses (#10569).</summary>
internal static class CreatureFeet
{
    /// <summary>A pole sits this far out from the middle joint, along the leg's current bend.</summary>
    private const float PoleMetres = 1;

    /// <summary>
    /// A two-bone chain per leg toward the ground under where the clip puts the foot, keeping the clip's
    /// own lift and bending the way the leg already bends. No foot moves more than
    /// <paramref name="mostMetres"/>: a cast that lands on a prop must not drag a leg up it.
    /// </summary>
    /// <param name="world">The creature's placement in world metres; <paramref name="local"/> is the same point in the session.</param>
    /// <returns>The chains, and how far the furthest foot moved.</returns>
    internal static (TwoBoneIk[] Chains, double Worst) Chains(ReadOnlySpan<JointPose> pose, IReadOnlyList<(uint Root, uint Mid, uint End)> legs, float scale,
        Vector3 world, Vector3 local, Func<double, double, float> groundAt, float mostMetres = float.PositiveInfinity)
    {
        TwoBoneIk[] chains = new TwoBoneIk[legs.Count];
        double worst = 0;
        for (int leg = 0; leg < legs.Count; leg++)
        {
            (uint root, uint mid, uint end) = legs[leg];
            Vector3 a = pose[(int)root].World.Translation, b = pose[(int)mid].World.Translation, c = pose[(int)end].World.Translation;
            // The clip's own height of the foot above the creature's ground, kept over the real ground below it.
            float lift = pose[(int)end].Model.Translation.Y * scale;
            Vector3 foot = world + (c - local);
            float ground = groundAt(foot.X, foot.Z);
            float wanted = local.Y + (ground - world.Y) + lift;
            Vector3 target = c with { Y = Math.Clamp(wanted, c.Y - mostMetres, c.Y + mostMetres) };
            worst = Math.Max(worst, Math.Abs(target.Y - c.Y));
            Vector3 bend = b - ((a + c) / 2);
            Vector3 pole = b + (bend.LengthSquared() > 1e-6f ? Vector3.Normalize(bend) * PoleMetres : Vector3.UnitY * PoleMetres);
            chains[leg] = new TwoBoneIk(root, mid, end, PoseSpace.World, target, pole, 1);
        }

        return (chains, worst);
    }
}
