using CraftSurvive.Game.Modules.Rpg;

namespace CraftSurvive.Game.Modules.Player;

/// <summary>
/// The one owner of what the player has earned: experience, level and the drops collected.
/// Awards arrive through the progression rules, so only the sources they accept advance it.
/// </summary>
internal sealed class PlayerProgress
{
    internal int Experience { get; private set; }

    internal int Level { get; private set; } = CharacterRules.MinimumLevel;

    /// <summary>How many items the player has collected, all kinds together.</summary>
    internal int ItemsCollected { get; private set; }

    /// <summary>Drops awarded and not yet taken by the inventory, which owns what is carried.</summary>
    private readonly Queue<LootDrop> uncollected = new();

    /// <summary>Takes the oldest awarded drop the inventory has not taken yet, if any.</summary>
    internal bool TryTakeDrop(out LootDrop drop) => uncollected.TryDequeue(out drop);

    internal ProgressionOutcome Award(EncounterReward reward)
    {
        ProgressionOutcome outcome = ProgressionRules.Award(Experience, Level, reward.Experience);
        Experience = outcome.Experience;
        Level = outcome.Level;
        foreach (LootDrop drop in reward.Drops)
        {
            ItemsCollected += drop.Quantity;
            uncollected.Enqueue(drop);
        }

        return outcome;
    }

    /// <summary>Continues from a saved session. The level follows from the experience.</summary>
    internal void Restore(int experience, int itemsCollected)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemsCollected);
        Level = ProgressionRules.LevelFor(experience);
        Experience = experience;
        ItemsCollected = itemsCollected;
    }

    internal void Reset()
    {
        uncollected.Clear();
        Experience = 0;
        Level = CharacterRules.MinimumLevel;
        ItemsCollected = 0;
    }
}
