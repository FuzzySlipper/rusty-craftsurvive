namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The product's identity spaces in one place. Appearance object ids, spatial entity ids and
/// entity-store component keys are separate spaces in the Engine; within each, the product keeps
/// its owners in disjoint ranges so no two owners can mint the same id.
/// </summary>
internal static class ProductIds
{
    /// <summary>The player: a spatial entity and the target creatures perceive.</summary>
    internal const ulong PlayerEntity = 1UL;

    /// <summary>Creature appearance objects are this base plus the creature id.</summary>
    internal const ulong CreatureAppearanceBase = 0x1_0000UL;

    /// <summary>
    /// Creature perception observers are this base plus the creature id, so an observer never
    /// shares an id with the player it is looking for.
    /// </summary>
    internal const ulong CreatureObserverBase = 0x2_0000UL;

    /// <summary>Retained light ids: the sky's sun (or moon) and its ambient fill.</summary>
    internal const ulong SunLight = 1UL;

    internal const ulong AmbientLight = 2UL;

    /// <summary>Entity-store component keys.</summary>
    internal const uint PlayerRuntimeComponent = 1U;

    internal const uint CreatureRuntimeComponent = 0x1001U;
}
