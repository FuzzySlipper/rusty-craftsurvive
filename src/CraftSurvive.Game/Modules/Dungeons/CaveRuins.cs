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
    IReadOnlyList<Vector3> Lights, HashSet<DungeonCell> Climbs, Vector3 WallFoot, Vector3 Mouth)
{
    /// <summary>The cave's air this far into a masonry voxel (metres of the cave's field) bursts it: the cave runs through.</summary>
    internal const float BurstOpen = 0.4f;

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
            // The ramp is the cave's air over its floor and fallen stone under it.
            float open = cave(p);
            return Ramp(p) is float ramp ? (ramp < 0f ? MathF.Min(open, ramp) : MathF.Max(open, ramp)) : open;
        }

        int x = (int)MathF.Floor(p.X), y = (int)MathF.Floor(p.Y), z = (int)MathF.Floor(p.Z);
        if (!Building.Contains(x, y, z) || Building.At(x, y, z) == BlockId.Air)
        {
            return MathF.Max(cave(p), RuinAir);
        }

        return Burst(cave, x, y, z) ? cave(p) : RuinMasonry;
    };

    /// <summary>Lays the ruin's standing masonry and its air into a sampled cave's voxels.</summary>
    internal void Lay(DungeonVolume volume, Func<Vector3, float> cave)
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
                            volume.Set(x, y, z, BlockId.Air);
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
                return new CaveRuin(size, grown.Placed, breach, grown.Vault, building, lights, climbs, foot, mouth);
            }
        }

        return null;
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
