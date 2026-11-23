using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The fog foes' ways as Engine-free rules (#10985; den-wiki wandering-years/fog-wildlife, "Roster and rules"): how loud
/// what the player does is, who hears it, when one senses the player, where it circles off to, how it floats and how
/// fast its teeth click. The creature module reads the world and applies these.
/// </summary>
internal static class FogRules
{
    // How loud each thing the player does is, as a share of a fog foe's hearing: running and a heavy landing carry
    // furthest, a swing (iron on stone, or on whatever it meets) nearly as far, a walking step hardly at all.
    internal const double RunningStep = 1.0, WalkingStep = 0.2, Jump = 0.3, Landing = 0.5, HardLanding = 1.0, Splash = 0.6, Swing = 0.8;

    /// <summary>
    /// How loud the player was in one update, as a share of a fog foe's hearing; 0 when silent. The loudest thing done
    /// counts. <paramref name="heard"/> is what their body did (its footfalls, jumps, landings and splashes).
    /// </summary>
    internal static double Loudness(BodySound heard, bool sprinting, bool swung)
    {
        double loudest = swung ? Swing : 0;
        if (heard.HasFlag(BodySound.Footstep)) loudest = Math.Max(loudest, sprinting ? RunningStep : WalkingStep);
        if (heard.HasFlag(BodySound.Jump)) loudest = Math.Max(loudest, Jump);
        if (heard.HasFlag(BodySound.Land)) loudest = Math.Max(loudest, Landing);
        if (heard.HasFlag(BodySound.HardLanding)) loudest = Math.Max(loudest, HardLanding);
        if (heard.HasFlag(BodySound.Splash)) loudest = Math.Max(loudest, Splash);
        return loudest;
    }

    /// <summary>Whether a fog foe hears a noise this loud this far off, hearing a full-loudness noise as far as <paramref name="hearMetres"/>.</summary>
    internal static bool Hears(double loudness, double hearMetres, double distance) => loudness > 0 && distance <= loudness * hearMetres;

    /// <summary>
    /// Whether a fog foe senses the player now: only while they are in the mist with it, and then if it heard them within
    /// its while or they are within its feel, however quiet. Out of the mist it comes no further: its edge is safety.
    /// </summary>
    internal static bool Senses(WildlifeFog fog, bool playerInMist, double heardAgoSeconds, double distance) =>
        playerInMist && (heardAgoSeconds <= fog.HeardSeconds || distance <= fog.FeelMetres);

    /// <summary>
    /// The angles at which a fog foe tries to circle off, in order (degrees), off the way from the player back to it: well
    /// to one side, then the other, then less so, and last straight back along the way it came, where a narrow passage
    /// leaves no room to either side.
    /// </summary>
    private static readonly double[] CircleAngles = [70, -70, 45, -45, 20, -20, 0];

    /// <summary>
    /// Where a fog foe circles off to after a bite: <paramref name="metres"/> from the player, swung to one side of the
    /// way it came where there is room, so it curves off rather than backing straight out. The first such place still in
    /// the mist; null when none is.
    /// </summary>
    /// <param name="side">Which way it swings first: 1 to its left, -1 to its right (each foe keeps its own).</param>
    internal static Vector2? CircleTo(Vector2 creature, Vector2 player, double metres, int side, Func<Vector2, bool> inMist)
    {
        ArgumentNullException.ThrowIfNull(inMist);
        Vector2 back = creature - player;
        double heading = back.LengthSquared() > 1e-6f ? Math.Atan2(back.Y, back.X) : 0;
        foreach (double angle in CircleAngles)
        {
            double turn = heading + (side * angle * Math.PI / 180);
            Vector2 to = player + (new Vector2((float)Math.Cos(turn), (float)Math.Sin(turn)) * (float)metres);
            if (inMist(to)) return to;
        }

        return null;
    }

    /// <summary>
    /// How high over its floor a fog foe swims now: its float, rising and falling over its bob, from its own phase. A
    /// dead one sinks to the floor at <see cref="SinkMetresPerSecond"/> from wherever it was.
    /// </summary>
    /// <param name="clock">The simulation time now, in seconds, which its bob follows.</param>
    /// <param name="lift">How high it swam last update.</param>
    /// <param name="seconds">The update's seconds, over which a dead one sinks.</param>
    internal static float Float(WildlifeFog fog, double clock, double phase, bool dead, float lift, double seconds)
    {
        ArgumentNullException.ThrowIfNull(fog);
        if (dead) return Math.Max(0f, lift - (float)(SinkMetresPerSecond * seconds));
        return (float)(fog.FloatMetres + (fog.BobMetres * Math.Sin((2 * Math.PI * clock / fog.BobSeconds) + phase)));
    }

    /// <summary>How fast a dead fog foe sinks to the floor.</summary>
    internal const double SinkMetresPerSecond = 1.5;

    /// <summary>
    /// How long until a hunting fog foe's teeth click again, this far from the player: its far pace at the edge of its
    /// hearing, quickening to its near pace at the player.
    /// </summary>
    internal static double ClickEvery(WildlifeFog fog, double hearMetres, double distance)
    {
        ArgumentNullException.ThrowIfNull(fog);
        double near = 1 - Math.Clamp(distance / Math.Max(hearMetres, 1e-6), 0, 1);
        return fog.ClickFarSeconds + ((fog.ClickNearSeconds - fog.ClickFarSeconds) * near);
    }
}
