using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>How much ruin a cave holds (#10097; owner, 2026-10-24: it varies by seed).</summary>
internal enum RuinSize
{
    /// <summary>A shell: the breach and a room or two.</summary>
    Shell,

    /// <summary>A buried wing: a few rooms and the vault.</summary>
    Wing,

    /// <summary>A large building of several storeys, heading two storeys down to its vault.</summary>
    Large,
}

/// <summary>
/// A ruin fitted into a karst cave (#10097): its modules on the cave's lattice (the breach first), its own voxels, the lights
/// its modules hang and the cells a body climbs through. The cave keeps its rock outside the ruin's cells; inside them the
/// ruin stands, its rooms dug out of the rock it is buried in, and its masonry gone wherever the cave's air runs through it
/// (a passage or chamber breaking in: the kit draws a wall burst open below as a breached wall, and leaves a floor that is
/// not whole). Its walked masonry - floors, ceilings, stairs - stands whatever the cave did, so its ways stay whole. Its
/// mouth is the cave's floor before the breach's broken side, where the passage meets it, joined to the breach's floor at
/// its broken wall's foot by a ramp of fallen stone through whatever rock lay between.
/// </summary>
internal sealed record CaveRuin(RuinSize Size, IReadOnlyList<PlacedModule> Modules, PlacedModule Breach, PlacedModule? Vault, DungeonVolume Building,
    IReadOnlyList<Vector3> Lights, HashSet<DungeonCell> Climbs, Vector3 WallFoot, Vector3 Mouth,
    IReadOnlyList<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> Joins)
{
    /// <summary>
    /// The ruin's footings (#10097's detail pass): the voxels of the cave's air under its lowest floors where the rock lay
    /// close under them, filled so the ruin sits on rock rather than hanging a sliver over it.
    /// </summary>
    internal IReadOnlySet<(int X, int Y, int Z)> Footings { get; init; } = new HashSet<(int X, int Y, int Z)>();

    /// <summary>Whether the cave's rock is closed against the ruin's outer walls (the detail pass's joins).</summary>
    internal bool Joined { get; init; }

    // Where the cave's rock meets the ruin's outer walls (the detail pass's joins): air the cave leaves open by less than this,
    // with a ruin's cell within this far one way and the cave's rock as near the other, is closed - the rock meets the masonry
    // rather than leaving a hairline gap.
    private const float JoinReach = 0.75f, JoinGap = 0.6f;
    /// <summary>The cave's air this far into a masonry voxel (metres of the cave's field) bursts it: the cave runs through.</summary>
    internal const float BurstOpen = 0.4f;

    /// <summary>
    /// The cave's rock overgrowing the burst walls (owner, 2026-10-24): the ruin's air voxels the rock has pushed into past a
    /// burst, each with its field (negative: how far inside the lobe).
    /// </summary>
    internal IReadOnlyDictionary<(int X, int Y, int Z), float> Overgrown { get; init; } = new Dictionary<(int X, int Y, int Z), float>();

    // The ramp from the broken wall to the mouth: this wide either side of its line and this tall over its floor, its fallen
    // stone this deep under it; it reaches this far on past both ends.
    private const float RampHalfWidth = 1.6f, RampHeight = 3.2f, RampDepth = 1.5f, RampBeyond = 0.75f;

    /// <summary>How open a ruin's own air reads in the combined field, and how solid its standing masonry.</summary>
    private const float RuinAir = 1f, RuinMasonry = -1f;

    /// <summary>Whether a place lies in one of the ruin's lattice cells.</summary>
    internal bool Inside(Vector3 p)
    {
        int cx = (int)MathF.Floor(p.X / ModuleCanvas.CellSize), cz = (int)MathF.Floor(p.Z / ModuleCanvas.CellSize);
        int cy = (int)MathF.Floor((p.Y - ModularDungeon.BaseY) / ModuleCanvas.StoreyHeight);
        return cells.Contains((cx, cy, cz));
    }

    private readonly HashSet<(int X, int Y, int Z)> cells = [.. Modules.SelectMany(m => m.Cells())];

    /// <summary>Whether the ruin's voxel at a place is masonry the cave has burst (its air runs through it there).</summary>
    /// Only a wall's voxels burst, between a storey's floor and ceiling rows: what is walked stands.
    internal bool Burst(Func<Vector3, float> cave, int x, int y, int z)
    {
        if (!Building.Contains(x, y, z) || Building.At(x, y, z) == BlockId.Air)
        {
            return false;
        }

        int across = ((x % ModuleCanvas.CellSize) + ModuleCanvas.CellSize) % ModuleCanvas.CellSize, along = ((z % ModuleCanvas.CellSize) + ModuleCanvas.CellSize) % ModuleCanvas.CellSize;
        int row = (y - ModularDungeon.BaseY) % ModuleCanvas.StoreyHeight;
        bool wall = across == 0 || across == ModuleCanvas.CellSize - 1 || along == 0 || along == ModuleCanvas.CellSize - 1;
        return wall && row > 0 && row < ModuleCanvas.StoreyHeight - 1 && cave(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) > BurstOpen;
    }

    /// <summary>How closed a hairline gap against the ruin's outer wall is made (metres of field).</summary>
    private const float DensityGap = 0.1f;

    /// <summary>
    /// Whether a place outside the ruin lies in a hairline gap between it and the cave's rock: one of its cells within
    /// <see cref="JoinReach"/> one way and the cave's rock as near the other (open air beyond is a space, not a gap).
    /// </summary>
    private bool Gap(Vector3 p, Func<Vector3, float> cave)
    {
        foreach (Vector3 axis in (ReadOnlySpan<Vector3>)[Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ])
        {
            if (Inside(p + (axis * JoinReach)) && cave(p - (axis * JoinReach)) <= 0f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How far a place lies from the ruin (#10097's detail pass, the placement map the cave's features are handed): from the
    /// nearest of its cells, its ramp's line (less the ramp's half width) and its mouth; 0 inside any.
    /// </summary>
    internal float Distance(Vector3 p)
    {
        float nearest = float.MaxValue;
        foreach ((int cx, int cy, int cz) in cells)
        {
            Vector3 min = new(cx * ModuleCanvas.CellSize, (cy * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY, cz * ModuleCanvas.CellSize);
            Vector3 max = min + new Vector3(ModuleCanvas.CellSize, ModuleCanvas.StoreyHeight, ModuleCanvas.CellSize);
            nearest = MathF.Min(nearest, Vector3.Distance(p, Vector3.Clamp(p, min, max)));
        }

        Vector3 along = Mouth - WallFoot;
        float t = along.LengthSquared() < 1e-6f ? 0f : Math.Clamp(Vector3.Dot(p - WallFoot, along) / along.LengthSquared(), 0f, 1f);
        nearest = MathF.Min(nearest, MathF.Max(0f, Vector3.Distance(p, WallFoot + (along * t) + new Vector3(0f, RampHeight * 0.5f, 0f)) - RampHalfWidth));
        return MathF.Min(nearest, MathF.Max(0f, Vector3.Distance(p, Mouth) - RampHalfWidth));
    }

    /// <summary>
    /// The fallen stone dressing its ramp (the detail pass hands the feature kit its place): debris either side of the ramp's
    /// line at these shares of its length, gravel down its middle; each on the ramp's floor.
    /// </summary>
    internal IEnumerable<OvergrowthPlacement> RampDressing()
    {
        Vector3 along = Mouth - WallFoot;
        Vector3 flat = new(along.X, 0f, along.Z);
        if (flat.LengthSquared() < 1e-6f)
        {
            yield break;
        }

        Vector3 forward = Vector3.Normalize(flat), side = new(-forward.Z, 0f, forward.X);
        foreach ((float t, float across, OvergrowthKind kind) in RampPieces)
        {
            Vector3 at = WallFoot + (along * t) + (side * across * RampHalfWidth);
            float pick = (t * 7.13f) - MathF.Floor(t * 7.13f);
            yield return new OvergrowthPlacement(kind, at - new Vector3(0f, 0.05f, 0f), across == 0f ? forward : side * MathF.Sign(across), 0.9f + (0.3f * pick), pick);
        }
    }

    private static readonly (float T, float Across, OvergrowthKind Kind)[] RampPieces =
    [
        (0.15f, 0.7f, OvergrowthKind.Debris), (0.35f, -0.7f, OvergrowthKind.Debris), (0.65f, 0.65f, OvergrowthKind.Debris), (0.85f, -0.6f, OvergrowthKind.Debris),
        (0.3f, 0f, OvergrowthKind.Gravel), (0.7f, 0f, OvergrowthKind.Gravel),
    ];

    /// <summary>Whether a place lies in the ramp's fallen stone, under its floor (its rock is rubble).</summary>
    internal bool UnderRamp(Vector3 p) => !Inside(p) && Ramp(p) is < 0f;

    /// <summary>
    /// The ramp's field at a place (positive open): open within its width over its floor - the floor running straight from
    /// the wall's foot to the mouth - and solid fallen stone under that floor; null away from it.
    /// </summary>
    private float? Ramp(Vector3 p)
    {
        Vector2 a = new(WallFoot.X, WallFoot.Z), b = new(Mouth.X, Mouth.Z), at = new(p.X, p.Z);
        float length = Vector2.Distance(a, b);
        if (length < 1e-3f)
        {
            return null;
        }

        Vector2 along = (b - a) / length;
        float t = Vector2.Dot(at - a, along);
        float lateral = MathF.Abs(Vector2.Dot(at - a, new Vector2(-along.Y, along.X)));
        if (t < -RampBeyond || t > length + RampBeyond || lateral > RampHalfWidth)
        {
            return null;
        }

        float floor = WallFoot.Y + ((Mouth.Y - WallFoot.Y) * Math.Clamp(t / length, 0f, 1f));
        float above = p.Y - floor;
        if (above < -RampDepth || above > RampHeight)
        {
            return null;
        }

        return above < 0f ? above : MathF.Min(MathF.Min(RampHalfWidth - lateral, above), RampHeight - above);
    }

    /// <summary>
    /// The cave with the ruin in it (positive open): outside the ruin's cells the cave's own field; inside them the ruin's
    /// air open, its standing masonry solid, and its burst masonry the cave's.
    /// </summary>
    internal Func<Vector3, float> Shape(Func<Vector3, float> cave) => p =>
    {
        if (!Inside(p))
        {
            float open = cave(p);
            // The footings under its floors; the rock closed against its outer walls.
            if (Footings.Contains(((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), (int)MathF.Floor(p.Z))))
            {
                open = MathF.Min(open, RuinMasonry * 0.5f);
            }
            else if (Joined && open > 0f && open < JoinGap && Gap(p, cave))
            {
                open = -DensityGap;
            }

            // The ramp is the cave's air over its floor and fallen stone under it.
            return Ramp(p) is float ramp ? (ramp < 0f ? MathF.Min(open, ramp) : MathF.Max(open, ramp)) : open;
        }

        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        if (!Building.Contains(x, y, z) || Building.At(x, y, z) == BlockId.Air)
        {
            return Overgrown.TryGetValue((x, y, z), out float lobe) ? lobe : MathF.Max(cave(p), RuinAir);
        }

        return Burst(cave, x, y, z) ? cave(p) : RuinMasonry;
    };

    /// <summary>Lays the ruin's standing masonry, its air and the rock overgrowing it (of the cave's rock there) into a sampled cave's voxels.</summary>
    internal void Lay(DungeonVolume volume, Func<Vector3, float> cave, Func<Vector3, BlockId> rock)
    {
        foreach (PlacedModule module in Modules)
        {
            int x0 = module.X * ModuleCanvas.CellSize, z0 = module.Z * ModuleCanvas.CellSize, y0 = (module.Y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY;
            for (int x = x0; x < x0 + (module.Width * ModuleCanvas.CellSize); x++)
                for (int y = y0; y < y0 + (module.Shape.Height * ModuleCanvas.StoreyHeight); y++)
                    for (int z = z0; z < z0 + (module.Depth * ModuleCanvas.CellSize); z++)
                    {
                        if (!volume.Contains(x, y, z) || !Building.Contains(x, y, z))
                        {
                            continue;
                        }

                        BlockId block = Building.At(x, y, z);
                        if (block == BlockId.Air)
                        {
                            volume.Set(x, y, z, Overgrown.ContainsKey((x, y, z)) ? rock(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f)) : BlockId.Air);
                        }
                        else if (!Burst(cave, x, y, z))
                        {
                            volume.Set(x, y, z, block);
                        }
                    }
        }
    }
}

/// <summary>
/// Fits a ruin into a karst cave (#10097; owner, 2026-10-24, atlas Combat &amp; Delving: the water-shaped cave first, the
/// ruin buried in its rock and broken into by its passages, how much of it by seed). The breach is set where one of the
/// cave's routes passes a lattice cell of open floor at a storey's height beside a cell buried in rock, its broken side
/// facing that passage; the ruin grows from its doors through the rock, preferring cells the rock buries.
/// </summary>
internal static class CaveRuins
{
    // How much ruin by seed: these shares of shells and wings (the rest large), and each size's rooms (beyond the breach),
    // how many storeys it heads down and whether it has its vault.
    private const double ShellShare = 0.3, WingShare = 0.45;
    private static (int Least, int Most, int Descend, bool Vault) Rooms(RuinSize size) => size switch
    {
        RuinSize.Shell => (1, 2, 0, false),
        RuinSize.Wing => (3, 6, 1, true),
        _ => (8, 13, 2, true),
    };

    // A breach's site: the cell it faces holds open floor within this far of the storey's floor, over at least this share
    // of its samples at body height, and a route point within this reach of its middle; the breach's own cell is at least
    // this share rock. So many sites are tried, best first, before a smaller ruin is.
    private const float SiteFloorRise = 2.5f, SiteOpenShare = 0.2f, SiteRouteReach = 6f, SiteBuried = 0.55f;
    private const int SitesTried = 12;

    /// <summary>How much a placement's buried share weighs in its favour while the ruin grows.</summary>
    private const double BuriedWeight = 4;

    /// <summary>The highest storey whose cells lie whole inside the cave's space.</summary>
    private static int TopStorey => ((KarstCave.ChunksY * 16) - ModularDungeon.BaseY) / ModuleCanvas.StoreyHeight - 1;

    /// <summary>The ruin fitted into a cave, or null when the cave has no site for one (or only fine voxels: a ruin is laid in 1 m voxels).</summary>
    internal static CaveRuin? Fit(Func<Vector3, float> cave, IReadOnlyList<Vector3> route, ulong seed)
    {
        DungeonRandom random = new(seed ^ 0x2017_0097UL);
        double roll = random.Unit();
        RuinSize first = roll < ShellShare ? RuinSize.Shell : roll < ShellShare + WingShare ? RuinSize.Wing : RuinSize.Large;
        List<(PlacedModule Breach, double Score)> sites = Sites(cave, route, random);
        for (RuinSize size = first; size >= RuinSize.Shell; size--)
        {
            (int least, int most, int descend, bool vault) = Rooms(size);
            foreach ((PlacedModule breach, double _) in sites.Where(s => s.Breach.Y >= descend).Take(SitesTried))
            {
                int rooms = random.Range(least, most);
                var grown = ModularDungeon.Ruin(random, breach, rooms, descend, vault, TopStorey, m => BuriedWeight * Buried(cave, m));
                if (grown.Failure is not null)
                {
                    continue;
                }

                (DungeonVolume building, IReadOnlyList<Vector3> lights, HashSet<DungeonCell> climbs) = ModularDungeon.StampRuin(grown.Placed, grown.Joins,
                    KarstCave.ChunksX, KarstCave.ChunksY, KarstCave.ChunksZ, random);
                (Vector3 foot, Vector3 mouth) = Mouth(cave, breach);
                return new CaveRuin(size, grown.Placed, breach, grown.Vault, building, lights, climbs, foot, mouth, grown.Joins);
            }
        }

        return null;
    }

    /// <summary>
    /// A chosen ruin finished in the cave's final field (#10097): the rock overgrowing its burst walls, and its footings.
    /// </summary>
    /// <param name="detail">Whether the detail pass runs (its footings and closed joins); false for its before.</param>
    internal static CaveRuin Finish(CaveRuin ruin, Func<Vector3, float> cave, ulong seed, bool detail = true) =>
        ruin with { Overgrown = Overgrow(ruin, cave, ruin.Joins, seed), Footings = detail ? Footings(ruin, cave) : new HashSet<(int X, int Y, int Z)>(), Joined = detail };

    /// <summary>The cave's air under a ruin's lowest floor is filled as a footing where the rock lies this close under it.</summary>
    private const float FootingGap = 2f;

    /// <summary>
    /// A ruin's footings: under every cell with no ruin cell below it, each column of the cave's air from its floor down to
    /// rock no further than <see cref="FootingGap"/> under it.
    /// </summary>
    private static HashSet<(int X, int Y, int Z)> Footings(CaveRuin ruin, Func<Vector3, float> cave)
    {
        HashSet<(int X, int Y, int Z)> cells = [.. ruin.Modules.SelectMany(m => m.Cells())];
        HashSet<(int X, int Y, int Z)> footings = [];
        foreach ((int cx, int cy, int cz) in cells.Where(c => !cells.Contains((c.X, c.Y - 1, c.Z))))
        {
            int y0 = (cy * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY;
            for (int x = cx * ModuleCanvas.CellSize; x < (cx + 1) * ModuleCanvas.CellSize; x++)
                for (int z = cz * ModuleCanvas.CellSize; z < (cz + 1) * ModuleCanvas.CellSize; z++)
                {
                    int gap = 0;
                    while (gap <= FootingGap && y0 - gap - 1 > 0 && cave(new Vector3(x + 0.5f, y0 - gap - 0.5f, z + 0.5f)) > 0f)
                    {
                        gap++;
                    }

                    if (gap == 0 || gap > FootingGap)
                    {
                        continue;
                    }

                    for (int k = 1; k <= gap; k++)
                    {
                        footings.Add((x, y0 - k, z));
                    }
                }
        }

        return footings;
    }

    /// <summary>
    /// The breach's possible sites, best first: every lattice cell and turn whose broken (cave) side faces a cell of open
    /// floor near the storey's floor with a route by it, the breach's own cell buried; scored by how buried it is and how
    /// near the route runs, ties by seed.
    /// </summary>
    private static List<(PlacedModule Breach, double Score)> Sites(Func<Vector3, float> cave, IReadOnlyList<Vector3> route, DungeonRandom random)
    {
        List<(PlacedModule, double)> sites = [];
        for (int y = 0; y <= TopStorey; y++)
            for (int z = 2; z < ModularDungeon.LatticeZ - 2; z++)
                for (int x = 2; x < ModularDungeon.LatticeX - 2; x++)
                {
                    PlacedModule cell = new(DungeonModules.Breach, 0, x, y, z);
                    double buried = Buried(cave, cell);
                    if (buried < SiteBuried)
                    {
                        continue;
                    }

                    for (int turns = 0; turns < 4; turns++)
                    {
                        PlacedModule breach = cell with { Turns = turns };
                        var broken = breach.Sockets().Single(s => s.Kind == SocketKind.Cave);
                        (int dx, int dz) = ModularDungeon.Step(broken.Face);
                        float floor = (y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY + 1;
                        Vector3 middle = new(((x + dx) * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f), floor + 1f, ((z + dz) * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f));
                        float nearest = route.Count == 0 ? float.MaxValue : route.Min(r => Vector3.Distance(r, middle));
                        if (nearest > SiteRouteReach || OpenFloor(cave, middle, floor) < SiteOpenShare)
                        {
                            continue;
                        }

                        sites.Add((breach, buried + (1 - (nearest / SiteRouteReach)) + (random.Unit() * 0.25)));
                    }
                }

        return [.. sites.OrderByDescending(s => s.Item2)];
    }

    /// <summary>
    /// A breach's broken wall's foot (on its floor, at the wall's middle) and the cave's floor before it: of the places out
    /// from that wall, the nearest whose floor lies within <see cref="SiteFloorRise"/> of the storey's floor and far enough
    /// out for a ramp no steeper than <see cref="RampRun"/> metres along per metre of rise; the cell beyond's middle if none.
    /// </summary>
    private static (Vector3 Foot, Vector3 Mouth) Mouth(Func<Vector3, float> cave, PlacedModule breach)
    {
        var broken = breach.Sockets().Single(s => s.Kind == SocketKind.Cave);
        (int dx, int dz) = ModularDungeon.Step(broken.Face);
        float floor = (breach.Y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY + 1;
        Vector3 wall = new((breach.X * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f) + (dx * ModuleCanvas.CellSize / 2f), floor,
            (breach.Z * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f) + (dz * ModuleCanvas.CellSize / 2f));
        for (float outward = 1f; outward <= ModuleCanvas.CellSize; outward += 0.5f)
        {
            Vector3 at = wall + new Vector3(dx * outward, 1f, dz * outward);
            if (cave(at) > 0f && StudyField.OnFloor(cave, at.X, at.Z, floor + SiteFloorRise) is Vector3 feet && MathF.Abs(feet.Y - floor) <= SiteFloorRise
                && outward >= MathF.Abs(feet.Y - floor) * RampRun)
            {
                return (wall, feet);
            }
        }

        return (wall, wall + new Vector3(dx * ModuleCanvas.CellSize / 2f, 0f, dz * ModuleCanvas.CellSize / 2f));
    }

    // The rock overgrowing the burst walls (owner, 2026-10-24) - the walls the cave's air has burst and the breach's own broken
    // wall - pushes on past each burst into the room in lobes out to
    // this reach from the burst's voxels, each place's own reach shaped by noise (this fine) between this share of it and
    // all of it; only where the cave's own field there is rock or open by less than this (it does not fill the cave's air
    // running on through the room); never within this far across of a lane from a doorway or the broken wall's foot to the
    // middle of its cell (in that storey), so the ruin's ways stay open.
    private const float OvergrowReach = 2.8f, OvergrowLeast = 0.3f, OvergrowNoise = 0.45f, OvergrowCaveOpen = 0.5f, OvergrowClear = 1.5f;

    /// <summary>The rock keeps this far across from the middle of a cell whose light the kit's relic stands by (out towards a corner).</summary>
    private const float RelicClear = 2.3f;

    /// <summary>The ruin's air voxels the cave's rock has overgrown past its bursts, each with its field (negative inside the lobe).</summary>
    private static Dictionary<(int X, int Y, int Z), float> Overgrow(CaveRuin ruin, Func<Vector3, float> cave,
        IReadOnlyList<(int X, int Y, int Z, ModuleFace Face, SocketKind Kind)> joins, ulong seed)
    {
        HashSet<(int X, int Y, int Z)> bursts = [];
        foreach (PlacedModule module in ruin.Modules)
        {
            int x0 = module.X * ModuleCanvas.CellSize, z0 = module.Z * ModuleCanvas.CellSize, y0 = (module.Y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY;
            for (int x = x0; x < x0 + (module.Width * ModuleCanvas.CellSize); x++)
                for (int y = y0; y < y0 + (module.Shape.Height * ModuleCanvas.StoreyHeight); y++)
                    for (int z = z0; z < z0 + (module.Depth * ModuleCanvas.CellSize); z++)
                    {
                        if (ruin.Burst(cave, x, y, z))
                        {
                            bursts.Add((x, y, z));
                        }
                    }
        }

        // The breach's own broken wall is the ruin's first burst: its wall slab's voxels between the corners, over its floor row.
        var broken = ruin.Breach.Sockets().Single(socket => socket.Kind == SocketKind.Cave);
        (int bdx, int bdz) = ModularDungeon.Step(broken.Face);
        int wx0 = ruin.Breach.X * ModuleCanvas.CellSize, wz0 = ruin.Breach.Z * ModuleCanvas.CellSize, wy0 = (ruin.Breach.Y * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY;
        int wallX = bdx > 0 ? wx0 + ModuleCanvas.CellSize - 1 : wx0, wallZ = bdz > 0 ? wz0 + ModuleCanvas.CellSize - 1 : wz0;
        for (int along = 1; along < ModuleCanvas.CellSize - 1; along++)
            for (int row = 1; row < ModuleCanvas.StoreyHeight - 1; row++)
            {
                bursts.Add(bdx != 0 ? (wallX, wy0 + row, wz0 + along) : (wx0 + along, wy0 + row, wallZ));
            }

        Dictionary<(int X, int Y, int Z), float> grown = [];

        // The lanes kept clear: from the broken wall's foot, and from each doorway either side, to the middle of the cell it
        // opens into - the ways through the ruin.
        Vector3 Middle(int cx, int cy, int cz) => new((cx * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f), (cy * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY + 1,
            (cz * ModuleCanvas.CellSize) + (ModuleCanvas.CellSize / 2f));
        List<(Vector3 From, Vector3 To)> lanes = [(ruin.WallFoot, Middle(ruin.Breach.X, ruin.Breach.Y, ruin.Breach.Z))];
        foreach ((int cx, int cy, int cz, ModuleFace face, SocketKind kind) in joins.Where(j => j.Kind == SocketKind.Door))
        {
            (int dx, int dz) = ModularDungeon.Step(face);
            Vector3 here = Middle(cx, cy, cz), door = here + new Vector3(dx * ModuleCanvas.CellSize / 2f, 0f, dz * ModuleCanvas.CellSize / 2f);
            lanes.Add((door, here));
            lanes.Add((door, Middle(cx + dx, cy, cz + dz)));
        }

        // And round each light's cell middle, where the kit stands its relic (DungeonKit: a relic in every cell a light hangs
        // in, out towards a corner): the room's light stays in the room.
        List<Vector3> relicCells = [.. ruin.Lights.Where(ruin.Inside).Select(light => Middle((int)MathF.Floor(light.X / ModuleCanvas.CellSize),
            (int)MathF.Floor((light.Y - ModularDungeon.BaseY) / ModuleCanvas.StoreyHeight), (int)MathF.Floor(light.Z / ModuleCanvas.CellSize)))];

        bool InLane(Vector3 p) => relicCells.Any(c => p.Y >= c.Y - 1f && p.Y < c.Y + ModuleCanvas.StoreyHeight - 1f && new Vector2(p.X - c.X, p.Z - c.Z).Length() < RelicClear) || lanes.Any(lane =>
        {
            if (p.Y < lane.From.Y - 1f || p.Y > lane.From.Y + ModuleCanvas.StoreyHeight - 1f)
            {
                return false;
            }

            Vector2 a = new(lane.From.X, lane.From.Z), b = new(lane.To.X, lane.To.Z), at = new(p.X, p.Z);
            float t = Math.Clamp(Vector2.Dot(at - a, b - a) / MathF.Max(1e-4f, Vector2.DistanceSquared(a, b)), 0f, 1f);
            return Vector2.Distance(at, a + ((b - a) * t)) < OvergrowClear;
        });

        int reach = (int)MathF.Ceiling(OvergrowReach);
        HashSet<(int X, int Y, int Z)> seen = [];
        foreach ((int bx, int by, int bz) in bursts)
            for (int dz = -reach; dz <= reach; dz++)
                for (int dy = -reach; dy <= reach; dy++)
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        (int X, int Y, int Z) at = (bx + dx, by + dy, bz + dz);
                        if (!seen.Add(at) || !ruin.Building.Contains(at.X, at.Y, at.Z) || ruin.Building.At(at.X, at.Y, at.Z) != BlockId.Air)
                        {
                            continue;
                        }

                        // Past the wall, not in it: the wall's own slab stays as the burst left it, for the kit's breached wall.
                        int across = ((at.X % ModuleCanvas.CellSize) + ModuleCanvas.CellSize) % ModuleCanvas.CellSize, along = ((at.Z % ModuleCanvas.CellSize) + ModuleCanvas.CellSize) % ModuleCanvas.CellSize;
                        if (across == 0 || across == ModuleCanvas.CellSize - 1 || along == 0 || along == ModuleCanvas.CellSize - 1)
                        {
                            continue;
                        }

                        Vector3 centre = new(at.X + 0.5f, at.Y + 0.5f, at.Z + 0.5f);
                        if (!ruin.Inside(centre) || cave(centre) > OvergrowCaveOpen || InLane(centre))
                        {
                            continue;
                        }

                        float nearest = float.MaxValue;
                        for (int ez = -reach; ez <= reach; ez++)
                            for (int ey = -reach; ey <= reach; ey++)
                                for (int ex = -reach; ex <= reach; ex++)
                                {
                                    if (bursts.Contains((at.X + ex, at.Y + ey, at.Z + ez)))
                                    {
                                        nearest = MathF.Min(nearest, MathF.Sqrt((ex * ex) + (ey * ey) + (ez * ez)));
                                    }
                                }

                        float own = OvergrowReach * (OvergrowLeast + ((1f - OvergrowLeast) * NaturalCavern.Noise(centre * OvergrowNoise, seed ^ 0x0E9UL)));
                        if (nearest < own)
                        {
                            grown[at] = nearest - own;
                        }
                    }

        return grown;
    }

    /// <summary>A ramp runs at least this far along per metre it rises (about 35 degrees).</summary>
    private const float RampRun = 1.45f;

    /// <summary>The share of a cell's samples at body height whose floor lies within <see cref="SiteFloorRise"/> of a storey's floor.</summary>
    private static double OpenFloor(Func<Vector3, float> cave, Vector3 middle, float floor)
    {
        int open = 0, total = 0;
        for (float dx = -3f; dx <= 3f; dx += 1.5f)
            for (float dz = -3f; dz <= 3f; dz += 1.5f)
            {
                total++;
                Vector3 at = middle + new Vector3(dx, 0f, dz);
                if (cave(at) <= 0f)
                {
                    continue;
                }

                Vector3 feet = StudyField.OnFloor(cave, at.X, at.Z, floor + SiteFloorRise);
                open += MathF.Abs(feet.Y - floor) <= SiteFloorRise ? 1 : 0;
            }

        return open / (double)total;
    }

    /// <summary>The share of a module's cells' interior samples that are rock: how buried it lies.</summary>
    private static double Buried(Func<Vector3, float> cave, PlacedModule module)
    {
        int rock = 0, total = 0;
        foreach ((int cx, int cy, int cz) in module.Cells())
        {
            float y0 = (cy * ModuleCanvas.StoreyHeight) + ModularDungeon.BaseY;
            for (float dx = 1.5f; dx < ModuleCanvas.CellSize; dx += 2.5f)
                for (float dy = 1.5f; dy < ModuleCanvas.StoreyHeight; dy += 2f)
                    for (float dz = 1.5f; dz < ModuleCanvas.CellSize; dz += 2.5f)
                    {
                        total++;
                        rock += cave(new Vector3((cx * ModuleCanvas.CellSize) + dx, y0 + dy, (cz * ModuleCanvas.CellSize) + dz)) <= 0f ? 1 : 0;
                    }
        }

        return total == 0 ? 0 : rock / (double)total;
    }
}
