namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>
/// Engine bodies by the animal that owns them (#10569). Ids are recycled, so an id alone does not say
/// whose body it is: a body serves only the animal it was made for, and is retired when that animal
/// goes, whoever holds its id by then and however that one is drawn.
/// </summary>
internal sealed class OwnedBodies<TBody>
    where TBody : class
{
    private readonly Dictionary<int, (WildlifeMember Owner, TBody Body)> bodies = [];

    internal int Count => bodies.Count;

    internal IEnumerable<TBody> All => bodies.Values.Select(entry => entry.Body);

    /// <summary>The animal's body, made now if it has none; another animal's body under its id is retired first.</summary>
    internal TBody Take(WildlifeMember member, Func<TBody> create, Action<TBody> retire)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(retire);
        if (bodies.TryGetValue(member.Id, out (WildlifeMember Owner, TBody Body) entry))
        {
            if (entry.Owner == member) return entry.Body;
            retire(entry.Body);
        }

        TBody body = create();
        bodies[member.Id] = (member, body);
        return body;
    }

    /// <summary>Retires every body whose owner is not among these animals.</summary>
    internal void Keep(IReadOnlySet<WildlifeMember> owners, Action<TBody> retire)
    {
        ArgumentNullException.ThrowIfNull(owners);
        ArgumentNullException.ThrowIfNull(retire);
        foreach ((int id, (WildlifeMember owner, TBody body)) in bodies.ToArray())
        {
            if (owners.Contains(owner)) continue;
            retire(body);
            bodies.Remove(id);
        }
    }
}

/// <summary>
/// Numbers for bodies' Engine objects (#10569). The Engine keeps a node per object id, of the kind
/// first drawn there, and a body retired this update is still in the last snapshot. So a number goes
/// round the whole range before it comes back, and one in use is skipped.
/// </summary>
internal sealed class CyclingSlots(int count)
{
    private readonly HashSet<int> used = [];
    private int next;

    internal int Take()
    {
        if (used.Count >= count) throw new InvalidOperationException($"more than {count} bodies at once");
        while (!used.Add(next)) next = (next + 1) % count;
        int slot = next;
        next = (next + 1) % count;
        return slot;
    }

    internal void Release(int slot) => used.Remove(slot);
}
