namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How far a drink has taken the lantern (#10966).</summary>
internal enum LanternPhase
{
    /// <summary>Burning as ever.</summary>
    Steady,

    /// <summary>Dimming and flickering while something raises its crescent at it.</summary>
    Guttering,

    /// <summary>Drunk: out for a while.</summary>
    Out,

    /// <summary>Sputtering back up.</summary>
    Returning,
}

/// <summary>
/// The player's lantern's drained state (#10966), as settled with Patch on the wiki's Ruins wildlife page ("Its crescent drinks
/// light"): while an implement-carrier raises its crescent at it, the lantern gutters, dimming to about half and flickering;
/// at the end of the raise it goes out for a while, then sputters back up. Steadied before the end (the player stepped out
/// of reach, broke its sight, or struck it), it burns as ever; the drinker killed, it is back at once. The creature rules
/// set it; the dungeon module owns it and draws the lantern by its <see cref="Share"/>. It touches no other light.
/// Engine-free.
/// </summary>
internal sealed class LanternDrain
{
    /// <summary>How far a guttering lantern dims by the end of the raise, before it goes out.</summary>
    internal const float GutterShare = 0.5f;

    /// <summary>How long one flicker lasts: the lantern's brightness jumps this often while it gutters or sputters.</summary>
    internal const double FlickerSeconds = 0.07;

    /// <summary>The deepest a flicker dips at the end of a gutter, as a share of the lantern's dimmed brightness.</summary>
    internal const float FlickerDepth = 0.6f;

    private double phaseSeconds;
    private double phaseLength;
    private double returnSeconds;
    private double clock;

    internal LanternPhase Phase { get; private set; } = LanternPhase.Steady;

    /// <summary>The creature drinking it, while one is; null while it burns steady.</summary>
    internal int? Drinker { get; private set; }

    /// <summary>Whether the player carries it lit now (the dungeon module's say: inside, or in the caves under the world, with a lantern treatment).</summary>
    internal bool Carried { get; set; }

    /// <summary>Lit and steady: something may start to drink it.</summary>
    internal bool Drinkable => Carried && Phase == LanternPhase.Steady;

    /// <summary>How far into its phase it is, 0 to 1.</summary>
    internal double Progress => phaseLength > 0 ? Math.Clamp(phaseSeconds / phaseLength, 0, 1) : 1;

    /// <summary>How long it has left in its phase, in seconds.</summary>
    internal double Left => Math.Max(0, phaseLength - phaseSeconds);

    /// <summary>
    /// How much of its light its drinker holds, 0 to 1: rising as it gutters, all of it while it is out, and handed back as
    /// it returns. The crescent's core burns by it.
    /// </summary>
    internal float Taken => Phase switch
    {
        LanternPhase.Guttering => (float)Progress,
        LanternPhase.Out => 1f,
        LanternPhase.Returning => 1f - (float)Progress,
        _ => 0f,
    };

    /// <summary>
    /// How brightly it burns, as a share of its own light, flicker and all: whole while steady; dimming toward
    /// <see cref="GutterShare"/> and flickering harder as it gutters; nothing while out; sputtering back up as it returns.
    /// </summary>
    internal float Share => Phase switch
    {
        LanternPhase.Guttering => (1f - ((1f - GutterShare) * (float)Progress)) * Flicker((float)Progress),
        LanternPhase.Out => 0f,
        LanternPhase.Returning => (float)Progress * Flicker(1f - (float)Progress),
        _ => 1f,
    };

    /// <summary>
    /// A creature starts to drink: the lantern gutters for <paramref name="seconds"/>, the rest of its raise. Only a lit,
    /// steady lantern can be drunk; returns whether this one is.
    /// </summary>
    internal bool Gutter(int by, double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);
        if (!Drinkable) return false;
        Drinker = by;
        Enter(LanternPhase.Guttering, seconds);
        return true;
    }

    /// <summary>The drink is cut short before the end of the raise: a guttering lantern burns steady again at once.</summary>
    internal void Steady(int by)
    {
        if (Drinker != by || Phase != LanternPhase.Guttering) return;
        Drinker = null;
        Enter(LanternPhase.Steady, 0);
    }

    /// <summary>
    /// The raise ends with the drink whole: a guttering lantern goes out for <paramref name="outSeconds"/>, then sputters back
    /// over <paramref name="backSeconds"/>.
    /// </summary>
    internal void Drink(int by, double outSeconds, double backSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(backSeconds);
        if (Drinker != by || Phase != LanternPhase.Guttering) return;
        returnSeconds = backSeconds;
        Enter(LanternPhase.Out, outSeconds);
    }

    /// <summary>Its drinker is killed: whatever it took is back at once.</summary>
    internal void Restore(int by)
    {
        if (Drinker != by) return;
        Clear();
    }

    /// <summary>Burns steady with no drinker (a restart, or leaving the dark).</summary>
    internal void Clear()
    {
        Drinker = null;
        Enter(LanternPhase.Steady, 0);
    }

    /// <summary>Time passes: out turns to returning, returning to steady.</summary>
    internal void Step(double seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seconds);
        clock += seconds;
        phaseSeconds += seconds;
        while (phaseSeconds >= phaseLength && Phase is LanternPhase.Out or LanternPhase.Returning)
        {
            double over = phaseSeconds - phaseLength;
            if (Phase == LanternPhase.Out) Enter(LanternPhase.Returning, returnSeconds);
            else
            {
                Drinker = null;
                Enter(LanternPhase.Steady, 0);
            }

            phaseSeconds = over;
        }
    }

    internal string Readout() => Phase == LanternPhase.Steady
        ? $"lantern {(Carried ? "lit" : "not carried")} steady"
        : $"lantern {Phase.ToString().ToLowerInvariant()} by {Drinker} {Left:F1}s left share={Share:F2} taken={Taken:F2}";

    private void Enter(LanternPhase phase, double seconds)
    {
        Phase = phase;
        phaseSeconds = 0;
        phaseLength = seconds;
    }

    /// <summary>
    /// A flicker of some <paramref name="depth"/> (0 to 1): each <see cref="FlickerSeconds"/> the lantern takes a fresh
    /// brightness, deeper dips the deeper it is, drawn from the clock so a replay flickers alike.
    /// </summary>
    private float Flicker(float depth)
    {
        ulong tick = (ulong)(clock / FlickerSeconds);
        ulong mixed = (tick + 0x9E37_79B9_7F4A_7C15UL) * 0xBF58_476D_1CE4_E5B9UL;
        mixed ^= mixed >> 31;
        float noise = (mixed >> 40) / (float)(1UL << 24);
        return 1f - (FlickerDepth * depth * noise);
    }
}
