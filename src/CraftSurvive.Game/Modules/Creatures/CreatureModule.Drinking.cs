using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Rpg;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>
/// The implement-carrier's light drinking (#10966), as settled with Patch on the wiki's Ruins wildlife page ("Its crescent
/// drinks light"): in its display it raises the crescent at the player's lit lantern within its reach and in its sight, and
/// the lantern gutters; whole, the drink puts it out for a while. The rules are <see cref="DrinkRules"/>; the lantern's
/// state is the dungeon module's <see cref="LanternDrain"/>, which this only sets.
/// </summary>
internal sealed partial class CreatureModule
{
    /// <summary>The player's lantern, the dungeon module's; null where none is wired (no drinking then).</summary>
    internal LanternDrain? Lantern { get; set; }

    /// <summary>Carriers raising the crescent now, by creature id.</summary>
    private readonly HashSet<int> drinking = [];

    /// <summary>When each carrier's last drink ended, whole or not, by its record's key: a carrier keeps its rest across sleep.</summary>
    private readonly Dictionary<string, long> lastDrank = [];

    private long drinksWhole, drinksFailed, drinksCut;

    /// <summary>One carrier's drink moves on, after its display has: what it does, and how brightly its core holds the light.</summary>
    private void TendDrink(Creature creature, Hunter hunter, bool here, bool seesPlayer, double playerMetres)
    {
        if (hunter.Kind.Hunt!.Drinks is not WildlifeDrinking drinks || Lantern is not LanternDrain lantern) return;
        bool raising = drinking.Contains(creature.Id);
        bool hunting = creature.Behavior.State is CreatureState.Alert or CreatureState.Pursuing or CreatureState.Attacking;
        switch (DrinkRules.Turn(drinks, raising, here && hunting, hunter.ThreatSeconds, hunter.BiteSeconds > 0 || hunter.HurtSeconds > 0, SinceDrank(hunter),
            lantern.Drinkable, lantern.Carried, playerMetres, here && seesPlayer))
        {
            case DrinkTurn.Begins:
                if (hunter.ThreatSeconds <= 0) hunter.ThreatSeconds = hunter.Kind.Hunt.Display!.Seconds;
                hunter.Threatened = true;
                if (lantern.Gutter(creature.Id, hunter.ThreatSeconds))
                {
                    drinking.Add(creature.Id);
                    lastEvent = $"carrier {creature.Id} raises its crescent: the lantern gutters";
                }

                break;
            case DrinkTurn.Fails:
                EndDrink(creature, hunter);
                lantern.Steady(creature.Id);
                drinksFailed++;
                lastEvent = $"carrier {creature.Id}'s drink failed: the lantern steadies";
                break;
            case DrinkTurn.Drinks:
                EndDrink(creature, hunter);
                lantern.Drink(creature.Id, drinks.OutSeconds, drinks.BackSeconds);
                drinksWhole++;
                lastEvent = $"carrier {creature.Id} drank the lantern: out for {drinks.OutSeconds:F0} s";
                break;
            case DrinkTurn.None:
            case DrinkTurn.Holds:
                break;
        }

        // Its core holds what it has taken: brightening through the raise as the lantern gutters, bright while it is out,
        // fading as it returns. Holding no drink, its clip decides (WildlifeMember.CoreGlow).
        hunter.Member.HeldGlow = lantern.Drinker == creature.Id ? lantern.Taken : null;
    }

    /// <summary>A carrier struck while it raises the crescent (#10966): the drink is cut short, its display ends, and it rests before the next.</summary>
    private void DrinkerStruck(Creature creature, Hunter hunter)
    {
        if (!drinking.Contains(creature.Id) || Lantern is not LanternDrain lantern) return;
        EndDrink(creature, hunter);
        lantern.Steady(creature.Id);
        hunter.ThreatSeconds = 0;
        drinksCut++;
        lastEvent = $"carrier {creature.Id} was struck: its drink was cut";
    }

    /// <summary>A carrier killed, or gone from the encounter (#10966): a drink under way fails, and a killed one gives back what it took.</summary>
    private void DrinkerGone(int id, bool killed)
    {
        bool wasDrinking = drinking.Remove(id);
        if (Lantern is not LanternDrain lantern) return;
        if (killed) lantern.Restore(id);
        else if (wasDrinking) lantern.Steady(id);
    }

    private void EndDrink(Creature creature, Hunter hunter)
    {
        drinking.Remove(creature.Id);
        lastDrank[hunter.Record.Key] = step;
    }

    private double? SinceDrank(Hunter hunter) => lastDrank.TryGetValue(hunter.Record.Key, out long drank) ? (step - drank) / HunterRules.StepsPerSecond : null;

    /// <summary>For the readout: the lantern's state and every carrier's drink.</summary>
    internal string DrinksReadout()
    {
        Vector3 playerWorld = player.WorldPosition;
        IEnumerable<string> carriers = hunters.Values.Where(hunter => hunter.Kind.Hunt!.Drinks is not null).Select(hunter =>
        {
            int id = hunters.First(entry => entry.Value == hunter).Key;
            double metres = roster.TryGet(id, out Creature creature) ? CreatureSimulation.PlanarDistance(creature.Position, playerWorld) : double.NaN;
            double? since = SinceDrank(hunter);
            return string.Create(CultureInfo.InvariantCulture,
                $"{id} {(drinking.Contains(id) ? "raising" : "-")} state={(roster.TryGet(id, out Creature c) ? c.Behavior.State.ToString() : "gone")} player={metres:F1}m display={hunter.ThreatSeconds:F2}s since={(since is double s ? s.ToString("F1", CultureInfo.InvariantCulture) + "s" : "never")} glow={(hunter.Member.HeldGlow is float glow ? glow.ToString("F2", CultureInfo.InvariantCulture) : "clip")}");
        });
        return $"{Lantern?.Readout() ?? "no lantern"}; drinks whole={drinksWhole} failed={drinksFailed} cut={drinksCut}; carriers: [{string.Join("; ", carriers)}]";
    }

    /// <summary>A carrier that fell asleep or left mid-raise fails its drink, so no lantern gutters for a drinker that is not there.</summary>
    private void DropGoneDrinkers()
    {
        if (drinking.Count == 0) return;
        foreach (int id in drinking.ToArray())
        {
            if (!hunters.ContainsKey(id) || !roster.TryGet(id, out Creature creature) || !creature.Awake) DrinkerGone(id, killed: false);
        }
    }

    private void ClearDrinks()
    {
        foreach (int id in drinking.ToArray()) DrinkerGone(id, killed: false);
        lastDrank.Clear();
    }
}
