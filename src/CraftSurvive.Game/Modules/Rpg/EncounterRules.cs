namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>One thing a table can produce.</summary>
internal readonly record struct LootEntry(string ItemId, int Minimum, int Maximum, int OneIn);

internal sealed record LootTable(string Id, int MinimumRolls, int MaximumRolls, LootEntry[] Entries);

internal readonly record struct LootDrop(string ItemId, int Quantity);

/// <summary>
/// Loot is a pure function of a table and an injected draw, which is the shape the
/// donor survey recommended: deterministic in checks with a fixed draw, and keyed
/// per encounter in play.
/// </summary>
internal static class LootRules
{
    /// <summary>
    /// Rarity is tested against a wide range rather than by equality on a narrow
    /// modulus, because a narrow modulus biases low draws. This is the same
    /// convention the terrain generator uses for its one-in-N draws.
    /// </summary>
    internal const int RarityScale = 1_000_000;

    /// <summary>Draws one number in [minimum, maximum] for a named scope.</summary>
    internal delegate int Draw(string scope, int minimum, int maximum);

    internal static LootDrop[] Roll(LootTable table, string scope, Draw draw)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(draw);
        if (table.MinimumRolls < 0 || table.MaximumRolls < table.MinimumRolls)
        {
            throw new ArgumentException($"Loot table {table.Id} has an impossible roll count.", nameof(table));
        }

        int rolls = draw($"{scope}:count", table.MinimumRolls, table.MaximumRolls);
        List<LootDrop> drops = [];
        for (int roll = 0; roll < rolls; roll++)
        {
            foreach (LootEntry entry in table.Entries)
            {
                if (entry.OneIn <= 0)
                {
                    throw new ArgumentException($"Loot entry {entry.ItemId} needs a positive one-in.", nameof(table));
                }

                int threshold = Math.Max(RarityScale / entry.OneIn, 1);
                int rarityDraw = draw($"{scope}:{entry.ItemId}:{roll}", 1, RarityScale);
                if (rarityDraw > threshold)
                {
                    continue;
                }

                int quantity = draw($"{scope}:{entry.ItemId}:{roll}:quantity", entry.Minimum, entry.Maximum);
                drops.Add(new LootDrop(entry.ItemId, quantity));
            }
        }

        return [.. drops];
    }
}

/// <summary>What a creature can do, which decides where it may be placed.</summary>
internal readonly record struct CreatureTraits(bool CanSwim, bool CanClimb, bool CanFly)
{
    internal static CreatureTraits Walker => new(CanSwim: false, CanClimb: false, CanFly: false);
    internal static CreatureTraits Amphibious => new(CanSwim: true, CanClimb: false, CanFly: false);
}

/// <summary>The ground and water facts at a candidate placement.</summary>
internal readonly record struct SpawnSite(long GroundY, long WaterLevel, bool HasGround, bool ShoreIsReachable)
{
    internal bool IsSubmerged => HasGround && GroundY < WaterLevel;
    internal bool IsWaterAdjacent => HasGround && GroundY == WaterLevel;
}

internal enum SpawnRefusal
{
    None = 0,
    NoGround = 1,
    SubmergedWithoutSwimming = 2,
    StrandedInWater = 3,
}

internal readonly record struct SpawnVerdict(bool Allowed, SpawnRefusal Refusal, string Reason);

/// <summary>
/// Placement rules for encounters. The invariant this exists to keep is that a
/// creature is never placed where it cannot survive or leave: a walker is never
/// put in a lake, and a swimmer that cannot climb is never put where the shore is
/// out of reach.
/// </summary>
internal static class SpawnRules
{
    internal static SpawnVerdict Evaluate(SpawnSite site, CreatureTraits traits)
    {
        if (!site.HasGround)
        {
            return new SpawnVerdict(false, SpawnRefusal.NoGround, "there is no ground at this site");
        }

        if (site.IsSubmerged && !traits.CanSwim && !traits.CanFly)
        {
            return new SpawnVerdict(false, SpawnRefusal.SubmergedWithoutSwimming,
                $"ground is at {site.GroundY} under water level {site.WaterLevel} and this creature cannot swim");
        }

        if (site.IsSubmerged && !site.ShoreIsReachable && !traits.CanClimb && !traits.CanFly)
        {
            return new SpawnVerdict(false, SpawnRefusal.StrandedInWater,
                "the water is deeper than the shore it could leave by and this creature cannot climb out");
        }

        return new SpawnVerdict(true, SpawnRefusal.None, "the site is safe for this creature");
    }
}
