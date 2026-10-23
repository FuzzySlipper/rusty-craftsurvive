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

    /// <summary>The glass steppe's keystone (#10096): a tuft of stiff glassy feelers with glowing beaded tips, an organism buried beneath.</summary>
    SteppeTuft,

    /// <summary>A larger tuft of glass feelers.</summary>
    SteppeTuftLarge,

    /// <summary>The buried organism's milky back breaking the floor.</summary>
    SteppeBack,

    /// <summary>A drift of shed glass fragments in a hollow.</summary>
    SteppeGrit,

    /// <summary>A sentinel spire of tall glass rods (about 4.5 m) at the colony's edge.</summary>
    SteppeSpire,

    /// <summary>The ribbon thicket's keystone (#10096): a woody arch 6 m across and 5 m high (its span along x, the passage along z), ribbon curtains hanging from it, a knot nursery at its crown.</summary>
    RibbonArch,

    /// <summary>A wider arch, 9 m across and 7 m high.</summary>
    RibbonArchWide,

    /// <summary>A curtain of ribbons hanging about 3 m from a branch stub on the ceiling (a ceiling piece).</summary>
    RibbonCurtain,

    /// <summary>A shed bed: fallen ribbons curled on a dark wet rot mat, pale shoots rooting in it.</summary>
    RibbonShed,

    /// <summary>The veil forest's keystone (#10096): a porous lace fan about 5 m across on a twisted trunk 3 m tall, its face square to the draft (its +z downwind), buttress roots braced upwind.</summary>
    VeilFan,

    /// <summary>A great veil fan, about 8 m across on a trunk 4.6 m tall.</summary>
    VeilFanGreat,

    /// <summary>A row of young lace fans 1.4 m long on a root pad, facing downwind.</summary>
    VeilComb,

    /// <summary>A pale drift of what the fans sift, trailing downwind about 3 m from a trunk's lee.</summary>
    VeilDrift,

    /// <summary>A cluster of small pale puffballs and cups rooting in a drift.</summary>
    VeilPuffs,

    /// <summary>The cushion barrens' keystone (#10096): a soft dome of olive nap about 2.4 m across and 1 m high, a rust fringe at its foot, breath-pores on its crown.</summary>
    CushionHummock,

    /// <summary>The same in rust and ochre.</summary>
    CushionHummockRust,

    /// <summary>An enormous hummock of several swellings, about 5 m across and 2.6 m high.</summary>
    CushionGreat,

    /// <summary>An old hummock split open, its cleft (towards +z) lined with orange-red gill-like fins.</summary>
    CushionSplit,

    /// <summary>A thin lobed film of nap creeping over the floor, about 2 m across.</summary>
    CushionMat,

    /// <summary>Small young hummocks budding from a scrap of mat.</summary>
    CushionBuds,

    /// <summary>The ivory heath's keystone (#10096): a swollen ivory storage bulb 1 m high crowned by a stiff bristle fan to 1.9 m.</summary>
    IvoryBulb,

    /// <summary>A great storage bulb, 2.2 m high, its bristles to about 4 m.</summary>
    IvoryBulbGreat,

    /// <summary>An old bulb weathered to a pitted shell, torn open on its +z side.</summary>
    IvoryHollow,

    /// <summary>Young bristle fans from ivory rims at the floor, scattered over about 1.4 m.</summary>
    IvoryTufts,

    /// <summary>A white salt crust of frost-like mineral flowers.</summary>
    IvoryBloom,

    /// <summary>Small ivory seed pods gathered in a hollow.</summary>
    IvoryPods,

    /// <summary>
    /// The geological kit's drip formations (#10305; owner, 2026-10-23: authored, instanced pieces where the cave's drip
    /// says): a calcite stalactite hanging 1.2 m from a ceiling (the plane y = 0), its root sunk into the rock.
    /// </summary>
    Stalactite,

    /// <summary>The same, 2.6 m.</summary>
    StalactiteLong,

    /// <summary>A stout calcite stalagmite rising 0.9 m from a splash-flared foot.</summary>
    Stalagmite,

    /// <summary>The same, 2.2 m.</summary>
    StalagmiteTall,

    /// <summary>A column 3 m from floor (y = 0) to ceiling (y = 3), where a stalactite and stalagmite grew together; scaled to its gap.</summary>
    Column,

    /// <summary>A calcite drapery curtain 2.4 m along x hanging about 1.3 m from a ceiling line, its +z facing down the ceiling's slope.</summary>
    Drapery,
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
    /// <summary>How far an overgrowth is turned about its surface's normal, at most, either way (radians), so repeats differ.</summary>
    private const float MostTwist = 0.12f;

    /// <summary>How a placement turns its piece (#10304 moved here so the bank can place its trunks): up its normal, facing out.</summary>
    internal static Quaternion Turn(OvergrowthPlacement p)
    {
        Vector3 up = Vector3.Normalize(p.Up ?? Vector3.UnitY), facing = Vector3.Normalize(p.Out);
        Vector3 y, z;
        if (OvergrowthPlacement.OnFloor(p.Kind) || MathF.Abs(Vector3.Dot(up, facing)) > 0.98f)
        {
            y = up;
            Vector3 flat = facing - (y * Vector3.Dot(facing, y));
            z = flat.LengthSquared() > 1e-6f ? Vector3.Normalize(flat) : Vector3.Normalize(Vector3.Cross(Vector3.UnitX, y));
        }
        else
        {
            z = facing;
            y = Vector3.Normalize(up - (z * Vector3.Dot(up, z)));
        }

        // System.Numerics transforms row vectors: the matrix's rows are where the frame's axes go.
        Vector3 x = Vector3.Cross(y, z);
        Quaternion frame = Quaternion.CreateFromRotationMatrix(new Matrix4x4(x.X, x.Y, x.Z, 0f, y.X, y.Y, y.Z, 0f, z.X, z.Y, z.Z, 0f, 0f, 0f, 0f, 1f));
        // Twisted about its up: a drape keeps hanging and a mound keeps its foot on the floor.
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(y, (p.Pick - 0.5f) * 2f * MostTwist) * frame);
    }

    /// <summary>
    /// The colliding trunks of a kind (#10096; #10304 moved here so the bank can read them): each a closed prism round a foot
    /// in the piece's frame (metres: its offset, radius and height); none for kinds that do not collide.
    /// </summary>
    internal static (float X, float Z, float Radius, float Height)[] Trunks(OvergrowthKind kind) => kind switch
    {
        OvergrowthKind.VentTall => [(0f, 0f, 0.32f, 2.6f)],
        OvergrowthKind.VentCluster => [(0f, 0f, 0.33f, 3f), (0.42f, 0.2f, 0.27f, 2.1f), (-0.3f, 0.38f, 0.22f, 1.6f), (0.1f, -0.42f, 0.2f, 1.4f)],
        OvergrowthKind.VentGiant => [(0f, 0f, 1.1f, 10f)],
        OvergrowthKind.VentGiantCluster => [(0f, 0f, 1.15f, 11f), (1.6f, 0.8f, 0.95f, 8f), (-1.1f, 1.4f, 0.78f, 6f)],
        OvergrowthKind.SeepChimney => [(0f, 0f, 0.45f, 2.6f), (0.55f, 0.25f, 0.32f, 1.6f), (-0.35f, 0.5f, 0.27f, 1f)],
        // A funnel collides as its stem, up to where its cup flares out over the player's head.
        OvergrowthKind.FunnelGiant => [(0f, 0f, 0.85f, 3f)],
        OvergrowthKind.FunnelTall => [(0f, 0f, 0.5f, 2f)],
        OvergrowthKind.SteppeSpire => [(0f, 0f, 0.4f, 4f)],
        // An arch collides at its feet against the walls (its ribbons the player pushes through).
        OvergrowthKind.RibbonArch => [(-2.9f, 0f, 0.45f, 2f), (2.9f, 0f, 0.45f, 2f)],
        OvergrowthKind.RibbonArchWide => [(-4.35f, 0f, 0.6f, 2.5f), (4.35f, 0f, 0.6f, 2.5f)],
        OvergrowthKind.VeilFan => [(0f, 0f, 0.26f, 3f)],
        OvergrowthKind.VeilFanGreat => [(0f, 0f, 0.34f, 4.6f)],
        // The barrens' domes collide as low prisms inside their swell: the small ones a step the traveller climbs over.
        OvergrowthKind.CushionHummock or OvergrowthKind.CushionHummockRust => [(0f, 0f, 0.95f, 0.85f)],
        OvergrowthKind.CushionSplit => [(0f, 0f, 1f, 0.95f)],
        OvergrowthKind.CushionGreat => [(0f, 0f, 2f, 2.3f)],
        OvergrowthKind.IvoryBulb => [(0f, 0f, 0.4f, 0.9f)],
        OvergrowthKind.IvoryBulbGreat => [(0f, 0f, 0.9f, 2f)],
        OvergrowthKind.IvoryHollow => [(0f, 0f, 0.55f, 1.2f)],
        // The cave formations (#10305): a column at its waist, a tall stalagmite up its stout lower part (the short ones and the
        // stalactites, hung above head height, do not collide).
        OvergrowthKind.Column => [(0f, 0f, 0.3f, 3f)],
        OvergrowthKind.StalagmiteTall => [(0f, 0f, 0.42f, 1.5f)],
        _ => [],
    };

    /// <summary>Whether a kind is the vent orchard's (an underground ecology's, grown round a cave's seeps) rather than overgrowth on the kit.</summary>
    internal static bool Vent(OvergrowthKind kind) => kind is OvergrowthKind.VentTall or OvergrowthKind.VentCluster or OvergrowthKind.VentShort or OvergrowthKind.VentBed
        or OvergrowthKind.VentGiant or OvergrowthKind.VentGiantCluster or OvergrowthKind.SeepChimney or OvergrowthKind.SeepShells or OvergrowthKind.SeepMat;

    /// <summary>Whether a kind is the inverted grove's (an underground ecology's, under a ceiling close to the surface).</summary>
    internal static bool Grove(OvergrowthKind kind) => kind is OvergrowthKind.GroveHanger or OvergrowthKind.GroveHangerSmall or OvergrowthKind.GroveRootweb
        or OvergrowthKind.GroveSacs or OvergrowthKind.GroveLitter or OvergrowthKind.GroveCatchers or OvergrowthKind.GroveMound;

    /// <summary>Whether a kind belongs to one of the underground ecologies (the study switches grow them), not to the overgrowth on the kit.</summary>
    internal static bool Ecology(OvergrowthKind kind) => Vent(kind) || Grove(kind) || Funnel(kind) || Steppe(kind) || Ribbon(kind) || Veil(kind) || Cushion(kind) || Ivory(kind);

    /// <summary>Whether a kind is the ivory heath's (an underground ecology's, on dead flowstone floors).</summary>
    internal static bool Ivory(OvergrowthKind kind) => kind is OvergrowthKind.IvoryBulb or OvergrowthKind.IvoryBulbGreat or OvergrowthKind.IvoryHollow
        or OvergrowthKind.IvoryTufts or OvergrowthKind.IvoryBloom or OvergrowthKind.IvoryPods;

    /// <summary>Whether a kind is the cushion barrens' (an underground ecology's, on the rubble slopes of fallen-in chambers).</summary>
    internal static bool Cushion(OvergrowthKind kind) => kind is OvergrowthKind.CushionHummock or OvergrowthKind.CushionHummockRust or OvergrowthKind.CushionGreat
        or OvergrowthKind.CushionSplit or OvergrowthKind.CushionMat or OvergrowthKind.CushionBuds;

    /// <summary>Whether a kind is the veil forest's (an underground ecology's, on the draft's line between a cave's openings).</summary>
    internal static bool Veil(OvergrowthKind kind) => kind is OvergrowthKind.VeilFan or OvergrowthKind.VeilFanGreat or OvergrowthKind.VeilComb
        or OvergrowthKind.VeilDrift or OvergrowthKind.VeilPuffs;

    /// <summary>Whether a kind is the ribbon thicket's (an underground ecology's, across tall wet passages).</summary>
    internal static bool Ribbon(OvergrowthKind kind) => kind is OvergrowthKind.RibbonArch or OvergrowthKind.RibbonArchWide or OvergrowthKind.RibbonCurtain or OvergrowthKind.RibbonShed;

    /// <summary>Whether a kind is the glass steppe's (an underground ecology's, on dry open floors).</summary>
    internal static bool Steppe(OvergrowthKind kind) => kind is OvergrowthKind.SteppeTuft or OvergrowthKind.SteppeTuftLarge or OvergrowthKind.SteppeBack
        or OvergrowthKind.SteppeGrit or OvergrowthKind.SteppeSpire;

    /// <summary>Whether a kind is the funnel garden's (an underground ecology's, under a shaft).</summary>
    internal static bool Funnel(OvergrowthKind kind) => kind is OvergrowthKind.FunnelGiant or OvergrowthKind.FunnelTall or OvergrowthKind.FunnelSmall
        or OvergrowthKind.FunnelUnfurled or OvergrowthKind.FunnelDrinkers or OvergrowthKind.FunnelDebris;

    /// <summary>Whether a kind is placed only turned about its up - one standing on a floor, or (the grove's) hanging from a ceiling (on the plane y = 0) rather than growing on a wall.</summary>
    internal static bool OnFloor(OvergrowthKind kind) => kind is OvergrowthKind.GrassTuft or OvergrowthKind.GrassStrip or OvergrowthKind.Mushrooms || Ecology(kind) || Formation(kind);

    /// <summary>Whether a kind is one of the geological kit's formations (#10305) rather than growth.</summary>
    internal static bool Formation(OvergrowthKind kind) => kind is OvergrowthKind.Stalactite or OvergrowthKind.StalactiteLong or OvergrowthKind.Stalagmite
        or OvergrowthKind.StalagmiteTall or OvergrowthKind.Column or OvergrowthKind.Drapery;
}
