namespace CraftSurvive.Game.Modules.Rpg;

/// <summary>Where an encounter lives. The region decides what may appear there.</summary>
internal enum RegionKind
{
    Wilderness = 0,
    Shoreline = 1,
    Dungeon = 2,
    Settlement = 3,
}

/// <summary>A site the world offers for an encounter, with the facts placement needs.</summary>
internal readonly record struct EncounterSite(
    RegionKind Region,
    long RegionId,
    long SurfaceY,
    long WaterLevel,
    bool HasGround,
    bool ShoreIsReachable)
{
    internal SpawnSite ToSpawnSite() => new(SurfaceY, WaterLevel, HasGround, ShoreIsReachable);
}

/// <summary>What a creature is, and where the world has offered to put it.</summary>
internal readonly record struct EncounterCandidate(
    int Id,
    EncounterSite Site,
    CreatureTraits Traits,
    long TimeWindow);

/// <summary>An encounter that is currently in the world.</summary>
internal readonly record struct ActiveEncounter(
    int Id,
    RegionKind Region,
    long RegionId,
    long ActivatedAtTick,
    long AwaySinceTick)
{
    internal bool IsAway => AwaySinceTick >= 0;
}

/// <summary>The product's standing limits on how much is awake at once.</summary>
internal readonly record struct EncounterPolicy(
    int MaximumActive,
    int MaximumActivePerRegion,
    double DespawnDistance,
    long DespawnGraceTicks)
{
    internal static EncounterPolicy Default => new(
        MaximumActive: 24,
        MaximumActivePerRegion: 8,
        DespawnDistance: 96.0,
        DespawnGraceTicks: 600);
}

internal readonly record struct EncounterDecision(bool Act, string Reason)
{
    internal static EncounterDecision Refuse(string reason) => new(false, reason);
    internal static EncounterDecision Allow(string reason) => new(true, reason);
}

/// <summary>
/// When an encounter may appear and when it must go away.
///
/// Activation is driven by **region and time**, not by the player's proximity: a
/// candidate is eligible only in its own time window, and only while its region
/// has room. Proximity appears in exactly one place - deciding that an encounter
/// which has been left far behind for long enough is despawned - so the world
/// keeps living while the player is elsewhere and does not keep paying for what
/// the player has abandoned.
/// </summary>
internal static class EncounterRules
{
    internal static bool IsEligible(EncounterCandidate candidate, long currentTimeWindow) =>
        candidate.TimeWindow == currentTimeWindow;

    internal static EncounterDecision CanActivate(
        EncounterPolicy policy,
        EncounterCandidate candidate,
        long currentTimeWindow,
        int activeTotal,
        int activeInRegion)
    {
        if (!IsEligible(candidate, currentTimeWindow))
        {
            return EncounterDecision.Refuse(
                $"its time window {candidate.TimeWindow} is not the current one {currentTimeWindow}");
        }

        if (activeTotal >= policy.MaximumActive)
        {
            return EncounterDecision.Refuse($"the world already holds {activeTotal} encounters");
        }

        if (activeInRegion >= policy.MaximumActivePerRegion)
        {
            return EncounterDecision.Refuse(
                $"region {candidate.Site.RegionId} already holds {activeInRegion} encounters");
        }

        SpawnVerdict placement = SpawnRules.Evaluate(candidate.Site.ToSpawnSite(), candidate.Traits);
        return placement.Allowed
            ? EncounterDecision.Allow("the site accepts this creature and the region has room")
            : EncounterDecision.Refuse(placement.Reason);
    }

    /// <summary>
    /// Whether an active encounter should be removed, given where it is now and
    /// whether its region is still resident.
    /// </summary>
    internal static EncounterDecision ShouldDespawn(
        EncounterPolicy policy,
        long tick,
        in ActiveEncounter encounter,
        double distanceToPlayer,
        bool regionResident)
    {
        if (!regionResident)
        {
            return EncounterDecision.Allow("its region is no longer resident");
        }

        if (distanceToPlayer <= policy.DespawnDistance)
        {
            return EncounterDecision.Refuse("the player is still near it");
        }

        if (!encounter.IsAway)
        {
            return EncounterDecision.Refuse("it has only just been left behind");
        }

        long away = tick - encounter.AwaySinceTick;
        return away >= policy.DespawnGraceTicks
            ? EncounterDecision.Allow($"it has been out of reach for {away} ticks")
            : EncounterDecision.Refuse($"it has been out of reach for {away} of {policy.DespawnGraceTicks} ticks");
    }

    /// <summary>Records that the encounter is out of reach, starting its grace period once.</summary>
    internal static ActiveEncounter MarkAway(in ActiveEncounter encounter, long tick, double distanceToPlayer, EncounterPolicy policy) =>
        distanceToPlayer > policy.DespawnDistance && !encounter.IsAway
            ? encounter with { AwaySinceTick = tick }
            : encounter;

    /// <summary>Records that the encounter is in reach again, clearing any grace period.</summary>
    internal static ActiveEncounter MarkPresent(in ActiveEncounter encounter, double distanceToPlayer, EncounterPolicy policy) =>
        distanceToPlayer <= policy.DespawnDistance ? encounter with { AwaySinceTick = -1 } : encounter;
}
