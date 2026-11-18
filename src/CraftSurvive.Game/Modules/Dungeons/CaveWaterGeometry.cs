using System.Numerics;
using System.Runtime.CompilerServices;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// A cave lake's or pool's finished surface (#10889): its grid's corners, faded by the depth under them, with an underside.
/// Built off the update from the cave's shape, kept with its zone while the zone lives, and stored with a stored cave, whose
/// shape is not kept (#10888 R10888-2).
/// </summary>
internal sealed record CaveWaterGeometry(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, Color[] Colours, uint[] Indices)
{
    private static readonly ConditionalWeakTable<WaterZone, CaveWaterGeometry> Kept = new();

    /// <summary>The surface built for a zone, if one was.</summary>
    internal static CaveWaterGeometry? Of(WaterZone zone) => Kept.TryGetValue(zone, out CaveWaterGeometry? geometry) ? geometry : null;

    /// <summary>Keeps a zone's surface for when it is drawn.</summary>
    internal static void Keep(WaterZone zone, CaveWaterGeometry geometry) => Kept.AddOrUpdate(zone, geometry);
}
