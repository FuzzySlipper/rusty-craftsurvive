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

    /// <summary>The vent orchard's (#10096) ivory tube, 2.6 m, open at its top under a crown of red feather fronds.</summary>
    VentTall,

    /// <summary>Three or four vent tubes fused at their feet, 1.4 to 3 m.</summary>
    VentCluster,

    /// <summary>A short vent tube, 0.8 m.</summary>
    VentShort,

    /// <summary>A bed of small vent tubes on a pale crust, the orchard's ground cover.</summary>
    VentBed,

    /// <summary>A giant vent tube for large caves, 10 m, its plume 5 m.</summary>
    VentGiant,

    /// <summary>Three giant vent tubes fused at their feet, 6 to 11 m.</summary>
    VentGiantCluster,

    /// <summary>The vent orchard's mineral chimneys over its seep, 1 to 2.6 m (the seep's glow and light at their top).</summary>
    SeepChimney,

    /// <summary>A bed of mussels and clams packed round the vent tubes' feet.</summary>
    SeepShells,

    /// <summary>A pale bacterial mat, the vent orchard's outer ground.</summary>
    SeepMat,

    /// <summary>The inverted grove's keystone (a ceiling piece, hanging from the plane y = 0): a braided trunk rooted in the ceiling and a bell-shaped crown of red filaments, about 7 m.</summary>
    GroveHanger,

    /// <summary>A smaller canopy hanger, about 4 m.</summary>
    GroveHangerSmall,

    /// <summary>The grove's pale root web over the ceiling between its trunks (a ceiling piece).</summary>
    GroveRootweb,

    /// <summary>Amber seed sacs hanging on cords below the crowns, 1 to 2.5 m (a ceiling piece).</summary>
    GroveSacs,

    /// <summary>The fallfield's pale litter of shed filaments and spores (a floor piece).</summary>
    GroveLitter,

    /// <summary>Pale cup-shaped catchers on short stalks, open to what falls.</summary>
    GroveCatchers,

    /// <summary>A low scavenger mound of gathered litter.</summary>
    GroveMound,

    /// <summary>The funnel garden's keystone (#10096): a living catch-basin 6 m tall, a fluted stem flaring into a gilled cup.</summary>
    FunnelGiant,

    /// <summary>A funnel 3.5 m tall.</summary>
    FunnelTall,

    /// <summary>A funnel 1.6 m tall.</summary>
    FunnelSmall,

    /// <summary>An old funnel split open into drooping petals, its gathered debris a small garden.</summary>
    FunnelUnfurled,

    /// <summary>A wet spill stain at a funnel's foot with a ring of pale drinker tubes in it.</summary>
    FunnelDrinkers,

    /// <summary>A patch of the debris garden: rot, moss cushions, pale sprouts and tiny cups.</summary>
    FunnelDebris,
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
    /// <summary>Whether a kind is the vent orchard's (an underground ecology's, grown round a cave's seeps) rather than overgrowth on the kit.</summary>
    internal static bool Vent(OvergrowthKind kind) => kind is OvergrowthKind.VentTall or OvergrowthKind.VentCluster or OvergrowthKind.VentShort or OvergrowthKind.VentBed
        or OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster or OvergrowthKind.SeepChimney or OvergrowthKind.SeepShells or OvergrowthKind.SeepMat;

    /// <summary>Whether a kind is the inverted grove's (an underground ecology's, under a ceiling close to the surface).</summary>
    internal static bool Grove(OvergrowthKind kind) => kind is OvergrowthKind.GroveHanger or OvergrowthKind.GroveHangerSmall or OvergrowthKind.GroveRootweb
        or OvergrowthKind.GroveSacs or OvergrowthKind.GroveLitter or OvergrowthKind.GroveCatchers or OvergrowthKind.GroveMound;

    /// <summary>Whether a kind belongs to one of the underground ecologies (the study switches grow them), not to the overgrowth on the kit.</summary>
    internal static bool Ecology(OvergrowthKind kind) => Vent(kind) || Grove(kind) || Funnel(kind);

    /// <summary>Whether a kind is the funnel garden's (an underground ecology's, under a shaft).</summary>
    internal static bool Funnel(OvergrowthKind kind) => kind is OvergrowthKind.FunnelGiant or OvergrowthKind.FunnelTall or OvergrowthKind.FunnelSmall
        or OvergrowthKind.FunnelUnfurled or OvergrowthKind.FunnelDrinkers or OvergrowthKind.FunnelDebris;

    /// <summary>Whether a kind is placed only turned about its up - one standing on a floor, or (the grove's) hanging from a ceiling (on the plane y = 0) rather than growing on a wall.</summary>
    internal static bool OnFloor(OvergrowthKind kind) => kind is OvergrowthKind.GrassTuft or OvergrowthKind.GrassStrip or OvergrowthKind.Mushrooms || Ecology(kind);
}
