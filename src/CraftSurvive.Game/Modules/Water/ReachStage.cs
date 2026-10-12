namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// A reach's water now: its flow (m³/s), that flow against its mean (Ratio) and against its bankfull flow
/// (Fullness), the stage that gives over its mean surface (metres), and whether it is frozen or dry.
/// </summary>
internal readonly record struct ReachStage(double Flow, double Ratio, double Fullness, double Stage, bool Ice, bool Dry)
{
    /// <summary>Over its banks: more than its channel holds.</summary>
    internal bool Overbank => Fullness > 1;
}

/// <summary>A river crossing as the party meets it (#9878): none, as usual, running high, in flood, frozen over, or dry.</summary>
internal enum RiverState { None, Running, High, Flood, Frozen, Dry }

/// <summary>How a reach's stage reads as a crossing.</summary>
internal static class RiverStates
{
    /// <summary>
    /// A reach carrying this share of its bankfull flow runs high - about twice its mean on most rivers.
    /// A share rather than a depth, so a river reads the same along its length, whatever each reach's slope.
    /// </summary>
    internal const double HighFullness = 0.6;

    /// <summary>A crossing's state from its reach's stage; a river with no stage yet runs as usual.</summary>
    internal static RiverState Of(ReachStage? stage) => stage switch
    {
        null => RiverState.Running,
        { Dry: true } => RiverState.Dry,
        { Ice: true } => RiverState.Frozen,
        { Overbank: true } => RiverState.Flood,
        { Fullness: >= HighFullness } => RiverState.High,
        _ => RiverState.Running,
    };
}
