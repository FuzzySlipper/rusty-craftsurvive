using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Feedback;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The ruin tenders (#10961), as settled with Patch on the wiki's Ruins wildlife page ("Tenders' indirect threats"): a brood
/// bearer releases its brood of small skitterers and shuffles off; a mender seals the doorway nearest the player. Neither
/// strikes a blow of its own. This module owns their errands, the broods' guard and the seals.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>Somewhere a creature goes by its own rules, walked as a search for that point (a tender's errand, #10961).</summary>
    /// <param name="Stays">It stays there until the errand is ended, rather than ending it on arrival (a mender at its seal).</param>
    private sealed record Errand(Vector3 To, long Until, string Why, bool Stays = false);

    private readonly Dictionary<int, Errand> errands = [];

    /// <summary>Bearers whose threat has just shown: their pods split after this update's creatures have moved.</summary>
    private readonly List<int> broodsDue = [];

    /// <summary>Each brood animal's bearer, by creature id: it guards that one.</summary>
    private readonly Dictionary<int, int> broodOf = [];

    /// <summary>When each bearer's home last split its pods, in update steps: they regrow from then.</summary>
    private readonly Dictionary<string, long> podsSplit = [];

    private readonly List<DoorSeal> seals = [];

    /// <summary>When each mender last finished a seal, in update steps: it refills from then.</summary>
    private readonly Dictionary<int, long> lastSealed = [];

    private int nextSeal;
    private bool sealsChanged;

    /// <summary>
    /// A chime grazer's ring under way (#10962): the update step it began, and whether its chime has sounded yet (once the
    /// crest is up).
    /// </summary>
    private sealed record Ring(long Began, bool Chimed);

    /// <summary>Each ringing grazer's ring, by creature id.</summary>
    private readonly Dictionary<int, Ring> rings = [];

    /// <summary>When each grazer's home last rang or had its ring cut, in update steps: it rings again from then.</summary>
    private readonly Dictionary<string, long> lastRang = [];

    /// <summary>The last call: how many answered it, for the readout.</summary>
    private string lastCall = "none";

    /// <summary>A ring's chime sounds once its crest has swung up: this far into the ring (the clip's rise, 0.25 of 3 s).</summary>
    private const double ChimeAfterSeconds = 0.75;

    /// <summary>The chime sounds from the crest, this high over the grazer's feet.</summary>
    private const float ChimeHeightMetres = 1.6f;

    /// <summary>An errand is reached within this far.</summary>
    private const float ErrandArriveMetres = 0.8f;

    /// <summary>How far a tender shuffles away from the player at a time, and how long it tries.</summary>
    private const float RetreatMetres = 6f;

    private const double RetreatSeconds = 6;

    /// <summary>How long a mender tries to reach a doorway before giving it up.</summary>
    private const double ToDoorSeconds = 30;

    /// <summary>A mender seals doorways within this far of the player, and counts the seals within this far against its most.</summary>
    private const float SealNearPlayerMetres = 16f;

    /// <summary>A brood animal sent back to its bearer goes this near it.</summary>
    private const float BroodHomeMetres = 2.5f;

    /// <summary>A bearer's brood spills out this far toward the player from its middle, clear of its own body.</summary>
    private const float BroodSpillMetres = 1.6f;

    /// <summary>How far past a seal's face a swing still finds it, beyond the reach that finds a creature.</summary>
    private const double SealSwingSlackMetres = 0.4;

    /// <summary>The state an errand holds its creature in: it walks to the errand's point as a searcher walks to where it last saw the player.</summary>
    private CreatureState? ErrandState(Creature creature) => errands.ContainsKey(creature.Id) ? CreatureState.Searching : null;

    /// <summary>Before routing: each creature on an errand searches its way to the errand's point; one there or out of time ends it.</summary>
    private void SteerErrands(Vector3 playerFeet)
    {
        foreach ((int id, Errand errand) in errands.ToArray())
        {
            if (!roster.TryGet(id, out Creature creature))
            {
                errands.Remove(id);
                continue;
            }

            bool there = Vector2.Distance(creature.Position, new Vector2(errand.To.X, errand.To.Z)) <= ErrandArriveMetres;
            if (step > errand.Until || (there && !errand.Stays))
            {
                EndErrand(creature, playerFeet);
                continue;
            }

            creature.LastSeen = errand.To;
            creature.Behavior = creature.Behavior with { State = CreatureState.Searching, SearchUntilTick = long.MaxValue };
        }
    }

    /// <summary>An errand ends: the creature takes up its own rules again, knowing where the player is.</summary>
    private void EndErrand(Creature creature, Vector3 playerFeet)
    {
        errands.Remove(creature.Id);
        creature.Behavior = creature.Behavior with { State = CreatureState.Alert, SearchUntilTick = -1 };
        creature.LastSeen = playerFeet;
    }

    private void Send(Creature creature, Vector2 to, double seconds, string why, bool stays = false) =>
        errands[creature.Id] = new Errand(new Vector3(to.X, StandingAt(creature), to.Y), step + (long)Math.Ceiling(seconds * HunterRules.StepsPerSecond), why, stays);

    /// <summary>
    /// After the creatures have moved: broods spill, pods regrow, bearers shuffle off, broods guard their bearers, menders go
    /// to their doorways and grow their seals, seals age. A change to the seals shuts or opens their doorways' navigation.
    /// </summary>
    private void Tend(double seconds, Vector3 playerFeet, CreatureSpace? here)
    {
        DropOrphans();
        DropGoneFixtures();
        HatchDue(playerFeet);
        foreach (int id in broodsDue)
        {
            if (roster.TryGet(id, out Creature bearer) && hunters.TryGetValue(id, out Hunter? hunter)) ReleaseBrood(bearer, hunter, playerFeet);
        }

        broodsDue.Clear();
        Vector2 player = new(playerFeet.X, playerFeet.Z);
        foreach (Creature creature in roster.All.ToArray())
        {
            if (!creature.Awake || !hunters.TryGetValue(creature.Id, out Hunter? hunter)) continue;
            if (hunter.Kind.Hunt!.Brood is WildlifeBrood brood) TendBearer(creature, hunter, brood, player, playerFeet);
            if (hunter.Kind.Hunt.Seals is WildlifeSealing sealing) TendMender(creature, hunter, sealing, playerFeet, here, seconds);
            if (hunter.Kind.Hunt.Calls is WildlifeCalling calling) TendGrazer(creature, hunter, calling, player);
        }

        GuardBroods(player, playerFeet);
        foreach (DoorSeal seal in seals)
        {
            if (seal.Whole) seal.AgeSeconds += seconds;
        }

        if (!sealsChanged) return;
        sealsChanged = false;
        foreach (CreatureNavigation navigation in navigations.Values) navigation.Reclose();
    }

    /// <summary>
    /// A mender that has died, been forgotten or fallen asleep (#10961) leaves no unfinished seal and holds no doorway: its
    /// half-grown membrane goes and the doorway is free for another. A whole seal stands without it. A grazer gone mid-ring
    /// (#10962) leaves no ring.
    /// </summary>
    private void DropOrphans()
    {
        bool Tending(int mender) => roster.TryGet(mender, out Creature creature) && creature.Awake && hunters.ContainsKey(mender);
        int dropped = seals.RemoveAll(seal => !seal.Whole && !Tending(seal.Mender));
        if (dropped > 0) lastEvent = $"{dropped} unfinished seal(s) dropped: their mender is gone";
        foreach (int mender in pendingDoors.Keys.Where(mender => !Tending(mender)).ToArray()) pendingDoors.Remove(mender);
        // A grazer gone mid-ring (#10962) calls nobody.
        foreach (int grazer in rings.Keys.Where(grazer => !Tending(grazer)).ToArray()) rings.Remove(grazer);
    }

    /// <summary>A bearer's pods regrow after its kind's while; once split, it shuffles away from a player it sees.</summary>
    private void TendBearer(Creature creature, Hunter hunter, WildlifeBrood brood, Vector2 player, Vector3 playerFeet)
    {
        if (hunter.Member.WornLook != brood.SplitLook) return;
        if (!podsSplit.TryGetValue(hunter.Record.Key, out long split) || (step - split) / HunterRules.StepsPerSecond >= brood.RegrowSeconds)
        {
            hunter.Member.WornLook = null;
            podsSplit.Remove(hunter.Record.Key);
            return;
        }

        bool seen = Vector2.Distance(creature.Position, player) <= hunter.Kind.Hunt!.SightMetres;
        if (seen && !errands.ContainsKey(creature.Id)) Send(creature, TenderRules.AwayFrom(creature.Position, player, RetreatMetres), RetreatSeconds, "retreat");
    }

    /// <summary>
    /// A chime grazer (#10962). Disturbed (hunting the player), it rings: it stands for its display's while showing its ring,
    /// the chime sounding once its crest is up, and a ring that runs its length calls the ruin hunters about it. Then, or
    /// while it is resting from its last ring, it walks away from a player it sees.
    /// </summary>
    private void TendGrazer(Creature creature, Hunter hunter, WildlifeCalling calling, Vector2 player)
    {
        if (rings.TryGetValue(creature.Id, out Ring? ring))
        {
            if (!ring.Chimed && (step - ring.Began) / HunterRules.StepsPerSecond >= ChimeAfterSeconds)
            {
                cues.RaiseAt(Cue.Chime, SpaceOf(creature).ToLocal(creature.Position.X, StandingAt(creature) + ChimeHeightMetres, creature.Position.Y));
                rings[creature.Id] = ring with { Chimed = true };
            }

            if (hunter.ThreatSeconds > 0) return;
            rings.Remove(creature.Id);
            lastRang[hunter.Record.Key] = step;
            Call(creature, calling);
            Send(creature, TenderRules.AwayFrom(creature.Position, player, RetreatMetres), RetreatSeconds, "retreat");
            return;
        }

        bool hunting = creature.Behavior.State is CreatureState.Alert or CreatureState.Pursuing or CreatureState.Attacking;
        if (!hunting || errands.ContainsKey(creature.Id)) return;
        if (TenderRules.RingsAgain(calling, SinceRang(hunter)))
        {
            StartRing(creature, hunter);
            return;
        }

        // Resting from its last ring: no ring now, and it keeps away.
        hunter.ThreatSeconds = 0;
        Send(creature, TenderRules.AwayFrom(creature.Position, player, RetreatMetres), RetreatSeconds, "retreat");
    }

    /// <summary>How long since a grazer's home last rang or had its ring cut; null if it never has.</summary>
    private double? SinceRang(Hunter hunter) => lastRang.TryGetValue(hunter.Record.Key, out long rang) ? (step - rang) / HunterRules.StepsPerSecond : null;

    /// <summary>A grazer starts to ring: it stands showing its display, its first threat of a hunt or a fresh one.</summary>
    private void StartRing(Creature creature, Hunter hunter)
    {
        errands.Remove(creature.Id);
        if (hunter.ThreatSeconds <= 0) hunter.ThreatSeconds = hunter.Kind.Hunt!.Display!.Seconds;
        hunter.Threatened = true;
        rings[creature.Id] = new Ring(step, false);
    }

    /// <summary>
    /// A ring has run its length (#10962): every other ruin hunter of the grazer's space within its call's reach, tenders
    /// excepted, searches its way to where it rang, waking if it rested; one already after the player keeps at it.
    /// </summary>
    private void Call(Creature grazer, WildlifeCalling calling)
    {
        CreatureSpace space = SpaceOf(grazer);
        Vector3 at = new(grazer.Position.X, StandingAt(grazer), grazer.Position.Y);
        int answered = 0, heard = 0;
        foreach (Creature other in roster.All)
        {
            if (other.Id == grazer.Id || SpaceOf(other) != space || !hunters.TryGetValue(other.Id, out Hunter? answering) || !TenderRules.Answers(answering.Kind)) continue;
            float metres = Vector2.Distance(other.Position, grazer.Position);
            if (metres > calling.ReachMetres) continue;
            heard++;
            if (other.Behavior.State is CreatureState.Pursuing or CreatureState.Attacking) continue;
            double seconds = TenderRules.AnswerSeconds(metres, answering.Kind.Speeds.Walk, answering.Kind.Hunt!.Search?.Seconds ?? 0);
            other.LastSeen = at;
            other.Behavior = other.Behavior with { State = CreatureState.Searching, SearchUntilTick = step + (long)Math.Ceiling(seconds * HunterRules.StepsPerSecond) };
            answered++;
        }

        lastCall = string.Create(CultureInfo.InvariantCulture, $"grazer {grazer.Id} at step {step}: {answered} answered of {heard} in reach");
        lastEvent = $"chime grazer {grazer.Id} rang: {answered} answered";
    }

    /// <summary>
    /// A bearer's threat has shown (#10961): its pods split and its brood spills out toward the player, already hunting as a
    /// pack, each guarding it.
    /// </summary>
    private void ReleaseBrood(Creature bearer, Hunter hunter, Vector3 playerFeet)
    {
        WildlifeBrood brood = hunter.Kind.Hunt!.Brood!;
        if (hunter.Member.WornLook == brood.SplitLook || Wildlife is null) return;
        hunter.Member.WornLook = brood.SplitLook;
        podsSplit[hunter.Record.Key] = step;
        WildlifeKind? kind = Wildlife.HunterKinds.FirstOrDefault(candidate => candidate.Id == brood.Kind);
        if (kind is null)
        {
            lastEvent = $"brood bearer {bearer.Id}: no hunter kind '{brood.Kind}' for its brood";
            return;
        }

        int released = Spill(bearer, kind, brood.Count, playerFeet);
        lastEvent = string.Create(CultureInfo.InvariantCulture, $"brood bearer {bearer.Id} split its pods: {released} of {brood.Count} {kind.Id} out");
    }

    /// <summary>
    /// A brood spills out of its parent toward the player (#10961, and a brood vessel's, #10963): <paramref name="count"/>
    /// animals of <paramref name="kind"/>, already hunting as a pack, each guarding the parent. Returns how many came out.
    /// </summary>
    private int Spill(Creature parent, WildlifeKind kind, int count, Vector3 playerFeet)
    {
        Vector2 player = new(playerFeet.X, playerFeet.Z);
        Vector2 toward = player - parent.Position;
        Vector2 spill = parent.Position + (toward.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(toward)) * BroodSpillMetres;
        float floor = StandingAt(parent);
        CreatureSpace space = SpaceOf(parent);
        HerdRecord record = new($"{kind.Id}@brood#{++hunterRegions}", kind.Id, spill, count) { Floor = floor + 1f };
        int first = nextId;
        if (space != walking || walking.UnderRock(new Vector3(spill.X, floor, spill.Y)))
            PlaceCaveHunter(record, kind, new Vector3(spill.X, floor + 1f, spill.Y), count, space);
        else
            PlaceHunter(record, kind, spill, 0, (int)Math.Ceiling(BroodSpillMetres * 2), count: count);

        int released = 0;
        for (int id = first; id < nextId; id++)
        {
            if (!roster.TryGet(id, out Creature hatched)) continue;
            broodOf[id] = parent.Id;
            hatched.Behavior = hatched.Behavior with { State = CreatureState.Alert };
            hatched.LastSeen = playerFeet;
            released++;
        }

        if (kind.Hunt!.Pack is WildlifePack pack) packAlarms[record.Key] = pack.AlarmSeconds;
        return released;
    }

    /// <summary>
    /// Each brood animal guards its bearer (#10961): while the player is further from the bearer than its brood's guard, it
    /// goes back to the bearer; once they come within it again, it hunts them. A brood whose bearer has gone hunts on.
    /// </summary>
    private void GuardBroods(Vector2 player, Vector3 playerFeet)
    {
        foreach ((int id, int bearerId) in broodOf.ToArray())
        {
            if (!roster.TryGet(id, out Creature hatched))
            {
                broodOf.Remove(id);
                continue;
            }

            // A bearer's brood, or a brood vessel's (#10963).
            if (!roster.TryGet(bearerId, out Creature bearer) || !hunters.TryGetValue(bearerId, out Hunter? hunter)
                || (hunter.Kind.Hunt!.Brood ?? hunter.Kind.Hunt.Fixture?.Brood) is not WildlifeBrood brood)
            {
                broodOf.Remove(id);
                continue;
            }

            bool guarding = errands.TryGetValue(id, out Errand? errand) && errand.Why == "guard";
            if (Vector2.Distance(bearer.Position, player) > brood.GuardMetres)
            {
                if (!guarding && Vector2.Distance(hatched.Position, bearer.Position) > BroodHomeMetres)
                    Send(hatched, bearer.Position, RetreatSeconds, "guard");
            }
            else if (guarding)
            {
                EndErrand(hatched, playerFeet);
            }
        }
    }

    /// <summary>
    /// A mender (#10961) that has the player in view goes to the doorway nearest them and stands there growing its seal; once
    /// whole the seal blocks, and the mender refills before the next. A whole seal or two near the player is its most.
    /// </summary>
    private void TendMender(Creature creature, Hunter hunter, WildlifeSealing sealing, Vector3 playerFeet, CreatureSpace? here, double seconds)
    {
        DoorSeal? growing = seals.FirstOrDefault(seal => seal.Mender == creature.Id && !seal.Whole);
        if (growing is not null)
        {
            growing.Grown = Math.Min(1, growing.Grown + (seconds / sealing.Seconds));
            hunter.Member.Clip = hunter.Kind.Hunt!.Rest?.Clip ?? WildlifeClipRole.Idle;
            if (!growing.Whole) return;
            sealsChanged = true;
            lastSealed[creature.Id] = step;
            errands.Remove(creature.Id);
            lastEvent = string.Create(CultureInfo.InvariantCulture, $"mender {creature.Id} sealed the doorway at {growing.Door.Floor.X:F1},{growing.Door.Floor.Y:F1},{growing.Door.Floor.Z:F1}");
            Send(creature, TenderRules.AwayFrom(creature.Position, new Vector2(playerFeet.X, playerFeet.Z), RetreatMetres), RetreatSeconds, "retreat");
            return;
        }

        CreatureSpace space = SpaceOf(creature);
        if (errands.TryGetValue(creature.Id, out Errand? errand))
        {
            if (errand.Why != "door" || PendingDoor(creature.Id) is not RuinDoorway door) return;
            if (TenderRules.Distance(creature.Position, door) > sealing.ReachMetres + ErrandArriveMetres) return;
            seals.Add(new DoorSeal(++nextSeal, door, space, sealing, creature.Id));
            pendingDoors.Remove(creature.Id);
            Send(creature, creature.Position, sealing.Seconds * 4, "sealing", stays: true);
            return;
        }

        // Only a mender hunting the player, in the player's space, sets off; one refilling waits. A doorway it gave up on is free again.
        pendingDoors.Remove(creature.Id);
        if (space != here || creature.Behavior.State is not (CreatureState.Alert or CreatureState.Pursuing or CreatureState.Attacking)) return;
        if (lastSealed.TryGetValue(creature.Id, out long sealedAt) && (step - sealedAt) / HunterRules.StepsPerSecond < sealing.RefillSeconds) return;
        Vector2 player = new(playerFeet.X, playerFeet.Z);
        int near = seals.Count(seal => seal.Space == space && TenderRules.Distance(player, seal.Door) <= SealNearPlayerMetres);
        if (near >= sealing.MostSeals) return;
        if (TenderRules.DoorToSeal(space.Doorways, playerFeet, seals.Where(seal => seal.Space == space).Select(seal => seal.Door).Concat(pendingDoors.Values), SealNearPlayerMetres)
            is not RuinDoorway chosen) return;
        pendingDoors[creature.Id] = chosen;
        Send(creature, TenderRules.SealingPlace(chosen, creature.Position), ToDoorSeconds, "door");
    }

    /// <summary>The doorway each mender is on its way to.</summary>
    private readonly Dictionary<int, RuinDoorway> pendingDoors = [];

    private RuinDoorway? PendingDoor(int mender) => pendingDoors.TryGetValue(mender, out RuinDoorway door) ? door : null;

    /// <summary>
    /// A tender struck by the player (#10961): a mender stops its seal and shuffles off; a bearer whose pods are whole splits
    /// them, as its threat would.
    /// </summary>
    private void TenderStruck(Creature creature, Hunter hunter)
    {
        Vector3 playerFeet = player.WorldFeetPosition;
        if (hunter.Kind.Hunt!.Seals is not null)
        {
            if (seals.RemoveAll(seal => seal.Mender == creature.Id && !seal.Whole) > 0) lastEvent = $"mender {creature.Id} was struck: its seal stopped";
            pendingDoors.Remove(creature.Id);
            Send(creature, TenderRules.AwayFrom(creature.Position, new Vector2(playerFeet.X, playerFeet.Z), RetreatMetres), RetreatSeconds, "retreat");
        }

        if (hunter.Kind.Hunt.Brood is WildlifeBrood brood && hunter.Member.WornLook != brood.SplitLook && !broodsDue.Contains(creature.Id)) broodsDue.Add(creature.Id);

        // A grazer struck mid-ring (#10962) stops ringing, calls nobody, and must rest before its next ring. Struck before it
        // rang, the blow disturbs it as being seen would: a rested grazer starts its ring, and one resting from its last ring
        // keeps away.
        if (hunter.Kind.Hunt.Calls is WildlifeCalling calling)
        {
            switch (TenderRules.Struck(calling, rings.ContainsKey(creature.Id), SinceRang(hunter)))
            {
                case GrazerBlow.CutsRing:
                    rings.Remove(creature.Id);
                    hunter.ThreatSeconds = 0;
                    lastRang[hunter.Record.Key] = step;
                    lastEvent = $"chime grazer {creature.Id} was struck: its ring was cut";
                    break;
                case GrazerBlow.StartsRing:
                    StartRing(creature, hunter);
                    lastEvent = $"chime grazer {creature.Id} was struck: it rings";
                    return;
                case GrazerBlow.KeepsAway:
                    break;
            }

            Send(creature, TenderRules.AwayFrom(creature.Position, new Vector2(playerFeet.X, playerFeet.Z), RetreatMetres), RetreatSeconds, "retreat");
        }
    }

    /// <summary>Whole seals shut sight (#10961): one standing between a creature and the player hides each from the other.</summary>
    private bool SealedOff(Creature creature, Vector3 playerFeet)
    {
        CreatureSpace space = SpaceOf(creature);
        float feet = StandingAt(creature);
        Vector2 player = new(playerFeet.X, playerFeet.Z);
        return seals.Any(seal => seal.Whole && seal.Space == space && TenderRules.InStorey(feet, seal.Door) && TenderRules.InStorey(playerFeet.Y, seal.Door)
            && TenderRules.Crosses(creature.Position, player, seal.Door));
    }

    /// <summary>
    /// The colliders whole seals stand as in a session (#10961): every character's step there meets them, the player's and
    /// the creatures'.
    /// </summary>
    internal IReadOnlyList<CharacterObstacle> Barriers(SpatialSession session) =>
        seals.Count == 0 && shutBowers.Count == 0 ? [] : [.. seals.Where(seal => seal.Whole && seal.Space.Session.Equals(session)).Select(seal =>
        {
            (Vector3 min, Vector3 max) = TenderRules.SealBox(seal.Door);
            Transform pose = new(seal.Space.ToLocal(seal.Door.Floor), Quaternion.Identity, Vector3.One);
            return new CharacterObstacle(ProductIds.DoorSealObstacleBase + (ulong)seal.Id, pose, min, max, CollisionEnabled: true, Vector3.Zero, Vector3.Zero);
        }), .. BowerWalls(session)];

    /// <summary>The navigation cells whole seals close in a space (#10961): each doorway's columns, at its floor, in local cells of this size.</summary>
    internal IReadOnlyList<(long X, long Z, float Floor)> SealedCells(CreatureSpace space, double cell)
    {
        List<(long X, long Z, float Floor)> cells = [];
        foreach (DoorSeal seal in seals.Where(s => s.Whole && s.Space == space))
        {
            Vector3 local = space.ToLocal(seal.Door.Floor);
            (Vector3 min, Vector3 max) = TenderRules.SealBox(seal.Door);
            // The cells under the whole opening, a cell either side of its plane, so no route squeezes past its edge.
            for (long x = (long)Math.Floor((local.X + min.X - cell) / cell); x <= (long)Math.Floor((local.X + max.X + cell) / cell) - 1; x++)
            for (long z = (long)Math.Floor((local.Z + min.Z - cell) / cell); z <= (long)Math.Floor((local.Z + max.Z + cell) / cell) - 1; z++)
                cells.Add((x, z, local.Y));
        }

        return cells;
    }

    /// <summary>The seals to draw (#10961): each with its object, its space's local floor point, which way it faces and how far grown.</summary>
    internal IReadOnlyList<WildlifeSeal> SealFacts() =>
        [.. seals.Select(seal => new WildlifeSeal(ProductIds.DoorSealBase + (ulong)seal.Id, seal.Space.ToLocal(seal.Door.Floor), seal.Door.FacesX, (float)seal.Grown, seal.Space))];

    /// <summary>
    /// The player's swing finds a whole seal in reach before any creature further off (#10961): it takes the blow, and the
    /// blows it needs cut it through. Null when no seal is in reach or a creature is nearer.
    /// </summary>
    private string? SwingAtSeal(double nearestCreature)
    {
        Vector3 feet = player.WorldFeetPosition;
        Vector2 at = new(feet.X, feet.Z);
        CreatureSpace? here = PlayerSpace;
        DoorSeal? seal = seals.Where(s => s.Whole && s.Space == here && TenderRules.InStorey(feet.Y, s.Door))
            .OrderBy(s => TenderRules.Distance(at, s.Door)).FirstOrDefault();
        if (seal is null) return null;
        double distance = TenderRules.Distance(at, seal.Door);
        if (distance > CharacterSheet.UnarmedReachMetres + SealSwingSlackMetres || distance > nearestCreature) return null;
        seal.Blows++;
        Vector3 struck = seal.Space.ToLocal(seal.Door.Floor + new Vector3(0, StruckHeightMetres, 0));
        if (seal.Blows < seal.BlowsToCut)
        {
            cues.RaiseAt(Cue.Strike, struck);
            return string.Create(CultureInfo.InvariantCulture, $"struck seal {seal.Id}: {seal.Blows}/{seal.BlowsToCut} ({(seal.Hard ? "hardened" : "fresh")})");
        }

        seals.Remove(seal);
        sealsChanged = true;
        cues.RaiseAt(Cue.Defeat, struck);
        return string.Create(CultureInfo.InvariantCulture, $"cut through seal {seal.Id} in {seal.Blows} blows ({(seal.Hard ? "hardened" : "fresh")})");
    }

    /// <summary>The tenders' readout (#10961): seals, broods and errands.</summary>
    internal string TendersReadout() => string.Create(CultureInfo.InvariantCulture,
        $"seals={seals.Count} [{string.Join(" | ", seals.Select(seal => $"{seal.Id} by {seal.Mender} at {seal.Door.Floor.X:F1},{seal.Door.Floor.Y:F1},{seal.Door.Floor.Z:F1} grown={seal.Grown:F2} age={seal.AgeSeconds:F0}s blows={seal.Blows}/{seal.BlowsToCut}"))}]"
        + $" broods={broodOf.Count} split={podsSplit.Count} errands=[{string.Join(" ", errands.Select(e => $"{e.Key}:{e.Value.Why}"))}]"
        + $" rings=[{string.Join(" ", rings.Select(r => $"{r.Key}:{(step - r.Value.Began) / HunterRules.StepsPerSecond:F1}s{(r.Value.Chimed ? " chimed" : "")}"))}] rested={lastRang.Count} lastCall=[{lastCall}]"
        + $" doorways={(PlayerSpace?.Doorways.Count ?? 0)} [{string.Join(" ", (PlayerSpace?.Doorways ?? []).Select(door => $"{door.Floor.X:F0},{door.Floor.Y:F0},{door.Floor.Z:F0}{(door.FacesX ? "x" : "z")}"))}]");

    /// <summary>Forgets the tenders' state with the creatures (a restart).</summary>
    private void ClearTenders()
    {
        errands.Clear();
        broodsDue.Clear();
        broodOf.Clear();
        podsSplit.Clear();
        seals.Clear();
        lastSealed.Clear();
        pendingDoors.Clear();
        rings.Clear();
        lastRang.Clear();
        lastCall = "none";
        sealsChanged = true;
        ClearFixtures();
        ClearDrinks();
    }
}

/// <summary>
/// A membrane a mender grows across a ruin doorway (#10961), as settled with Patch on the wiki's Ruins wildlife page. It grows
/// while its mender stands at it, then blocks passage and sight until it is cut through; it hardens with age.
/// </summary>
internal sealed class DoorSeal(int id, RuinDoorway door, CreatureSpace space, WildlifeSealing sealing, int mender)
{
    internal int Id { get; } = id;

    /// <summary>The doorway, in its space's metres.</summary>
    internal RuinDoorway Door { get; } = door;

    internal CreatureSpace Space { get; } = space;

    internal WildlifeSealing Sealing { get; } = sealing;

    /// <summary>The mender growing it; it keeps the id once grown, for the readout.</summary>
    internal int Mender { get; } = mender;

    /// <summary>How far grown, 0 to 1: it blocks once whole.</summary>
    internal double Grown { get; set; }

    /// <summary>How long it has stood whole.</summary>
    internal double AgeSeconds { get; set; }

    /// <summary>How many blows it has taken.</summary>
    internal int Blows { get; set; }

    internal bool Whole => Grown >= 1;

    internal bool Hard => TenderRules.Hard(Sealing, AgeSeconds);

    /// <summary>The blows that cut it through: a fresh seal's, or once hardened a hard one's.</summary>
    internal int BlowsToCut => TenderRules.BlowsToCut(Sealing, AgeSeconds);
}

/// <summary>A seal to draw (#10961): its object, its floor point in its space's local metres, which way it faces, how far grown.</summary>
internal sealed record WildlifeSeal(ulong ObjectId, Vector3 Floor, bool FacesX, float Grown, CreatureSpace Space);
