using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// A hostile animal (#10615): wildlife's world places it and draws it, and the creature rules hunt with
/// it. This is what it shows of itself: the animal wildlife drew for it, the herd record that counts it,
/// and the threat, bite and flinch it is part way through.
/// </summary>
internal sealed class Hunter(WildlifeKind kind, HerdRecord record, WildlifeMember member)
{
    internal WildlifeKind Kind { get; } = kind ?? throw new ArgumentNullException(nameof(kind));

    internal HerdRecord Record { get; } = record ?? throw new ArgumentNullException(nameof(record));

    internal WildlifeMember Member { get; } = member ?? throw new ArgumentNullException(nameof(member));

    /// <summary>How much of its threat is left, and whether it has shown one since it last lost interest.</summary>
    internal double ThreatSeconds { get; set; }

    internal bool Threatened { get; set; }

    /// <summary>How much of its bite and of its flinch is left.</summary>
    internal double BiteSeconds { get; set; }

    internal double HurtSeconds { get; set; }

    /// <summary>The blow its bite carries, and how long until the jaws close on it; null once they have.</summary>
    internal AttackProfile? Blow { get; set; }

    internal double BlowInSeconds { get; set; }

    /// <summary>Where it stood when last read, to tell how fast it is going.</summary>
    internal Vector2 LastPosition { get; set; } = member.Position;

    /// <summary>It stands to show its threat or bite: it does not walk meanwhile.</summary>
    internal bool Holding => ThreatSeconds > 0 || BiteSeconds > 0;
}

/// <summary>A hostile animal as wildlife's bodies draw it this update.</summary>
internal readonly record struct WildlifeHunter(WildlifeKind Kind, WildlifeMember Member, float Feet);

/// <summary>What a hostile animal does with its body, as Engine-free rules: the presentation applies these and decides nothing.</summary>
internal static class HunterRules
{
    /// <summary>Slower than this over the ground is standing.</summary>
    private const float StandingSpeed = 0.05f;

    /// <summary>A creature kind from its content: it hunts by the creature rules, with the numbers its kind gives.</summary>
    internal static CreatureKind KindOf(WildlifeKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        WildlifeHunt hunt = kind.Hunt ?? throw new InvalidDataException($"{kind.Id} does not hunt");
        return new CreatureKind(
            kind.Id,
            BehaviorTuning.Hostile with { SightRange = hunt.SightMetres, AttackRange = hunt.ReachMetres, AttackCooldownTicks = hunt.CooldownTicks },
            kind.MaximumHealth,
            kind.Defence,
            kind.Speeds.Flee,
            hunt.HaltMetres,
            new AttackProfile(hunt.Accuracy, hunt.Power, DamageType.Piercing),
            kind.ExperienceAward,
            kind.Loot);
    }

    /// <summary>The bite begins: the blow lands when the jaws close, so a player who steps away in time is not there.</summary>
    internal static void Bite(Hunter hunter, AttackProfile attack, double clipSeconds)
    {
        ArgumentNullException.ThrowIfNull(hunter);
        hunter.Blow = attack;
        hunter.BlowInSeconds = hunter.Kind.Hunt!.BiteWindupSeconds;
        hunter.BiteSeconds = clipSeconds;
        hunter.ThreatSeconds = 0;
    }

    /// <summary>Moves its threat, bite and flinch on by some seconds. Returns the blow if the jaws close now.</summary>
    /// <param name="state">What the creature rules have it doing.</param>
    /// <param name="threatSeconds">How long its threat clip runs.</param>
    internal static AttackProfile? Advance(Hunter hunter, CreatureState state, double playerDistance, double threatSeconds, double seconds)
    {
        ArgumentNullException.ThrowIfNull(hunter);
        hunter.ThreatSeconds = Math.Max(0, hunter.ThreatSeconds - seconds);
        hunter.BiteSeconds = Math.Max(0, hunter.BiteSeconds - seconds);
        hunter.HurtSeconds = Math.Max(0, hunter.HurtSeconds - seconds);
        if (state == CreatureState.Idle) hunter.Threatened = false;
        // It shows its threat once a hunt, from where it first saw the player, unless they are already in reach.
        bool hunting = state is CreatureState.Pursuing or CreatureState.Alert;
        if (hunting && !hunter.Threatened)
        {
            hunter.Threatened = true;
            if (playerDistance > hunter.Kind.Hunt!.ReachMetres) hunter.ThreatSeconds = threatSeconds;
        }

        if (hunter.Blow is not AttackProfile blow) return null;
        hunter.BlowInSeconds -= seconds;
        if (hunter.BlowInSeconds > 0) return null;
        hunter.Blow = null;
        return blow;
    }

    /// <summary>Whether a closing bite finds the player: it still sees them, so no wall has come between, and they are within its reach and a step more.</summary>
    internal static bool Lands(WildlifeHunt hunt, double playerDistance, bool seesPlayer) => seesPlayer && playerDistance <= hunt.ReachMetres + hunt.DodgeMetres;

    /// <summary>
    /// Where its mesh stands for a capsule at a point. The mesh's origin is under its bounding box, well
    /// forward of its legs on so long a head; the capsule holds the legs, so the mesh stands ahead of it
    /// by as much as the middle of its feet lies behind the origin.
    /// </summary>
    /// <summary>
    /// The body creatures are routed for, once another has joined them: as wide as the widest and as
    /// tall as the tallest, whichever came first, on cells at least the width of it (#10622).
    /// </summary>
    internal static (float Radius, float Height, double Cell) Envelope(float radius, float height, float otherRadius, float otherHeight, double smallestCell)
    {
        float widest = Math.Max(radius, otherRadius);
        return (widest, Math.Max(height, otherHeight), Math.Max(smallestCell, Math.Ceiling(2 * widest)));
    }

    internal static Vector2 Drawn(Vector2 capsule, float yaw, double hindMetres, double foreMetres, float size) =>
        capsule + (WildlifeRules.Facing(yaw) * (float)(-(hindMetres + foreMetres) / 2 * size));

    /// <summary>The clip its state calls for. Being hit shows over anything; a bite or threat runs its length; then it goes by its speed.</summary>
    /// <param name="speed">Its speed over the ground, in metres a second.</param>
    /// <param name="runsAbove">The speed where its gait changes to a run.</param>
    internal static WildlifeClipRole Want(Hunter hunter, CreatureState state, float speed, double runsAbove)
    {
        ArgumentNullException.ThrowIfNull(hunter);
        if (hunter.HurtSeconds > 0) return WildlifeClipRole.Hurt;
        if (hunter.BiteSeconds > 0) return WildlifeClipRole.Bite;
        if (hunter.ThreatSeconds > 0) return WildlifeClipRole.Threat;
        if (speed > StandingSpeed) return speed > runsAbove ? WildlifeClipRole.Run : WildlifeClipRole.Walk;
        return state is CreatureState.Attacking or CreatureState.Pursuing or CreatureState.Alert ? WildlifeClipRole.Look : WildlifeClipRole.Idle;
    }

    /// <summary>Its facing after some seconds: where it is going, or at the player when it stands to them, turning no faster than its kind can.</summary>
    internal static float Turned(float yaw, Vector2 heading, double turnDegreesPerSecond, double seconds)
    {
        if (heading.LengthSquared() < 1e-6f) return yaw;
        float wanted = MathF.Atan2(heading.X, -heading.Y);
        float apart = MathF.IEEERemainder(wanted - yaw, 2 * MathF.PI);
        float most = (float)(turnDegreesPerSecond * Math.PI / 180 * seconds);
        return yaw + Math.Clamp(apart, -most, most);
    }
}
