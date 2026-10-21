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
}

/// <summary>
/// One overgrowth on a surface: where its frame's origin stands (layout metres), the surface's outward direction there
/// (its +z), its size and a pick for small variation. Any source of surface anchors makes these - the kit's frames
/// (<see cref="KitOvergrowth"/>) now, cave rock's points and normals later - and <see cref="DungeonOvergrowth"/> draws them.
/// </summary>
internal readonly record struct OvergrowthPlacement(OvergrowthKind Kind, Vector3 At, Vector3 Out, float Scale, float Pick);
