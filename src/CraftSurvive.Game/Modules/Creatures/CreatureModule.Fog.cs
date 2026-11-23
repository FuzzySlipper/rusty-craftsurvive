using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The fog foes (#10985; den-wiki wandering-years/fog-wildlife, "Roster and rules"): they swim a mist sump's air over the
/// floor and never leave the mist, hunt by what they hear rather than what they see, click their teeth faster as they
/// come, and circle off after each bite before they come again. <see cref="FogRules"/> decides; this applies.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>Whether a point in the session's frame lies in a mist sump's mist, from the dungeon's owner; with none, nowhere does.</summary>
    internal Func<Vector3, bool>? InMist { get; set; }

    /// <summary>
    /// A place is tested for mist this far over its floor: about where a fog foe swims and a player's waist. A sump's mist
    /// lies thin along its floor and fills its passage higher up.
    /// </summary>
    private const float MistTestRiseMetres = 1.0f;

    /// <summary>A fog foe's teeth click from about its head: this far over its lift.</summary>
    private const float ClickOverLiftMetres = 0.3f;

    /// <summary>How loud the player was in the last update, and how many noises fog foes have heard, for the readout.</summary>
    private double lastLoudness;

    private long noisesHeard;

    /// <summary>The last fog foe's circling off after a bite: where to, or why it stayed.</summary>
    private string lastCircle = "none";

    private bool FeetInMist(Vector3 feet) => InMist?.Invoke(feet + (Vector3.UnitY * MistTestRiseMetres)) ?? false;

    private bool FeetInMist(Vector2 ground, float floor) => FeetInMist(new Vector3(ground.X, floor, ground.Y));

    /// <summary>
    /// Before sensing: how loud the player was, and each fog foe in their space within its hearing of it hears it there. A
    /// resting one wakes to it.
    /// </summary>
    private void Listen(Vector3 playerFeet, CreatureSpace? here, double seconds)
    {
        lastLoudness = FogRules.Loudness(player.TakeBodySounds(), player.Sprinting, player.AttackRequested);
        foreach ((int id, Hunter hunter) in hunters)
        {
            if (hunter.Kind.Hunt?.Fog is null) continue;
            hunter.HeardAgoSeconds += seconds;
            if (!roster.TryGet(id, out Creature creature) || !creature.Awake || creature.Space != here) continue;
            if (!FogRules.Hears(lastLoudness, hunter.Kind.Hunt.SightMetres, Vector3.Distance(Feet(creature), playerFeet))) continue;
            hunter.HeardAgoSeconds = 0;
            hunter.HeardAt = playerFeet;
            noisesHeard++;
            if (creature.Behavior.State == CreatureState.Resting && !(overrides.Count > 0 && (Frozen(creature) || ForcedState(creature) is not null)))
            {
                creature.Behavior = creature.Behavior with { State = CreatureState.Alert };
            }
        }
    }

    /// <summary>
    /// What fog foes sense, in place of sight: one senses the player while they are in the mist with it and it heard them
    /// lately or they are within its feel. One that heard them but does not sense them goes to where it heard them.
    /// </summary>
    private void SenseFog(HashSet<int> seeing, Vector3 playerFeet)
    {
        bool playerInMist = FeetInMist(playerFeet);
        foreach ((int id, Hunter hunter) in hunters)
        {
            if (hunter.Kind.Hunt?.Fog is not WildlifeFog fog || !roster.TryGet(id, out Creature creature) || !creature.Awake) continue;
            seeing.Remove(id);
            if (FogRules.Senses(fog, playerInMist, hunter.HeardAgoSeconds, CreatureSimulation.PlanarDistance(creature.Position, playerFeet)))
            {
                seeing.Add(id);
            }
            else if (hunter.HeardAgoSeconds <= fog.HeardSeconds && hunter.HeardAt is Vector3 heard)
            {
                creature.LastSeen = heard;
            }
        }
    }

    /// <summary>
    /// Whether a body may step to these feet: a fog foe never steps out of the mist, and one that finds itself outside it
    /// (set down there, or where the floor rises at a mouth) only swims back toward its home, which is in the mist.
    /// </summary>
    private bool MayStep(Hunter? hunter, Vector2 from, float fromFloor, Vector3 to)
    {
        if (hunter?.Kind.Hunt?.Fog is null || FeetInMist(to)) return true;
        if (FeetInMist(from, fromFloor)) return false;
        Vector2 home = hunter.Record.Home;
        return Vector2.Distance(new Vector2(to.X, to.Z), home) < Vector2.Distance(from, home);
    }

    /// <summary>
    /// A fog foe's own update, after its threat, bite and flinch moved on: it swims at its float, its teeth click while it
    /// hunts, and a bite just finished sends it circling off.
    /// </summary>
    /// <param name="wasBiting">Whether it was biting before this update moved its bite on.</param>
    private void ShowFog(Creature creature, Hunter hunter, Vector3 playerWorld, bool wasBiting, double seconds)
    {
        WildlifeFog fog = hunter.Kind.Hunt!.Fog!;
        WildlifeMember member = hunter.Member;
        member.Lift = FogRules.Float(fog, step / HunterRules.StepsPerSecond, hunter.BobPhase, dead: false, member.Lift, seconds);
        hunter.Circling = errands.TryGetValue(creature.Id, out Errand? errand) && errand.Why == CircleErrand;

        Vector2 player = new(playerWorld.X, playerWorld.Z);
        double distance = CreatureSimulation.PlanarDistance(creature.Position, playerWorld);
        if (creature.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking)
        {
            hunter.ClickInSeconds -= seconds;
            if (hunter.ClickInSeconds <= 0)
            {
                cues.RaiseAt(Cue.NeedleClick, SpaceOf(creature).ToLocal(creature.Position.X, StandingAt(creature) + member.Lift + ClickOverLiftMetres, creature.Position.Y));
                hunter.ClickInSeconds = FogRules.ClickEvery(fog, hunter.Kind.Hunt.SightMetres, distance);
            }
        }
        else
        {
            hunter.ClickInSeconds = 0;
        }

        if (!wasBiting || hunter.BiteSeconds > 0 || hunter.Circling) return;
        float floor = StandingAt(creature);
        if (FogRules.CircleTo(creature.Position, player, fog.CircleMetres, hunter.CircleSide, point => FeetInMist(point, floor)) is Vector2 to)
        {
            Send(creature, to, fog.CircleSeconds, CircleErrand);
            hunter.Circling = true;
            lastCircle = string.Create(CultureInfo.InvariantCulture, $"{creature.Id} at step {step} to {to.X:F1},{to.Y:F1}");
        }
        else
        {
            lastCircle = string.Create(CultureInfo.InvariantCulture, $"{creature.Id} at step {step} stayed: no place {fog.CircleMetres:F1} m off in the mist");
        }
    }

    /// <summary>The errand a fog foe circles off on.</summary>
    private const string CircleErrand = "circle";

    /// <summary>A fog foe's part of the readout: how loud the player was, how many noises were heard, and each foe's hearing.</summary>
    private string FogReadout() => hunters.Values.Any(hunter => hunter.Floats)
        ? string.Create(CultureInfo.InvariantCulture,
            $" fog: loudness={lastLoudness:F1} heard={noisesHeard} circle={lastCircle} [{string.Join(" ", hunters.Where(pair => pair.Value.Floats).Select(pair => string.Create(CultureInfo.InvariantCulture, $"{pair.Key}:ago={Math.Min(pair.Value.HeardAgoSeconds, 999):F1},lift={pair.Value.Member.Lift:F2},circling={pair.Value.Circling}")))}]")
        : "";
}
