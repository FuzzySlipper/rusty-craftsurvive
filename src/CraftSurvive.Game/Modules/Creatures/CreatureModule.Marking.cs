using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The channel sifter's spore mark (#10971), as settled with Patch on the wiki's Ruins wildlife page ("Channel sifter: its
/// spore mark"). A sifter that sees the player within its reach rears and puffs in its display; the spores burst into a
/// cloud that hangs where it burst; a player in it is marked (the player's hazards own the mark); and while the player is
/// marked every ruin hunter that comes within the mark's reach of them is drawn to them unseen, for as long as the mark
/// lasts. The rules are <see cref="MarkRules"/>.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>A sifter's puff under way: the update step it began, and whether its spores have burst yet.</summary>
    private sealed record Puff(long Began, bool Burst);

    /// <summary>A cloud of spores hanging where it burst, until an update step, in its space.</summary>
    private sealed record SporeCloud(int By, CreatureSpace Space, Vector3 Feet, long Until, WildlifeMarking Marking);

    /// <summary>A hunter drawn to a marked player sees them this far, wherever it was drawn from.</summary>
    private const double DrawnSightMetres = 200;

    private readonly Dictionary<int, Puff> puffs = [];
    private readonly List<SporeCloud> clouds = [];

    /// <summary>When each sifter's last puff ended, by its record's key: a sifter keeps its rest across sleep.</summary>
    private readonly Dictionary<string, long> lastPuffed = [];

    /// <summary>The hunters drawn to the marked player, by creature id; emptied when the mark ends.</summary>
    private readonly HashSet<int> drawnToMark = [];

    /// <summary>How far the mark on the player draws from: the reach of the sifter whose cloud marked them.</summary>
    private double markDrawMetres;

    private long puffsBurst;
    private string lastMark = "none";

    /// <summary>
    /// A channel sifter (#10971). Hunting the player within its puff's reach, rested, it puffs: it stands for its display, its
    /// spores bursting into a cloud partway through, and once the display has run it flees. Resting from its last puff, it
    /// keeps away.
    /// </summary>
    private void TendSifter(Creature creature, Hunter hunter, WildlifeMarking marking, Vector2 player)
    {
        if (puffs.TryGetValue(creature.Id, out Puff? puff))
        {
            if (!puff.Burst && (step - puff.Began) / HunterRules.StepsPerSecond >= marking.BurstSeconds)
            {
                long until = step + (long)Math.Ceiling(marking.CloudSeconds * HunterRules.StepsPerSecond);
                clouds.Add(new SporeCloud(creature.Id, SpaceOf(creature), new Vector3(creature.Position.X, StandingAt(creature), creature.Position.Y), until, marking));
                puffs[creature.Id] = puff with { Burst = true };
                puffsBurst++;
                lastMark = $"sifter {creature.Id} puffed: its cloud hangs {marking.CloudSeconds:F0} s";
            }

            if (hunter.ThreatSeconds > 0) return;
            EndPuff(creature, hunter);
            Send(creature, TenderRules.AwayFrom(creature.Position, player, RetreatMetres), RetreatSeconds, "flee");
            return;
        }

        bool hunting = creature.Behavior.State is CreatureState.Alert or CreatureState.Pursuing or CreatureState.Attacking;
        if (!hunting || errands.ContainsKey(creature.Id)) return;
        switch (MarkRules.Turn(marking, SincePuffed(hunter), Vector2.Distance(creature.Position, player)))
        {
            case PuffTurn.Puffs:
                StartPuff(creature, hunter);
                break;
            case PuffTurn.KeepsAway:
                hunter.ThreatSeconds = 0;
                Send(creature, TenderRules.AwayFrom(creature.Position, player, RetreatMetres), RetreatSeconds, "retreat");
                break;
            case PuffTurn.None:
                break;
        }
    }

    /// <summary>A sifter starts to puff: it stands showing its display, its first threat of a hunt or a fresh one.</summary>
    private void StartPuff(Creature creature, Hunter hunter)
    {
        errands.Remove(creature.Id);
        if (hunter.ThreatSeconds <= 0) hunter.ThreatSeconds = hunter.Kind.Hunt!.Display!.Seconds;
        hunter.Threatened = true;
        puffs[creature.Id] = new Puff(step, false);
    }

    private void EndPuff(Creature creature, Hunter hunter)
    {
        puffs.Remove(creature.Id);
        lastPuffed[hunter.Record.Key] = step;
    }

    private double? SincePuffed(Hunter hunter) => lastPuffed.TryGetValue(hunter.Record.Key, out long puffed) ? (step - puffed) / HunterRules.StepsPerSecond : null;

    /// <summary>A sifter struck before it puffs is disturbed as being seen would disturb it: rested, it puffs.</summary>
    private void SifterStruck(Creature creature, Hunter hunter, WildlifeMarking marking)
    {
        if (puffs.ContainsKey(creature.Id) || MarkRules.Turn(marking, SincePuffed(hunter), 0) != PuffTurn.Puffs) return;
        StartPuff(creature, hunter);
    }

    /// <summary>After the creatures have moved: clouds that have hung their while go, and one the player stands in marks them.</summary>
    private void TendClouds(Vector3 playerFeet, CreatureSpace? here)
    {
        clouds.RemoveAll(cloud => step > cloud.Until);
        // A sifter gone mid-puff (asleep, killed, forgotten) bursts nothing; spores already in the air hang on.
        foreach (int id in puffs.Keys.Where(id => !roster.TryGet(id, out Creature sifter) || !sifter.Awake || !hunters.ContainsKey(id)).ToArray()) puffs.Remove(id);
        if (here is null || player.Vitals.IsDown) return;
        foreach (SporeCloud cloud in clouds)
        {
            if (cloud.Space != here || !MarkRules.InCloud(cloud.Marking, cloud.Feet, playerFeet)) continue;
            bool fresh = !player.Hazards.Spores.Marked;
            player.Hazards.Spores.Mark(cloud.By, cloud.Marking.MarkSeconds);
            markDrawMetres = cloud.Marking.DrawMetres;
            if (fresh) lastMark = $"sifter {cloud.By}'s cloud marked the player for {cloud.Marking.MarkSeconds:F0} s";
        }
    }

    /// <summary>
    /// Before the creatures move (#10971): while the player is marked, every awake ruin hunter of their space within the mark's
    /// reach is drawn to them, and stays drawn while the mark lasts; drawn, it knows where they are, wakes if it rested, and
    /// hunts them as if it saw them.
    /// </summary>
    private void DrawToMark(HashSet<int> alerted, Vector3 playerFeet, CreatureSpace? here)
    {
        if (!player.Hazards.Spores.Marked || here is null || player.Vitals.IsDown)
        {
            if (drawnToMark.Count > 0) lastMark = $"the mark ended: {drawnToMark.Count} drawn hunter(s) left to their own rules";
            drawnToMark.Clear();
            return;
        }

        Vector2 at = new(playerFeet.X, playerFeet.Z);
        foreach (Creature creature in roster.All)
        {
            if (!creature.Awake || SpaceOf(creature) != here || !hunters.TryGetValue(creature.Id, out Hunter? hunter)) continue;
            if (MarkRules.Draws(hunter.Kind, Vector2.Distance(creature.Position, at), markDrawMetres)) drawnToMark.Add(creature.Id);
        }

        drawnToMark.RemoveWhere(id => !hunters.ContainsKey(id) || !roster.TryGet(id, out Creature drawn) || !drawn.Awake);
        foreach (int id in drawnToMark)
        {
            if (!roster.TryGet(id, out Creature drawn)) continue;
            alerted.Add(id);
            drawn.LastSeen = playerFeet;
            if (drawn.Behavior.State == CreatureState.Resting && !(overrides.Count > 0 && (Frozen(drawn) || ForcedState(drawn) is not null)))
                drawn.Behavior = drawn.Behavior with { State = CreatureState.Alert };
        }
    }

    /// <summary>A hunter drawn to the marked player sees them from wherever it was drawn.</summary>
    private bool DrawnToMark(Creature creature) => drawnToMark.Contains(creature.Id);

    /// <summary>Assisted: marks the player as a sifter's cloud would, for <paramref name="seconds"/>, drawing from the default reach.</summary>
    internal string MarkPlayer(double seconds, double drawMetres)
    {
        player.Hazards.Spores.Mark(-1, seconds);
        markDrawMetres = drawMetres;
        lastMark = string.Create(CultureInfo.InvariantCulture, $"assisted: the player is marked for {seconds:F0} s, drawing from {drawMetres:F0} m");
        return MarksReadout();
    }

    /// <summary>For the readout: the player's mark, the clouds hanging, each sifter's puff and rest, and the hunters drawn.</summary>
    internal string MarksReadout()
    {
        Vector3 feet = player.WorldFeetPosition;
        Vector2 at = new(feet.X, feet.Z);
        IEnumerable<string> sifters = hunters.Where(entry => entry.Value.Kind.Hunt!.Marks is not null).Select(entry =>
        {
            double metres = roster.TryGet(entry.Key, out Creature sifter) ? Vector2.Distance(sifter.Position, at) : double.NaN;
            double? since = SincePuffed(entry.Value);
            string state = roster.TryGet(entry.Key, out Creature s) ? s.Behavior.State.ToString() : "gone";
            return string.Create(CultureInfo.InvariantCulture,
                $"{entry.Key} {(puffs.TryGetValue(entry.Key, out Puff? p) ? (p.Burst ? "puffed" : "puffing") : "-")} state={state} player={metres:F1}m display={entry.Value.ThreatSeconds:F2}s since={(since is double t ? t.ToString("F1", CultureInfo.InvariantCulture) + "s" : "never")}{(errands.TryGetValue(entry.Key, out Errand? errand) ? $" errand={errand.Why}" : "")}");
        });
        IEnumerable<string> hanging = clouds.Select(cloud => string.Create(CultureInfo.InvariantCulture,
            $"by {cloud.By} at {cloud.Feet.X:F1},{cloud.Feet.Y:F1},{cloud.Feet.Z:F1} {(cloud.Until - step) / HunterRules.StepsPerSecond:F1}s left player={Vector2.Distance(new Vector2(cloud.Feet.X, cloud.Feet.Z), at):F1}m"));
        IEnumerable<string> drawn = drawnToMark.Select(id => roster.TryGet(id, out Creature creature)
            ? string.Create(CultureInfo.InvariantCulture, $"{id} {(hunters.TryGetValue(id, out Hunter? h) ? h.Kind.Id : "?")} {creature.Behavior.State} {Vector2.Distance(creature.Position, at):F1}m")
            : $"{id} gone");
        return string.Create(CultureInfo.InvariantCulture,
            $"player=[{player.Hazards.Spores.Readout()}] draws={markDrawMetres:F0}m clouds=[{string.Join("; ", hanging)}] puffs={puffsBurst} sifters=[{string.Join("; ", sifters)}] drawn=[{string.Join("; ", drawn)}] last=[{lastMark}]");
    }

    private void ClearMarks()
    {
        puffs.Clear();
        clouds.Clear();
        lastPuffed.Clear();
        drawnToMark.Clear();
        lastMark = "none";
    }
}
