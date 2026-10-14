namespace CraftSurvive.Game.Modules.WorldGen;

/// <summary>What a settlement is: the start's sea port, a river town, or the mountain hold at the neck.</summary>
internal enum SettlementKind { Port, Town, Hold }

/// <summary>Why a site was chosen, for the census and the place's description.</summary>
internal enum SettlementReason { Mouth, Harbour, Confluence, BoatHead, CanoeHead, LakeOutlet }

/// <summary>
/// A chosen settlement: where it stands (world metres), on which reach, why and its score. A port also
/// carries its shelter from the open sea and the flat land about it (0 to 1), and whether its river is
/// the largest of the harbours it was chosen from. A seated settlement (#9895) stands beside its water
/// on <see cref="Ground"/>, the pad its town is built on; <see cref="X"/> and <see cref="Z"/> are then
/// that seat.
/// </summary>
internal sealed record Settlement(string Name, SettlementKind Kind, double X, double Z, int Reach, SettlementReason Reason, double Score,
    double Shelter = 0, double Flat = 0, bool LargestFlow = true)
{
    internal string Key => KeyFor(Name);
    internal static string KeyFor(string name) => "town:" + name;

    /// <summary>The town's ground beside its water; until seated, a level pad at the site facing +X.</summary>
    internal TownGround Ground { get; init; } = new(X, Z, 1, 0, GenerationConstants.WaterLevel + TownGround.QuayMetres);
}
