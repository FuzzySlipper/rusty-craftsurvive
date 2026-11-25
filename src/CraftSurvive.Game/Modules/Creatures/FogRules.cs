using System.Numerics;
using CraftSurvive.Game.Modules.Player;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The fog foes' ways as Engine-free rules (#10985; den-wiki wandering-years/fog-wildlife, "Roster and rules"): how loud
/// what the player does is, who hears it, when one senses the player, where it circles off to, where a darter loops
/// (#10986), how it floats and how often it makes its sound. The creature module reads the world and applies these.
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
    /// Whether a hunter may start or land a bite (R10985-1): any but a fog foe as its sight already decides, a fog foe only
    /// along a clear line to the player. It hunts by what it hears, and hearing passes walls that teeth do not.
    /// </summary>
    internal static bool ClearToBite(bool floats, bool clearLine) => !floats || clearLine;

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
    /// Where a waiter (#10987) withdraws to: off to one side of the way it came as it circles off, at
    /// <paramref name="metres"/> from the player where the mist allows, or nearer, half a metre at a time down to
    /// <paramref name="least"/> (just out of its reach), so it withdraws even where the mist leaves little room. Null when
    /// no such place is in the mist.
    /// </summary>
    internal static Vector2? WithdrawTo(Vector2 creature, Vector2 player, double metres, double least, int side, Func<Vector2, bool> inMist)
    {
        for (double distance = metres; distance >= least - 1e-9; distance -= WithdrawStepMetres)
        {
            if (CircleTo(creature, player, distance, side, inMist) is Vector2 to) return to;
        }

        return null;
    }

    /// <summary>How much nearer a waiter tries each time it finds no room to withdraw.</summary>
    internal const double WithdrawStepMetres = 0.5;

    /// <summary>
    /// Where a darter (#10986) loops to next while the player stays out of its dart: <paramref name="metres"/> from the
    /// player, <paramref name="degrees"/> further round them from where it is, on its own side, so it swims round and
    /// round them. Where the mist leaves no room that way (a narrow passage) it tries less far round, then the other way;
    /// null when no such place is in the mist.
    /// </summary>
    /// <param name="side">Which way round it swims: 1 to its left, -1 to its right (each foe keeps its own).</param>
    internal static Vector2? LoopTo(Vector2 creature, Vector2 player, double metres, double degrees, int side, Func<Vector2, bool> inMist)
    {
        ArgumentNullException.ThrowIfNull(inMist);
        Vector2 from = creature - player;
        double bearing = from.LengthSquared() > 1e-6f ? Math.Atan2(from.Y, from.X) : 0;
        foreach (int way in (int[])[side, -side])
        {
            foreach (double share in LoopShares)
            {
                double turn = bearing + (way * share * degrees * Math.PI / 180);
                Vector2 to = player + (new Vector2((float)Math.Cos(turn), (float)Math.Sin(turn)) * (float)metres);
                if (inMist(to)) return to;
            }
        }

        return null;
    }

    /// <summary>How much of its loop's step round a darter tries, in order, where the mist leaves less room.</summary>
    private static readonly double[] LoopShares = [1, 0.6, 0.3];

    /// <summary>
    /// Whether a darter (#10986) darts in now: the player has come within its dart, as one driving it off does, and its
    /// bite is <paramref name="ready"/> (cooled down, along a clear line). One within its dart that cannot bite yet does
    /// not wait in the player's face: it loops away, and darts when it can.
    /// </summary>
    internal static bool Darts(WildlifeFog fog, double distance, bool ready)
    {
        ArgumentNullException.ThrowIfNull(fog);
        return ready && fog.DartMetres is double dart && distance <= dart;
    }

    /// <summary>
    /// What a darter (#10986) does this update: dart in, loop on, or leave its course as it is. Hunting or searching, it
    /// loops round the player; when they come within its dart and its bite is ready it drops its loop and darts. Its
    /// readiness is its cooldown and a clear line, not whether it is already attacking: a looping darter is held searching
    /// by its loop (R10986-1). One within its dart that cannot bite yet loops away; one on another errand (circling off),
    /// mid-bite or resting keeps its course.
    /// </summary>
    /// <param name="looping">Whether it is on its loop now.</param>
    /// <param name="otherErrand">Whether it is on some other errand, such as circling off after a bite.</param>
    internal static DarterMove Darter(WildlifeFog fog, double distance, CreatureState state, bool looping, bool otherErrand, bool biting, bool cooledDown, bool clearLine)
    {
        ArgumentNullException.ThrowIfNull(fog);
        if (Darts(fog, distance, cooledDown && clearLine)) return looping ? DarterMove.Dart : DarterMove.Keep;
        if (looping || otherErrand || biting || state is not (CreatureState.Pursuing or CreatureState.Attacking or CreatureState.Searching)) return DarterMove.Keep;
        return DarterMove.Loop;
    }

    /// <summary>
    /// What a waiter (#10987, the gulper) does this update. It never comes at the player: hearing them, it holds still
    /// where it hangs; once they are within its reach with its bite cooled and a clear line, it strikes; within its reach
    /// while it is still cooling down, it withdraws; when it no longer senses them it rests where it is. One on another
    /// errand (withdrawing), mid-bite, resting or dead keeps its course.
    /// </summary>
    /// <param name="senses">Whether it senses the player now (by hearing or feel, in the mist).</param>
    /// <param name="holding">Whether it is holding still now.</param>
    internal static WaiterMove Waiter(double distance, double reach, CreatureState state, bool senses, bool holding, bool otherErrand, bool biting, bool cooledDown, bool clearLine)
    {
        if (state is CreatureState.Dead or CreatureState.Resting || biting || otherErrand) return WaiterMove.Keep;
        if (!senses) return holding ? WaiterMove.Rest : WaiterMove.Keep;
        if (distance <= reach)
        {
            if (!cooledDown) return WaiterMove.Withdraw;
            if (clearLine) return holding ? WaiterMove.Strike : WaiterMove.Keep;
        }

        return holding ? WaiterMove.Keep : WaiterMove.Hold;
    }

    /// <summary>
    /// Whether a fog foe makes its sound in this state: the needle-mouth's teeth click while it hunts; the fade-ribbon
    /// flutters whenever it swims, resting or dead excepted, so it is sensed before it is seen (#10986); the gulper's
    /// throat pulses even at rest, so it is heard before it is reached (#10987).
    /// </summary>
    internal static bool Sounds(FogSound sound, CreatureState state) => sound switch
    {
        FogSound.Pulse => state is not CreatureState.Dead,
        FogSound.Flutter => state is not (CreatureState.Resting or CreatureState.Dead),
        _ => state is CreatureState.Pursuing or CreatureState.Attacking,
    };

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

/// <summary>What a darter does this update (#10986): keeps its course, drops its loop to dart in, or sets off on its loop.</summary>
internal enum DarterMove
{
    Keep,
    Dart,
    Loop,
}

/// <summary>What a waiter does this update (#10987): keeps its course, holds still, strikes, withdraws, or rests where it is.</summary>
internal enum WaiterMove
{
    Keep,
    Hold,
    Strike,
    Withdraw,
    Rest,
}
