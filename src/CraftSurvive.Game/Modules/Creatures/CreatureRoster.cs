using System.Numerics;
using CraftSurvive.Game.Modules.Rpg;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>One creature: where it is in the world, what it can take, and what it is doing.</summary>
internal sealed class Creature
{
    internal Creature(int id, CreatureKind kind, Vector2 position)
    {
        Id = id;
        Kind = kind;
        Position = position;
        Combat = CombatantState.Fresh(kind.MaximumHealth, kind.Defence);
        Behavior = CreatureBehaviorState.Spawned;
    }

    internal int Id { get; }

    internal CreatureKind Kind { get; }

    /// <summary>World X and Z. Height follows the ground the creature stands on.</summary>
    internal Vector2 Position { get; set; }

    /// <summary>
    /// The height of the ground it stands on, carried from step to step (#9734): each move takes the
    /// first surface below its reach from here, not the highest in its column, so a creature that
    /// walked in under a roof stays on the floor. Null until first placed on resident ground.
    /// </summary>
    internal float? Feet { get; set; }

    internal CombatantState Combat { get; set; }

    internal CreatureBehaviorState Behavior { get; set; }

    /// <summary>
    /// Whether the ground under the creature is resident. A creature over ground the world has not
    /// loaded is dormant: not drawn, not sensing and not moving, until the player comes near.
    /// </summary>
    internal bool Awake { get; set; }

    /// <summary>The navigation waypoint the creature is walking to, in world X and Z, and the step it was chosen at.</summary>
    internal Vector2? Waypoint { get; set; }

    internal long WaypointStep { get; set; }

    /// <summary>What the last route query answered, for readouts.</summary>
    internal string RouteOutcome { get; set; } = "none";

    /// <summary>After a failed route, the step before which the creature does not ask again (#9531).</summary>
    internal long BackoffUntil { get; set; }

    /// <summary>The current back-off length; zero once a route is found.</summary>
    internal long BackoffSteps { get; set; }

    /// <summary>The path being followed, as world X/Z cell centres, and how far along it the creature is.</summary>
    internal Vector2[]? Path { get; set; }
    internal int PathIndex { get; set; }

    /// <summary>Where the player stood when the path was asked for, and the navigation publication it was asked of.</summary>
    internal Vector2 PathGoal { get; set; }
    internal long PathRevision { get; set; }

    /// <summary>How many steps the creature has not closed on its waypoint, and how far it was last step.</summary>
    internal int StuckSteps { get; set; }
    internal float WaypointDistance { get; set; } = float.MaxValue;
}

/// <summary>
/// The one table of creatures in the world. The encounter director decides membership; the roster
/// mirrors it exactly, and everything else - appearances, entity rows - is derived from the roster
/// each update rather than kept alongside it.
/// </summary>
internal sealed class CreatureRoster
{
    private readonly SortedDictionary<int, Creature> creatures = [];

    internal int Count => creatures.Count;

    /// <summary>Every creature, in id order.</summary>
    internal IEnumerable<Creature> All => creatures.Values;

    internal IEnumerable<int> Ids => creatures.Keys;

    internal bool TryGet(int id, out Creature creature) => creatures.TryGetValue(id, out creature!);

    internal void Add(Creature creature)
    {
        ArgumentNullException.ThrowIfNull(creature);
        if (!creatures.TryAdd(creature.Id, creature))
        {
            throw new InvalidOperationException($"Creature {creature.Id} is already in the roster.");
        }
    }

    internal bool Remove(int id) => creatures.Remove(id);

    /// <summary>Removes every id the director released; returns how many were present.</summary>
    internal int Release(IEnumerable<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        int removed = 0;
        foreach (int id in ids)
        {
            removed += creatures.Remove(id) ? 1 : 0;
        }

        return removed;
    }

    internal void Clear() => creatures.Clear();
}
