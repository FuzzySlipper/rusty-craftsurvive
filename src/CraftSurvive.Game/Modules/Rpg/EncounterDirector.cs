namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>
/// The one owner of what is currently in the world. Placement, caps and despawn
/// are rules; this holds the state those rules act on, so that "how many are
/// awake" and "which ones" have a single answer rather than being recounted at
/// every call site.
///
/// It knows nothing about the Engine: the caller supplies the region residency
/// and the distance to the player for each tick, which is what lets the same
/// director be driven by a staged scene in checks and by the streamed world in
/// play.
/// </summary>
internal sealed class EncounterDirector
{
    private readonly EncounterPolicy policy;
    private readonly Dictionary<int, ActiveEncounter> active = [];

    internal EncounterDirector(EncounterPolicy policy) => this.policy = policy;

    internal int ActiveCount => active.Count;

    internal IReadOnlyCollection<int> ActiveIds => active.Keys;

    internal int ActiveCountIn(long regionId)
    {
        int count = 0;
        foreach (ActiveEncounter encounter in active.Values)
        {
            if (encounter.RegionId == regionId)
            {
                count++;
            }
        }

        return count;
    }

    internal bool TryGet(int id, out ActiveEncounter encounter) => active.TryGetValue(id, out encounter);

    /// <summary>Whether this encounter is already in the world.</summary>
    internal bool IsActive(int id) => active.ContainsKey(id);

    /// <summary>
    /// Attempts to place a candidate. A refusal carries the rule's typed reason and its text, so a
    /// caller can log why nothing appeared instead of guessing.
    /// </summary>
    internal bool TryActivate(EncounterCandidate candidate, long tick, out EncounterDecision decision)
    {
        if (active.ContainsKey(candidate.Id))
        {
            decision = EncounterDecision.Refuse(EncounterRefusal.AlreadyActive, $"encounter {candidate.Id} is already in the world");
            return false;
        }

        decision = EncounterRules.CanActivate(
            policy,
            candidate,
            candidate.TimeWindow,
            active.Count,
            ActiveCountIn(candidate.Site.RegionId));

        if (!decision.Act)
        {
            return false;
        }

        active[candidate.Id] = new ActiveEncounter(
            candidate.Id,
            candidate.Site.Region,
            candidate.Site.RegionId,
            ActivatedAtTick: tick,
            AwaySinceTick: -1);
        return true;
    }

    /// <summary>
    /// Advances every encounter's away/present bookkeeping and removes the ones
    /// the rules say must go, returning their ids so the caller can take them out
    /// of the world too. Removals are collected first so the dictionary is not
    /// mutated while it is being walked.
    /// </summary>
    internal IReadOnlyList<int> Tick(long tick, Func<int, double> distanceToPlayer, Func<long, bool> regionResident)
    {
        ArgumentNullException.ThrowIfNull(distanceToPlayer);
        ArgumentNullException.ThrowIfNull(regionResident);

        List<int> leaving = [];
        foreach ((int id, ActiveEncounter encounter) in active)
        {
            double distance = distanceToPlayer(id);
            bool resident = regionResident(encounter.RegionId);

            ActiveEncounter updated = EncounterRules.MarkPresent(
                EncounterRules.MarkAway(encounter, tick, distance, policy),
                distance,
                policy);

            if (EncounterRules.ShouldDespawn(policy, tick, updated, distance, resident).Act)
            {
                leaving.Add(id);
                continue;
            }

            active[id] = updated;
        }

        foreach (int id in leaving)
        {
            active.Remove(id);
        }

        return leaving;
    }

    /// <summary>Removes one encounter regardless of the rules - used when the world takes it away.</summary>
    internal bool Remove(int id) => active.Remove(id);

    internal void Clear() => active.Clear();
}
