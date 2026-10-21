using System.Numerics;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>The underground forest's wall overgrowth (#10096): authored meshes, each in a wall's frame (the surface it grows on the plane z = 0, outward +z, y up).</summary>
internal enum OvergrowthKind
{
    /// <summary>Vine strands hanging about 3.4 m from a lip or cornice, leaf clumps thick at the top.</summary>
    VineDrape,

    /// <summary>The same, about 1.7 m.</summary>
    VineDrapeShort,

    /// <summary>A mound of ivy bulging off a wall's foot, its base on the floor, a tongue climbing the wall.</summary>
    IvyMound,

    /// <summary>A cushion of ivy on a wall's face, short trails falling from it.</summary>
    IvyClump,

    /// <summary>Roots twisting down about 3.5 m from a rib's head or an overhang, a little ivy at their head.</summary>
    RootTangle,

    /// <summary>A round tuft of grass out of a low moss pad, as if from a joint in the floor (a floor piece: on the plane y = 0, its +z only a turn).</summary>
    GrassTuft,

    /// <summary>A run of grass tufts 1.2 m long along its x, for a slab's seam or a wall's foot.</summary>
    GrassStrip,

    /// <summary>A cluster of small capped mushrooms.</summary>
    Mushrooms,
}

/// <summary>
/// One overgrowth on a surface: where its frame's origin stands (layout metres), the direction its +z faces (a wall
/// piece's surface outward; a floor piece's only a turn), its size, a pick for small variation, and its up: the world's
/// up by default, a rock floor's normal under a floor piece (the piece stands square to it), and a wall piece is turned to
/// face <see cref="Out"/> exactly, leaning with a sloping rock face. Any source of surface anchors makes these - the kit's
/// frames (<see cref="KitOvergrowth"/>), cave rock's points and normals (<see cref="KarstCave"/>) - and
/// <see cref="DungeonOvergrowth"/> draws them.
/// </summary>
internal readonly record struct OvergrowthPlacement(OvergrowthKind Kind, Vector3 At, Vector3 Out, float Scale, float Pick, Vector3? Up = null)
{
    /// <summary>Whether a kind stands on a floor (on the plane y = 0) rather than growing on a wall.</summary>
    internal static bool OnFloor(OvergrowthKind kind) => kind is OvergrowthKind.GrassTuft or OvergrowthKind.GrassStrip or OvergrowthKind.Mushrooms;
}
