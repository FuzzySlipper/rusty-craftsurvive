using System.Numerics;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>How much ruin a cave holds (#10955): the dungeon's ruin sizes, as wildlife reads them.</summary>
internal enum RuinExtent
{
    /// <summary>The breach and a room or two.</summary>
    Shell,

    /// <summary>A buried wing: a few rooms and the vault.</summary>
    Wing,

    /// <summary>A large building, several storeys down to its vault.</summary>
    Large,
}

/// <summary>Where in a ruin a kind of its life lives (#10955).</summary>
internal enum RuinSite
{
    /// <summary>In a wall beside a doorway (the niche unfolder).</summary>
    BesideDoor,

    /// <summary>Under a relic light (the vault filter).</summary>
    Light,

    /// <summary>In the middle of a room.</summary>
    Room,

    /// <summary>In a room's corner (the brood vessel).</summary>
    Corner,

    /// <summary>Near its fixture (a tender).</summary>
    Near,

    /// <summary>On a doorway's threshold (the threshold lurker).</summary>
    Threshold,

    /// <summary>Through the ruin's passages and stairs.</summary>
    Passage,

    /// <summary>At the ruin's mouth, the cave's floor before its breach.</summary>
    Mouth,

    /// <summary>At the vault.</summary>
    Vault,
}

/// <summary>One room of a ruin, in world metres over its floor: its middle and its corners.</summary>
internal sealed record RuinRoom(Vector3 Middle, IReadOnlyList<Vector3> Corners, bool Vault, bool Passage);

/// <summary>
/// What a cave's ruin offers its life (#10955), in world metres, each site in the air just over its floor as a cave's sites
/// are: its rooms, the doorways' thresholds and the walls beside them, the floors under its relic lights, its mouth, and the
/// boxes of its modules, which keep cave life out.
/// </summary>
internal sealed record RuinSites(RuinExtent Extent, Vector3 Mouth, IReadOnlyList<RuinRoom> Rooms, IReadOnlyList<Vector3> Thresholds,
    IReadOnlyList<Vector3> BesideDoors, IReadOnlyList<Vector3> Lights, IReadOnlyList<(Vector3 Min, Vector3 Max)> Boxes)
{
    internal string Readout() => string.Create(System.Globalization.CultureInfo.InvariantCulture,
        $"{Extent} rooms={Rooms.Count(room => !room.Passage && !room.Vault)} passages={Rooms.Count(room => room.Passage)} vault={Rooms.Any(room => room.Vault)} doorways={Thresholds.Count} lights={Lights.Count} mouth={Mouth.X:F0},{Mouth.Y:F0},{Mouth.Z:F0}");

    /// <summary>Whether a place lies in one of the ruin's modules.</summary>
    internal bool Inside(Vector3 at) => Boxes.Any(box => at.X >= box.Min.X && at.X <= box.Max.X && at.Y >= box.Min.Y && at.Y <= box.Max.Y && at.Z >= box.Min.Z && at.Z <= box.Max.Z);

    /// <summary>Whether cave life keeps away from a place: in the ruin, or within <paramref name="mouthMetres"/> of its mouth.</summary>
    internal bool KeepsAway(Vector3 at, float mouthMetres) => Inside(at) || Vector3.Distance(at, Mouth) < mouthMetres;

    /// <summary>The places a kind of ruin life may take, by its site (a tender's are found by its fixture instead).</summary>
    internal IReadOnlyList<Vector3> Of(RuinSite site) => site switch
    {
        RuinSite.BesideDoor => BesideDoors,
        RuinSite.Light => Lights,
        RuinSite.Room => [.. Rooms.Where(room => !room.Passage && !room.Vault).Select(room => room.Middle)],
        RuinSite.Corner => [.. Rooms.Where(room => !room.Passage && !room.Vault).SelectMany(room => room.Corners)],
        RuinSite.Threshold => Thresholds,
        RuinSite.Passage => [.. Rooms.Where(room => room.Passage).Select(room => room.Middle)],
        RuinSite.Mouth => [Mouth],
        RuinSite.Vault => [.. Rooms.Where(room => room.Vault).Select(room => room.Middle)],
        _ => [],
    };

    /// <summary>Every place in the ruin a tender may stand by its fixture: its rooms' middles and corners and under its lights, the vault's middle kept for its special.</summary>
    internal IEnumerable<Vector3> AnyPlace => Rooms.SelectMany(room => room.Vault ? room.Corners : room.Corners.Prepend(room.Middle)).Concat(Lights);

    /// <summary>
    /// The places a kind makes do with when its own are all taken: one that lives in rooms, corners or passages takes any
    /// room's or passage's middle or corner (the vault's middle kept for its special). A doorway, light, mouth or vault kind
    /// has no other place.
    /// </summary>
    internal IReadOnlyList<Vector3> OrElse(RuinSite site) => site is RuinSite.Room or RuinSite.Corner or RuinSite.Passage
        ? [.. Rooms.SelectMany(room => room.Vault ? room.Corners : room.Corners.Prepend(room.Middle))]
        : [];
}
