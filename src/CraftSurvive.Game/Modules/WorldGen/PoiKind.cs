namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>
/// The kinds of place worth travelling to. The numeric value is part of the
/// persisted discovery record and of the generation contract's draws, so a value
/// may be appended but never renumbered.
/// </summary>
internal enum PoiKind : ushort
{
    /// <summary>No site. A cell that is submerged, out of bounds, or otherwise refused.</summary>
    None = 0,

    /// <summary>A ring of tall pillars: the cheapest thing to see from far away.</summary>
    StandingStones = 1,

    /// <summary>Fallen walls of a building, on ground flat enough to have held one.</summary>
    Ruin = 2,

    /// <summary>A stone arch and a recess, cut into a slope. The shape says "way in".</summary>
    CaveMouth = 3,

    /// <summary>
    /// A built descent: a framed shaft head on broken ground. This slice owns that it
    /// exists, where it stands, and that the player has found it; the load transition
    /// into an interior is the dimension slice's (#8604), so nothing here teleports.
    /// </summary>
    DungeonEntrance = 4,

    /// <summary>A platform on legs, on high ground: somewhere to look out from.</summary>
    VantagePoint = 5,
}
