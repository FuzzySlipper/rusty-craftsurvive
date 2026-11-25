using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The ruin fixtures (#10963), as settled with Patch on the wiki's Ruins wildlife page ("Fixtures as hazards"): living
/// infrastructure rooted where it grows, acting only on a player within its reach. This module owns their states, their
/// holds on the player, the walls of shut bowers and the vessels' broods; <see cref="FixtureRules"/> decides.
/// </summary>
internal sealed partial class CreatureModule
{
    private readonly Dictionary<int, FixtureState> fixtures = [];

    /// <summary>Shut bowers, by creature id: their walls stand as colliders.</summary>
    private readonly HashSet<int> shutBowers = [];

    /// <summary>Vessels that split this update: their broods spill once the creatures have moved, as a bearer's do.</summary>
    private readonly List<int> hatchesDue = [];

    /// <summary>The last thing a fixture did, for the readout.</summary>
    private string lastFixture = "none";

    /// <summary>A seized player is drawn toward a point this far in front of the fixture's middle, at its feet.</summary>
    private const float DrawToMetres = 0.9f;

    /// <summary>
    /// A shut bower's wall: so many square blocks round it, each this wide and this tall, standing square to the axes as a
    /// seal does, close enough that no body slips between them.
    /// </summary>
    private const int WallBlocks = 28;

    private const float WallBlockMetres = 0.7f, WallHeightMetres = 3.2f;

    /// <summary>Whether a creature is a rooted fixture.</summary>
    private bool Rooted(Creature creature, out Hunter fixture)
    {
        if (hunters.TryGetValue(creature.Id, out Hunter? hunter) && hunter.Kind.Hunt!.Fixture is not null)
        {
            fixture = hunter;
            return true;
        }

        fixture = null!;
        return false;
    }

    /// <summary>
    /// One step of a fixture (#10963), in place of the simulation's: it never moves, and acts by its rules on the player
    /// within its reach, on its own floor.
    /// </summary>
    private void TendFixture(Creature creature, Hunter hunter, HashSet<int> seeing, bool playerCanBeHit, double seconds)
    {
        WildlifeFixture fixture = hunter.Kind.Hunt!.Fixture!;
        WildlifeMember member = hunter.Member;
        Vector3 feet = player.WorldFeetPosition;
        if (!fixtures.TryGetValue(creature.Id, out FixtureState? state))
        {
            state = fixtures[creature.Id] = new FixtureState();
            // Placed by hand it faces the player; #10955's placement faces it out of its recess.
            Vector2 toPlayer = new Vector2(feet.X, feet.Z) - creature.Position;
            if (toPlayer.LengthSquared() > 1e-6f) member.Yaw = MathF.Atan2(toPlayer.X, -toPlayer.Y);
            if (fixture.CeilingMetres > 0) member.Lift = (CeilingOver(creature) ?? (float)fixture.CeilingMetres) - (float)fixture.CeilingMetres;
        }

        bool sameFloor = Math.Abs(feet.Y - StandingAt(creature)) <= CreatureSimulation.MeleeHeightMetres;
        double metres = sameFloor ? CreatureSimulation.PlanarDistance(creature.Position, feet) : double.PositiveInfinity;
        bool holds = player.Hazards.SeizedBy == creature.Id;
        bool brokeFree = state.Phase == FixturePhase.Acting && fixture.Act == FixtureAct.Seizes && !holds && player.Hazards.TakeBrokeFree();
        bool struck = state.StruckPending;
        state.StruckPending = false;
        FixtureFacts facts = new(metres, seeing.Contains(creature.Id), playerCanBeHit, struck, brokeFree);
        state.LastFacts = facts;
        FixtureEffect effect = FixtureRules.Step(fixture, state, facts, seconds);
        Apply(creature, hunter, fixture, effect, feet);
        creature.Behavior = creature.Behavior with { State = state.Phase == FixturePhase.Resting ? CreatureState.Idle : CreatureState.Alert };
        member.Velocity = Vector2.Zero;
        member.Position = creature.Position;
        member.Lost = fixture.Act == FixtureAct.Numbs
            ? [.. fixture.PartJoints.Take(FixtureRules.PartsLost(fixture.Parts, creature.Combat.Health, creature.Combat.MaximumHealth))]
            : [];
        Pose(hunter, fixture, state);
    }

    /// <summary>What a fixture's act does to the player and the ruin.</summary>
    private void Apply(Creature creature, Hunter hunter, WildlifeFixture fixture, FixtureEffect effect, Vector3 feet)
    {
        if (effect == FixtureEffect.None) return;
        string id = string.Create(CultureInfo.InvariantCulture, $"{hunter.Kind.Id} {creature.Id}");
        if (effect.HasFlag(FixtureEffect.Seize))
        {
            Vector2 front = creature.Position + (new Vector2(MathF.Sin(hunter.Member.Yaw), -MathF.Cos(hunter.Member.Yaw)) * DrawToMetres);
            player.Hazards.Seize(creature.Id, new Vector3(front.X, feet.Y, front.Y), (float)fixture.DrawMetresPerSecond, fixture.StruggleSeconds, (float)fixture.StruggleStaminaPerSecond);
            cues.Raise(Cue.Strike);
            Fixtured($"{id} seized the player");
        }

        if (effect.HasFlag(FixtureEffect.Release))
        {
            if (player.Hazards.SeizedBy == creature.Id) player.Hazards.Release();
            Fixtured($"{id} let go");
        }

        if (effect.HasFlag(FixtureEffect.Hurt)) Hurt(id, fixture.Damage, "holds");
        if (effect.HasFlag(FixtureEffect.Sting))
        {
            Hurt(id, fixture.Damage, "stung");
            player.Hazards.Numbs(fixture.NumbSeconds, player.Stamina);
            Fixtured(string.Create(CultureInfo.InvariantCulture, $"{id} stung the player: numb {fixture.NumbSeconds:F0}s"));
        }

        if (effect.HasFlag(FixtureEffect.Close))
        {
            shutBowers.Add(creature.Id);
            sealsChanged = true;
            Fixtured($"{id} shut");
        }

        if (effect.HasFlag(FixtureEffect.Open))
        {
            shutBowers.Remove(creature.Id);
            sealsChanged = true;
            Fixtured($"{id} opened");
        }

        if (effect.HasFlag(FixtureEffect.Hatch)) hatchesDue.Add(creature.Id);
        if (effect.HasFlag(FixtureEffect.Smash)) Fixtured($"{id}: a vessel smashed");
    }

    private void Hurt(string id, int damage, string how)
    {
        PlayerDefeatState after = player.Vitals.TakeHit(damage, step);
        lastEvent = string.Create(CultureInfo.InvariantCulture, $"{id} {how} the player for {damage}; health {after.Health}/{after.MaximumHealth}");
    }

    private void Fixtured(string what)
    {
        lastFixture = string.Create(CultureInfo.InvariantCulture, $"step {step}: {what}");
        lastEvent = what;
    }

    /// <summary>After the creatures have moved: each vessel that split this update spills its brood.</summary>
    private void HatchDue(Vector3 playerFeet)
    {
        foreach (int id in hatchesDue)
        {
            if (roster.TryGet(id, out Creature vessel) && Rooted(vessel, out Hunter hunter)) Hatch(vessel, hunter, hunter.Kind.Hunt!.Fixture!, playerFeet);
        }

        hatchesDue.Clear();
    }

    /// <summary>A vessel splits (#10963): one animal of its brood spills out toward the player, hunting, guarding the vessel.</summary>
    private void Hatch(Creature vessel, Hunter hunter, WildlifeFixture fixture, Vector3 playerFeet)
    {
        WildlifeBrood brood = fixture.Brood!;
        WildlifeKind? kind = Wildlife?.HunterKinds.FirstOrDefault(candidate => candidate.Id == brood.Kind);
        if (kind is null)
        {
            Fixtured($"brood vessel {vessel.Id}: no hunter kind '{brood.Kind}' for its brood");
            return;
        }

        int released = Spill(vessel, kind, brood.Count, playerFeet);
        Fixtured(string.Create(CultureInfo.InvariantCulture, $"{hunter.Kind.Id} {vessel.Id} split a vessel: {released} {kind.Id} out"));
    }

    /// <summary>The clip a fixture shows for its phase: its tell, its act, its hold or recovery, a vessel's spent count.</summary>
    private void Pose(Hunter hunter, WildlifeFixture fixture, FixtureState state)
    {
        WildlifeMember member = hunter.Member;
        WildlifeRig rig = Wildlife!.RigOf(hunter.Kind);
        string? Named(string key, int count = 0) => fixture.Clips.TryGetValue(key, out string? name)
            ? string.Format(CultureInfo.InvariantCulture, name, count)
            : null;
        double Length(string? name) => name is not null && rig.Rig.Clips.TryGetValue(name, out CreatureClip? clip) ? clip.Seconds : 0;
        member.Clip = state.HurtSeconds > 0 && state.Phase == FixturePhase.Resting ? WildlifeClipRole.Hurt : WildlifeClipRole.Idle;
        member.NamedClip = null;
        if (fixture.Act == FixtureAct.Hatches)
        {
            member.NamedClip = state.Phase switch
            {
                FixturePhase.Telling => Named("swell", state.Spent + 1),
                FixturePhase.Acting when state.Seconds < Length(Named("split", state.Spent)) => Named("split", state.Spent),
                _ => state.Spent == 0 ? Named("whole") : Named("spent", state.Spent),
            };
            return;
        }

        double bite = rig.Clip(WildlifeClipRole.Bite).Seconds;
        switch (state.Phase)
        {
            case FixturePhase.Telling:
                member.Clip = WildlifeClipRole.Threat;
                break;
            case FixturePhase.Acting:
                member.Clip = WildlifeClipRole.Bite;
                if (state.Seconds >= bite && Named("hold") is string hold) member.NamedClip = hold;
                break;
            case FixturePhase.Recovering when state.Seconds < Length(Named("recover")):
                member.NamedClip = Named("recover");
                break;
        }
    }

    /// <summary>A blow landed on a fixture (#10963): it may cut a hold short, cut a shut bower open, or smash a vessel.</summary>
    private void FixtureStruck(Creature creature, Hunter hunter)
    {
        if (!fixtures.TryGetValue(creature.Id, out FixtureState? state)) return;
        WildlifeFixture fixture = hunter.Kind.Hunt!.Fixture!;
        state.StruckPending = true;
        state.HurtSeconds = ClipSeconds(hunter, WildlifeClipRole.Hurt);
        Apply(creature, hunter, fixture, FixtureRules.Blow(fixture, state), player.WorldFeetPosition);
    }

    /// <summary>
    /// The swing of a player inside a shut bower (#10963) lands on its membrane, which its blows cut through, opening it.
    /// Null when the player is in no shut bower.
    /// </summary>
    private string? SwingAtBower()
    {
        if (shutBowers.Count == 0) return null;
        Vector3 feet = player.WorldFeetPosition;
        foreach (int id in shutBowers)
        {
            if (!roster.TryGet(id, out Creature bower) || !Rooted(bower, out Hunter hunter) || !fixtures.TryGetValue(id, out FixtureState? state)) continue;
            WildlifeFixture fixture = hunter.Kind.Hunt!.Fixture!;
            if (CreatureSimulation.PlanarDistance(bower.Position, feet) > fixture.WallMetres) continue;
            FixtureEffect effect = FixtureRules.Blow(fixture, state);
            Apply(bower, hunter, fixture, effect, feet);
            cues.RaiseAt(effect.HasFlag(FixtureEffect.Open) ? Cue.Defeat : Cue.Strike, SpaceOf(bower).ToLocal(feet + new Vector3(0, StruckHeightMetres, 0)));
            return effect.HasFlag(FixtureEffect.Open)
                ? string.Create(CultureInfo.InvariantCulture, $"cut through bower {id}'s membrane: it opened")
                : string.Create(CultureInfo.InvariantCulture, $"struck bower {id}'s membrane: {state.Blows}/{fixture.Blows}");
        }

        return null;
    }

    /// <summary>
    /// A fixture gone (#10963) holds nothing: a seizer that died, was forgotten or slept lets the player go, and a bower
    /// that did leaves no wall.
    /// </summary>
    private void DropGoneFixtures()
    {
        foreach (int id in fixtures.Keys.Where(id => !roster.TryGet(id, out Creature creature) || !creature.Awake || !hunters.ContainsKey(id)).ToArray())
        {
            fixtures.Remove(id);
            if (player.Hazards.SeizedBy == id) player.Hazards.Release();
            if (shutBowers.Remove(id)) sealsChanged = true;
        }
    }

    /// <summary>The walls shut bowers stand as (#10963): a ring of blocks round each, in its session.</summary>
    private IEnumerable<CharacterObstacle> BowerWalls(SpatialSession session)
    {
        Vector3 half = new(WallBlockMetres / 2, 0, WallBlockMetres / 2);
        foreach (int id in shutBowers)
        {
            if (!roster.TryGet(id, out Creature bower) || !Rooted(bower, out Hunter hunter)) continue;
            CreatureSpace space = SpaceOf(bower);
            if (!space.Session.Equals(session)) continue;
            float ring = (float)hunter.Kind.Hunt!.Fixture!.WallMetres;
            float floor = StandingAt(bower);
            for (int k = 0; k < WallBlocks; k++)
            {
                float angle = 2 * MathF.PI * k / WallBlocks;
                Vector3 at = new(bower.Position.X + (MathF.Sin(angle) * ring), floor, bower.Position.Y + (MathF.Cos(angle) * ring));
                Transform pose = new(space.ToLocal(at), Quaternion.Identity, Vector3.One);
                yield return new CharacterObstacle(ProductIds.FixtureObstacleBase + ((ulong)(id % (int)(ProductIds.FixtureObstacleLimit / WallBlocks)) * WallBlocks) + (ulong)k,
                    pose, -half, half + new Vector3(0, WallHeightMetres, 0), CollisionEnabled: true, Vector3.Zero, Vector3.Zero);
            }
        }
    }

    /// <summary>The fixtures' readout (#10963): each one's phase, and the player's hold and numbing.</summary>
    internal string FixturesReadout() => string.Create(CultureInfo.InvariantCulture,
        $"fixtures=[{string.Join(" | ", fixtures.Select(f => FixtureLine(f.Key, f.Value)))}] shut=[{string.Join(" ", shutBowers)}] player=[{player.Hazards.Readout()} stamina={player.Stamina.Current:F0}/{player.Stamina.Maximum}] last=[{lastFixture}]");

    private string FixtureLine(int id, FixtureState state)
    {
        if (!roster.TryGet(id, out Creature creature) || !hunters.TryGetValue(id, out Hunter? hunter)) return $"{id} gone";
        double metres = CreatureSimulation.PlanarDistance(creature.Position, player.WorldFeetPosition);
        return string.Create(CultureInfo.InvariantCulture,
            $"{id} {hunter.Kind.Id} {state.Phase} {state.Seconds:F1}s player={metres:F1}m sensed={state.LastFacts.PlayerMetres:F1}m hittable={state.LastFacts.PlayerCanBeHit} sees={state.LastFacts.SeesPlayer} health={creature.Combat.Health}/{creature.Combat.MaximumHealth} blows={state.Blows} spent={state.Spent} lost={hunter.Member.Lost.Count} clip={hunter.Member.NamedClip ?? hunter.Member.Clip.ToString()} lift={hunter.Member.Lift:F2}");
    }

    private void ClearFixtures()
    {
        fixtures.Clear();
        hatchesDue.Clear();
        shutBowers.Clear();
        lastFixture = "none";
    }
}
