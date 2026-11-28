namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>Architectural style of an overworld river bridge (#11186).</summary>
internal enum CrossingStyle : byte
{
    /// <summary>Weathered timber footbridge for smaller creeks and wilderness streams.</summary>
    TimberFootbridge = 0,

    /// <summary>Sturdy masonry-and-timber bridge for settled river hinterlands and wider channels.</summary>
    StoneBridge = 1,
}

/// <summary>
/// Fast macro context at an anchor cell for continental crossing cadence (#11186),
/// evaluated from continent-level bounds and settlement seats without loading region tiles.
/// </summary>
internal readonly record struct CrossingContext(
    double SettlementDistanceMetres,
    bool BeyondFrontier,
    bool InNeck,
    bool OpenSea);

/// <summary>
/// One shaped river centreline point inside an anchor cell, offered by <see cref="MapDrainage"/>
/// for hydrology-driven bridge placement (#11186).
/// </summary>
internal readonly record struct RiverCrossingCandidate(
    double CentreX,
    double CentreZ,
    double DownX,
    double DownZ,
    double HalfWidth,
    double WaterSurface,
    double Flow,
    Navigability Class,
    bool InLake);

/// <summary>
/// Civilization-gradient cadence, stream-size filtering, and style rules for continental river
/// crossings (#11186). Replaces blind 256 m grid-line scanning with crossings anchored on mapped
/// drainage reaches: regular in settled river hinterlands (~1-2 km along rivers), rare weathered
/// footbridges in deep wilderness (~5-7 km), and none beyond the frontier, in open sea, on tiny
/// headwater trickles, or across navigable boat/great rivers.
/// </summary>
internal static class CrossingDistribution
{
    // --- civilization gradient distances (metres from nearest settlement seat or hold) ---
    internal const double TownApproachMetres = 4_000;
    internal const double SettledHinterlandMetres = PoiDistribution.SettledHinterlandMetres;
    internal const double DeepFrontierMetres = PoiDistribution.DeepFrontierMetres;

    // --- per-cell cadence probabilities (a river crossing 256 m cells averages ~1 bridge per 256/p metres) ---
    private const double TownApproachCadence = 0.26;
    private const double HinterlandCadence = 0.17;
    private const double BorderlandsCadence = 0.08;
    private const double DeepWildernessCadence = 0.04;
    private const double NeckCadenceMultiplier = 0.50;

    // --- stream & river eligibility bounds ---
    /// <summary>Minimum mean flow (m³/s, ~4 km² catchment) so tiny spring-head trickles are waded rather than bridged.</summary>
    internal const double MinimumFlow = 0.08;

    /// <summary>Maximum half-width (m) bridged in settled river hinterlands (wider rivers use boats).</summary>
    internal const double SettledMaximumHalfWidth = 10.5;

    /// <summary>Maximum half-width (m) bridged in the borderlands.</summary>
    internal const double BorderlandsMaximumHalfWidth = 8.0;

    /// <summary>Maximum half-width (m) bridged by weathered wilderness footbridges.</summary>
    internal const double WildernessMaximumHalfWidth = 6.0;

    /// <summary>Minimum bank-to-bank span (m) worth placing a bridge across.</summary>
    internal const double MinimumSpanMetres = 3.0;

    /// <summary>Maximum bank-to-bank span (m) for settled stone bridges.</summary>
    internal const double SettledMaximumSpanMetres = 26.0;

    /// <summary>Maximum bank-to-bank span (m) for borderland bridges.</summary>
    internal const double BorderlandsMaximumSpanMetres = 20.0;

    /// <summary>Maximum bank-to-bank span (m) for wilderness footbridges.</summary>
    internal const double WildernessMaximumSpanMetres = 16.0;

    /// <summary>Half-width (m) above which a bridge upgrades to masonry even outside the settled hinterland.</summary>
    internal const double StoneBridgeHalfWidthThreshold = 5.0;

    /// <summary>Extra search distance (m) past the nominal channel half-width when locating dry banks.</summary>
    internal const int BankSearchMargin = 10;

    /// <summary>Maximum elevation difference (m) allowed between near and far banks so ramps stay walkable.</summary>
    internal const double MaximumBankHeightDifference = 4.0;

    /// <summary>Keep bridge centres this far inside the anchor cell so bank searches stay compact.</summary>
    internal const double CellEdgeMargin = 16.0;

    /// <summary>Probability in [0, 1] that an anchor cell with an eligible reach receives a crossing.</summary>
    internal static double Cadence(in CrossingContext context)
    {
        if (context.BeyondFrontier || context.OpenSea)
        {
            return 0;
        }

        double cadence = context.SettlementDistanceMetres < TownApproachMetres ? TownApproachCadence
            : context.SettlementDistanceMetres < SettledHinterlandMetres ? HinterlandCadence
            : context.SettlementDistanceMetres < DeepFrontierMetres ? BorderlandsCadence
            : DeepWildernessCadence;

        if (context.InNeck)
        {
            cadence *= NeckCadenceMultiplier;
        }

        return Math.Clamp(cadence, 0, 1);
    }

    internal static double MaximumHalfWidth(in CrossingContext context) =>
        context.SettlementDistanceMetres < SettledHinterlandMetres ? SettledMaximumHalfWidth
        : context.SettlementDistanceMetres < DeepFrontierMetres ? BorderlandsMaximumHalfWidth
        : WildernessMaximumHalfWidth;

    internal static double MaximumSpanMetres(in CrossingContext context) =>
        context.SettlementDistanceMetres < SettledHinterlandMetres ? SettledMaximumSpanMetres
        : context.SettlementDistanceMetres < DeepFrontierMetres ? BorderlandsMaximumSpanMetres
        : WildernessMaximumSpanMetres;

    /// <summary>Whether a river centreline point is an eligible stream/small river (not a lake, trickle, or boat/great river).</summary>
    internal static bool IsBridgeable(in RiverCrossingCandidate candidate, in CrossingContext context) =>
        !candidate.InLake
        && candidate.Flow >= MinimumFlow
        && candidate.Class < Navigability.Boat
        && candidate.HalfWidth > 0
        && candidate.HalfWidth <= MaximumHalfWidth(in context);

    internal static CrossingStyle StyleFor(in RiverCrossingCandidate candidate, in CrossingContext context) =>
        context.SettlementDistanceMetres < SettledHinterlandMetres || candidate.HalfWidth >= StoneBridgeHalfWidthThreshold
            ? CrossingStyle.StoneBridge
            : CrossingStyle.TimberFootbridge;
}
