using System.Numerics;
using CraftSurvive.Game.Modules.Dungeons;
using CraftSurvive.Game.Modules.Wildlife;

namespace CraftSurvive.Game.Modules.Creatures;

/// <summary>The tenders' rules (#10961) that need no Engine: where a seal stands and what it shuts, where a tender goes.</summary>
internal static class TenderRules
{
    /// <summary>Whether a whole seal this old has hardened (#10961).</summary>
    internal static bool Hard(WildlifeSealing sealing, double ageSeconds) => ageSeconds >= sealing.HardenSeconds;

    /// <summary>The blows that cut a whole seal this old through: a fresh seal's, or once hardened a hard one's.</summary>
    internal static int BlowsToCut(WildlifeSealing sealing, double ageSeconds) => Hard(sealing, ageSeconds) ? sealing.HardBlows : sealing.FreshBlows;

    /// <summary>A seal's thickness through its doorway, in metres.</summary>
    internal const float SealThicknessMetres = 0.3f;

    /// <summary>
    /// A seal's box about its doorway's floor point, in that space's axes: thin along the way through, the doorway's width
    /// across, its height up.
    /// </summary>
    internal static (Vector3 Min, Vector3 Max) SealBox(RuinDoorway door)
    {
        float through = SealThicknessMetres / 2, across = RuinDoorway.Width / 2;
        Vector3 half = door.FacesX ? new Vector3(through, 0, across) : new Vector3(across, 0, through);
        return (new Vector3(-half.X, 0, -half.Z), new Vector3(half.X, RuinDoorway.Height, half.Z));
    }

    /// <summary>Whether a line along the ground from one point to another passes through a doorway's opening.</summary>
    internal static bool Crosses(Vector2 from, Vector2 to, RuinDoorway door)
    {
        Vector2 at = new(door.Floor.X, door.Floor.Z), through = door.Through, across = new(through.Y, through.X);
        float a = Vector2.Dot(from - at, through), b = Vector2.Dot(to - at, through);
        if (a * b > 0 || MathF.Abs(a - b) < 1e-6f) return false;
        float share = a / (a - b);
        float lateral = Vector2.Dot(Vector2.Lerp(from, to, share) - at, across);
        return MathF.Abs(lateral) <= RuinDoorway.Width / 2;
    }

    /// <summary>Whether a height stands within a doorway's storey: from a little under its floor to its top.</summary>
    internal static bool InStorey(float y, RuinDoorway door) => y >= door.Floor.Y - StoreySlackMetres && y <= door.Floor.Y + RuinDoorway.Height;

    private const float StoreySlackMetres = 1f;

    /// <summary>How far a point on the ground is from a doorway's opening: from the nearest point across its width.</summary>
    internal static float Distance(Vector2 point, RuinDoorway door)
    {
        Vector2 at = new(door.Floor.X, door.Floor.Z), through = door.Through, across = new(through.Y, through.X);
        Vector2 offset = point - at;
        float lateral = Math.Clamp(Vector2.Dot(offset, across), -RuinDoorway.Width / 2, RuinDoorway.Width / 2);
        return Vector2.Distance(point, at + (across * lateral));
    }

    /// <summary>Where a tender stands to seal a doorway: before its middle, on the side it comes from.</summary>
    internal static Vector2 SealingPlace(RuinDoorway door, Vector2 from)
    {
        Vector2 at = new(door.Floor.X, door.Floor.Z);
        float side = Vector2.Dot(from - at, door.Through) >= 0 ? 1 : -1;
        return at + (door.Through * side * StandOffMetres);
    }

    private const float StandOffMetres = 1f;

    /// <summary>Somewhere away from the player, this far from where a tender stands, for it to shuffle off to.</summary>
    internal static Vector2 AwayFrom(Vector2 at, Vector2 player, float metres)
    {
        Vector2 away = at - player;
        return at + (away.LengthSquared() < 1e-6f ? Vector2.UnitX : Vector2.Normalize(away)) * metres;
    }

    /// <summary>
    /// The doorway a mender seals (#10961): the one nearest the player on the player's storey, within reach of them, that no
    /// seal stands in or grows in; null for none.
    /// </summary>
    internal static RuinDoorway? DoorToSeal(IReadOnlyList<RuinDoorway> doors, Vector3 playerFeet, IEnumerable<RuinDoorway> taken, float withinMetres)
    {
        HashSet<RuinDoorway> shut = [.. taken];
        Vector2 player = new(playerFeet.X, playerFeet.Z);
        return doors.Where(door => !shut.Contains(door) && InStorey(playerFeet.Y, door) && Distance(player, door) <= withinMetres)
            .OrderBy(door => Distance(player, door)).Select(door => (RuinDoorway?)door).FirstOrDefault();
    }
}
