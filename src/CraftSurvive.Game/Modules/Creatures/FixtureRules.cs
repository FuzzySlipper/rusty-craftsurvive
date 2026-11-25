using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>Where a ruin fixture is in its act (#10963).</summary>
internal enum FixturePhase
{
    /// <summary>At rest, waiting for the player to come within its reach.</summary>
    Resting,

    /// <summary>Showing its tell: unfolding, shivering, creaking, swelling.</summary>
    Telling,

    /// <summary>Acting: holding the player, lashing, shut, or splitting a vessel and waiting to split the next.</summary>
    Acting,

    /// <summary>Folding away, rising, or open again, before it can act again.</summary>
    Recovering,
}

/// <summary>What a fixture's act did this step, for the creature module to apply.</summary>
[Flags]
internal enum FixtureEffect
{
    None = 0,

    /// <summary>It seized the player.</summary>
    Seize = 1,

    /// <summary>It let them go.</summary>
    Release = 2,

    /// <summary>Its hold or its digesting hurts the player: <see cref="WildlifeFixture.Damage"/>.</summary>
    Hurt = 4,

    /// <summary>Its lobes lashed the player: <see cref="WildlifeFixture.Damage"/>, and numbing.</summary>
    Sting = 8,

    /// <summary>It folded shut.</summary>
    Close = 16,

    /// <summary>It opened again.</summary>
    Open = 32,

    /// <summary>A vessel split and hatched.</summary>
    Hatch = 64,

    /// <summary>A blow smashed a vessel before it hatched.</summary>
    Smash = 128,
}

/// <summary>What a fixture senses this step: all read before it decides.</summary>
/// <param name="PlayerMetres">How far the player stands from its middle, across the ground; infinity on another floor.</param>
/// <param name="SeesPlayer">The Engine's perception: a vessel acts only on a player it sees.</param>
/// <param name="PlayerCanBeHit">The player is up and not just risen.</param>
/// <param name="Struck">A blow landed on it since it last decided.</param>
/// <param name="BrokeFree">The player it holds broke free since it last decided.</param>
internal readonly record struct FixtureFacts(double PlayerMetres, bool SeesPlayer, bool PlayerCanBeHit, bool Struck, bool BrokeFree);

/// <summary>A fixture's own state: owned by the creature module, decided by <see cref="FixtureRules"/>.</summary>
internal sealed class FixtureState
{
    internal FixturePhase Phase { get; set; }

    /// <summary>How long it has been in its phase.</summary>
    internal double Seconds { get; set; }

    /// <summary>How long its hold or its digesting has gone since it last hurt the player.</summary>
    internal double SinceHurt { get; set; }

    /// <summary>Whether its lash has come down yet this act.</summary>
    internal bool Lashed { get; set; }

    /// <summary>How long a shut bower has stood empty.</summary>
    internal double EmptySeconds { get; set; }

    /// <summary>Blows on the membrane of a shut bower, or toward smashing a vessel's next vessel.</summary>
    internal int Blows { get; set; }

    /// <summary>A vessel's vessels spent, split or smashed.</summary>
    internal int Spent { get; set; }

    /// <summary>How long since its last vessel was spent: they regrow together after the brood's regrowth.</summary>
    internal double SinceSpent { get; set; }

    /// <summary>A blow landed on it since it last decided; the module sets it, the next step reads it.</summary>
    internal bool StruckPending { get; set; }

    /// <summary>The update step it was last tended at; -1 before its first.</summary>
    internal long LastStep { get; set; } = -1;

    /// <summary>Which way it faces (a creature's yaw), kept for whichever creature wakes for it.</summary>
    internal float Yaw { get; set; }

    /// <summary>What it last sensed, for the readout.</summary>
    internal FixtureFacts LastFacts { get; set; }

    /// <summary>How much of its flinch is left.</summary>
    internal double HurtSeconds { get; set; }

    internal void Enter(FixturePhase phase)
    {
        Phase = phase;
        Seconds = 0;
    }
}

/// <summary>
/// The ruin fixtures' hazards (#10963), as settled with Patch on the wiki's Ruins wildlife page ("Fixtures as hazards").
/// Each is rooted, shows a tell before it acts, and acts only on a player within its reach:
/// <list type="bullet">
/// <item>the niche unfolder seizes a player who stays in reach through its unfolding, and holds them, hurting them, until
/// its hold runs out, a blow lands on it, or they break free; then it folds away and rests;</item>
/// <item>the vault filter, once its tell has shown, drops its lobes whether or not the player has stepped away: one still
/// beneath is stung and numbed; then its lobes rise and it rests;</item>
/// <item>the folding bower shuts on a player who stays under it through its creak, and digests them while they are
/// inside; cut through its membrane, or left empty long enough, it opens again, and closes again only after a while;</item>
/// <item>the brood vessel, while it sees the player within its reach or after a blow, swells a vessel, splits it to hatch,
/// and waits before the next, until its vessels are spent; spent vessels regrow together, and blows smash whole ones.</item>
/// </list>
/// Engine-free: the creature module reads the facts, applies the effects and draws the phases.
/// </summary>
internal static class FixtureRules
{
    /// <summary>How far past its reach a filter's lash still finds a player who stepped away during its tell.</summary>
    internal const double LashSlackMetres = 0.3;

    internal static FixtureEffect Step(WildlifeFixture fixture, FixtureState state, FixtureFacts facts, double seconds)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(state);
        state.Seconds += seconds;
        state.HurtSeconds = Math.Max(0, state.HurtSeconds - seconds);
        bool within = facts.PlayerMetres <= fixture.ReachMetres;
        return fixture.Act switch
        {
            FixtureAct.Seizes => Seizes(fixture, state, facts, within, seconds),
            FixtureAct.Numbs => Numbs(fixture, state, facts, within),
            FixtureAct.Closes => Closes(fixture, state, facts, within, seconds),
            FixtureAct.Hatches => Hatches(fixture, state, facts, within, seconds),
            _ => FixtureEffect.None,
        };
    }

    private static FixtureEffect Seizes(WildlifeFixture fixture, FixtureState state, FixtureFacts facts, bool within, double seconds)
    {
        switch (state.Phase)
        {
            case FixturePhase.Resting:
                if (within && facts.PlayerCanBeHit) state.Enter(FixturePhase.Telling);
                return FixtureEffect.None;
            case FixturePhase.Telling:
                // Stepping back out of reach while it unfolds escapes it.
                if (!within || !facts.PlayerCanBeHit)
                {
                    state.Enter(FixturePhase.Resting);
                    return FixtureEffect.None;
                }

                if (state.Seconds < fixture.TellSeconds) return FixtureEffect.None;
                state.Enter(FixturePhase.Acting);
                state.SinceHurt = 0;
                return FixtureEffect.Seize;
            case FixturePhase.Acting:
                if (facts.Struck || facts.BrokeFree || !facts.PlayerCanBeHit || state.Seconds >= fixture.ActSeconds)
                {
                    state.Enter(FixturePhase.Recovering);
                    return FixtureEffect.Release;
                }

                return HurtsAgain(fixture, state, seconds);
            default:
                if (state.Seconds >= fixture.RestSeconds) state.Enter(FixturePhase.Resting);
                return FixtureEffect.None;
        }
    }

    private static FixtureEffect Numbs(WildlifeFixture fixture, FixtureState state, FixtureFacts facts, bool within)
    {
        switch (state.Phase)
        {
            case FixturePhase.Resting:
                if (within && facts.PlayerCanBeHit) state.Enter(FixturePhase.Telling);
                return FixtureEffect.None;
            case FixturePhase.Telling:
                // Once its tell has begun its lobes come down: a watchful player has stepped away by then.
                if (state.Seconds < fixture.TellSeconds) return FixtureEffect.None;
                state.Enter(FixturePhase.Acting);
                state.Lashed = false;
                return FixtureEffect.None;
            case FixturePhase.Acting:
                FixtureEffect lash = FixtureEffect.None;
                if (!state.Lashed && state.Seconds >= fixture.StrikeAfterSeconds)
                {
                    state.Lashed = true;
                    if (facts.PlayerMetres <= fixture.ReachMetres + LashSlackMetres && facts.PlayerCanBeHit) lash = FixtureEffect.Sting;
                }

                if (state.Seconds >= fixture.ActSeconds) state.Enter(FixturePhase.Recovering);
                return lash;
            default:
                if (state.Seconds >= fixture.RestSeconds) state.Enter(FixturePhase.Resting);
                return FixtureEffect.None;
        }
    }

    private static FixtureEffect Closes(WildlifeFixture fixture, FixtureState state, FixtureFacts facts, bool within, double seconds)
    {
        switch (state.Phase)
        {
            case FixturePhase.Resting:
                if (within && facts.PlayerCanBeHit) state.Enter(FixturePhase.Telling);
                return FixtureEffect.None;
            case FixturePhase.Telling:
                // Running straight through gets out before it shuts.
                if (!within || !facts.PlayerCanBeHit)
                {
                    state.Enter(FixturePhase.Resting);
                    return FixtureEffect.None;
                }

                if (state.Seconds < fixture.TellSeconds) return FixtureEffect.None;
                state.Enter(FixturePhase.Acting);
                (state.SinceHurt, state.EmptySeconds, state.Blows) = (0, 0, 0);
                return FixtureEffect.Close;
            case FixturePhase.Acting:
                if (within && facts.PlayerCanBeHit)
                {
                    state.EmptySeconds = 0;
                    return HurtsAgain(fixture, state, seconds);
                }

                state.EmptySeconds += seconds;
                if (state.EmptySeconds < fixture.RestSeconds) return FixtureEffect.None;
                state.Enter(FixturePhase.Recovering);
                return FixtureEffect.Open;
            default:
                // Open again, it closes again only after a while: the act's seconds.
                if (state.Seconds >= fixture.ActSeconds) state.Enter(FixturePhase.Resting);
                return FixtureEffect.None;
        }
    }

    private static FixtureEffect Hatches(WildlifeFixture fixture, FixtureState state, FixtureFacts facts, bool within, double seconds)
    {
        state.SinceSpent += seconds;
        // Spent vessels regrow together, once the brood's regrowth has passed since the last was spent.
        if (state.Spent > 0 && state.Phase == FixturePhase.Resting && state.SinceSpent >= fixture.Brood!.RegrowSeconds) state.Spent = 0;
        bool roused = (facts.SeesPlayer && within && facts.PlayerCanBeHit) || facts.Struck;
        switch (state.Phase)
        {
            case FixturePhase.Resting:
                if (roused && state.Spent < fixture.Parts) state.Enter(FixturePhase.Telling);
                return FixtureEffect.None;
            case FixturePhase.Telling:
                if (state.Seconds < fixture.TellSeconds) return FixtureEffect.None;
                // A vessel smashed while it swelled leaves nothing to hatch.
                if (state.Spent >= fixture.Parts)
                {
                    state.Enter(FixturePhase.Resting);
                    return FixtureEffect.None;
                }

                state.Enter(FixturePhase.Acting);
                state.Spent++;
                state.SinceSpent = 0;
                return FixtureEffect.Hatch;
            default:
                if (state.Seconds < fixture.ActSeconds) return FixtureEffect.None;
                bool more = facts.SeesPlayer && within && facts.PlayerCanBeHit && state.Spent < fixture.Parts;
                state.Enter(more ? FixturePhase.Telling : FixturePhase.Resting);
                return FixtureEffect.None;
        }
    }

    /// <summary>A hold or a shut bower hurts the player each time its damage's while has passed.</summary>
    private static FixtureEffect HurtsAgain(WildlifeFixture fixture, FixtureState state, double seconds)
    {
        state.SinceHurt += seconds;
        if (state.SinceHurt < fixture.DamageEverySeconds) return FixtureEffect.None;
        // One hurt at a time: a long step banks no more (R10963-2).
        state.SinceHurt = Math.Min(state.SinceHurt - fixture.DamageEverySeconds, fixture.DamageEverySeconds / 2);
        return FixtureEffect.Hurt;
    }

    /// <summary>
    /// A blow lands on a fixture's part: a shut bower's membrane, cut through by its blows, opens it; a vessel's blows smash
    /// its next whole vessel, which then hatches nothing. Any other fixture's blows are its health's.
    /// </summary>
    internal static FixtureEffect Blow(WildlifeFixture fixture, FixtureState state)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(state);
        switch (fixture.Act)
        {
            case FixtureAct.Closes when state.Phase == FixturePhase.Acting:
                if (++state.Blows < fixture.Blows) return FixtureEffect.None;
                state.Enter(FixturePhase.Recovering);
                return FixtureEffect.Open;
            case FixtureAct.Hatches when state.Spent < fixture.Parts:
                if (++state.Blows < fixture.Blows) return FixtureEffect.None;
                state.Blows = 0;
                state.Spent++;
                state.SinceSpent = 0;
                return FixtureEffect.Smash;
            default:
                return FixtureEffect.None;
        }
    }

    /// <summary>
    /// How long a fixture slept before this update (R10963-1): all the while since it was last tended, at
    /// <paramref name="stepsPerSecond"/> steps a second, beyond this update's own <paramref name="seconds"/>; 0 while it
    /// is tended update by update. Records this step as its last.
    /// </summary>
    internal static double Slept(FixtureState state, long step, double seconds, double stepsPerSecond = HunterRules.StepsPerSecond)
    {
        ArgumentNullException.ThrowIfNull(state);
        double since = state.LastStep < 0 ? 0 : (step - state.LastStep) / stepsPerSecond;
        state.LastStep = step;
        double slept = since - seconds;
        return slept > SleptAtLeastSeconds ? slept : 0;
    }

    /// <summary>Less than this between tendings is the ordinary rhythm of updates, not sleep.</summary>
    internal const double SleptAtLeastSeconds = 1.0;

    /// <summary>Sleep is run in steps no longer than this, so each phase it passes through ends when it would have.</summary>
    internal const double SleepStepSeconds = 0.5;

    /// <summary>
    /// A fixture's time asleep (R10963-2), run with nobody there: a hold lets go, a shut bower empties and opens after its
    /// while, a lash comes down on nothing, and a vessel swelling settles without hatching; spent vessels regrow over it.
    /// Nothing it does asleep touches the player. Returns what it did (a release, an opening) for the module to apply,
    /// before the update's own step runs with what the fixture senses now.
    /// </summary>
    internal static FixtureEffect Sleep(WildlifeFixture fixture, FixtureState state, double seconds)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(state);
        FixtureFacts away = new(double.PositiveInfinity, false, false, false, false);
        if (fixture.Act == FixtureAct.Hatches && state.Phase == FixturePhase.Telling) state.Enter(FixturePhase.Resting);
        FixtureEffect done = FixtureEffect.None;
        double left = seconds;
        // Until it has settled to rest, step it through its phases; at rest, only its regrowth is left.
        while (left > 0 && state.Phase != FixturePhase.Resting)
        {
            double step = Math.Min(left, SleepStepSeconds);
            done |= Step(fixture, state, away, step);
            left -= step;
        }

        if (left > 0)
        {
            state.Seconds += left;
            state.SinceSpent += left;
            state.HurtSeconds = 0;
            done |= Step(fixture, state, away, 0);
        }

        return done & (FixtureEffect.Release | FixtureEffect.Open);
    }

    /// <summary>The parts a fixture has lost by its health (a vault filter's lobes, #10963): a part for each share of it gone.</summary>
    internal static int PartsLost(int parts, int health, int maximumHealth)
    {
        if (parts <= 0 || maximumHealth <= 0) return 0;
        int left = (int)Math.Ceiling(Math.Clamp((double)health / maximumHealth, 0, 1) * parts);
        return parts - left;
    }
}
