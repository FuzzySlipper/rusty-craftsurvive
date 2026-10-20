using System.Numerics;
using CraftSurvive.Game.Modules.Content;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>A piece of the dungeon kit (#10089; the ancients' language, docs/style-references/ancients).</summary>
internal enum KitPiece
{
    Floor,
    Vault,
    Wall,
    WallDoor,
    Corner,

    /// <summary>A wall with an overscaled scalloped shell alcove and a seat (Whim).</summary>
    WallAlcove,

    /// <summary>A wall with a round ceramic mosaic in a brass ring (Whim's ringed moons).</summary>
    WallRoundel,

    /// <summary>A stone sphere hung from the vault on one rib (Whim), in a hall's middle.</summary>
    Sphere,

    /// <summary>The relic: a row of straight dark rods on a plinth, untouched by the erosion (Whim's discordant technology).</summary>
    Relic,

    /// <summary>The relic's cold glow between its rods, drawn in its own emissive material: the source of the relic's light.</summary>
    RelicGlow,

    /// <summary>A corner rib snapped off at half its height, a fallen piece at its foot (Bones).</summary>
    CornerBroken,

    /// <summary>A stair flight drawn over the voxel flight that still carries the walking.</summary>
    Stair,
}

/// <summary>
/// One kit piece set in a dungeon, in layout metres: which piece, the base centre of its cell (the bottom of the cell's
/// floor block), its turn about the vertical (radians), and how far it stretches across (x) and through (z) in its own
/// frame where a hall runs on into the next cell.
/// </summary>
internal readonly record struct KitPlacement(KitPiece Piece, Vector3 Origin, float Turn, Vector2 Stretch, Vector2 Shift);

/// <summary>
/// Fits the dungeon kit to a modular dungeon (#10089): every building cell's floor, ceiling and walls, found from the
/// finished volume, become kit pieces, and the voxels they stand in leave the drawn volume. What the kit has no piece
/// for yet (stairs, rubble, balconies, broken walls) stays voxels. The walkable volume is untouched, so routes are
/// checked as before; the pieces collide as static meshes.
/// </summary>
internal static class DungeonKit
{
    private const int Cell = ModuleCanvas.CellSize, Storey = ModuleCanvas.StoreyHeight;

    /// <summary>The voxels a piece's place must hold for the piece: a floor or ceiling whole, a wall nearly (a door's hole is open).</summary>
    private const double WholeShare = 0.95, WallShare = 0.9;

    /// <summary>A piece's own frame: its interior is this wide (the cell less its two walls).</summary>
    private const float PieceInterior = Cell - 2;

    // Variety (#10089): the share of plain walls that are alcove or roundel walls instead, and of corner ribs that are
    // broken; a building module at least this many cells across hangs a sphere in its middle.
    private const double AlcoveShare = 0.12, RoundelShare = 0.08, BrokenShare = 0.2;
    private const int SphereCells = 2;

    // A relic stands this far out from its cell's middle towards a corner (clear of the middle the routes aim at and the
    // doors in the walls' middles), its glow this high over the floor.
    private const float RelicOut = 1.6f, GlowHeight = 1.05f;

    // The stairs the kit draws: a module's name, and its voxel flight's foot corner in the module's canvas
    // (DungeonModules.Stair and Gallery, ModuleCanvas.StairsNorth).
    private static readonly (string Module, int X, int Z)[] Flights = [("stair", 2, 1), ("gallery", (2 * Cell) - 4, (2 * Cell) - 5 - Storey)];

    /// <summary>
    /// The kit fitted to a dungeon, its volume less the voxels the pieces stand in, and where its relics glow (layout
    /// metres): one relic in every building cell a light of the layout hangs in, which then lights from its relic.
    /// </summary>
    internal static (IReadOnlyList<KitPlacement> Pieces, DungeonVolume Drawn, IReadOnlyList<Vector3> Relics) Fit(DungeonVolume volume,
        IReadOnlyList<PlacedModule> modules, IReadOnlyList<Vector3> lights, ulong seed)
    {
        DungeonVolume drawn = Copy(volume);
        List<KitPlacement> pieces = [];
        List<Vector3> relics = [];
        List<(int X0, int X1, int Y0, int Y1, int Z0, int Z1)> cleared = [];
        foreach (PlacedModule module in modules.Where(m => m.Shape.Kind == ModuleKind.Building))
        {
            foreach ((int cx, int cy, int cz) in module.Cells())
            {
                int ox = cx * Cell, oy = (cy * Storey) + ModularDungeon.BaseY, oz = cz * Cell;
                Vector3 origin = new(ox + (Cell / 2f), oy, oz + (Cell / 2f));
                bool south = Wall(volume, ox, oy, oz, ModuleFace.South, out bool southDoor);
                bool north = Wall(volume, ox, oy, oz, ModuleFace.North, out bool northDoor);
                bool west = Wall(volume, ox, oy, oz, ModuleFace.West, out bool westDoor);
                bool east = Wall(volume, ox, oy, oz, ModuleFace.East, out bool eastDoor);
                // A floor or ceiling runs to the cell's edge where no wall stands there.
                int x0 = west ? 1 : 0, x1 = east ? Cell - 2 : Cell - 1, z0 = south ? 1 : 0, z1 = north ? Cell - 2 : Cell - 1;
                foreach ((KitPiece piece, int y) in new[] { (KitPiece.Floor, oy), (KitPiece.Vault, oy + Storey - 1) })
                {
                    if (Share(volume, ox + x0, ox + x1, y, y, oz + z0, oz + z1) < WholeShare)
                    {
                        continue;
                    }

                    if (piece == KitPiece.Floor)
                    {
                        // The floor's voxels stay, just under the floor piece, so the floor walks as it did and stays one surface
                        // with any voxel floor beside it. The piece spans the whole cell, under the walls' curved recesses too.
                        pieces.Add(Spread(piece, origin, 0, Cell - 1, 0, Cell - 1));
                    }
                    else
                    {
                        // The vault rises into the ceiling's voxels: they go.
                        pieces.Add(Spread(piece, origin, x0, x1, z0, z1));
                        cleared.Add((ox + x0, ox + x1, y, y, oz + z0, oz + z1));
                    }
                }

                foreach ((ModuleFace face, bool stands, bool door) in new[] { (ModuleFace.South, south, southDoor), (ModuleFace.North, north, northDoor),
                    (ModuleFace.West, west, westDoor), (ModuleFace.East, east, eastDoor) })
                {
                    if (!stands)
                    {
                        continue;
                    }

                    // A wall runs on over the cell's corner column where no wall crosses it there (a hall's long side).
                    (bool before, bool after) = face switch
                    {
                        ModuleFace.South => (!west, !east),
                        ModuleFace.North => (!east, !west),
                        ModuleFace.West => (!north, !south),
                        _ => (!south, !north),
                    };
                    double pick = Pick(seed, cx, cy, cz, (int)face);
                    KitPiece kind = door ? KitPiece.WallDoor : pick < AlcoveShare ? KitPiece.WallAlcove : pick < AlcoveShare + RoundelShare ? KitPiece.WallRoundel : KitPiece.Wall;
                    pieces.Add(WallPiece(kind, origin, face, before, after));
                    cleared.Add(WallSlab(ox, oy, oz, face, before, after));
                }

                foreach ((bool a, bool b, float turn) in new[] { (south, west, 0f), (west, north, MathF.PI / 2), (north, east, MathF.PI), (east, south, -MathF.PI / 2) })
                {
                    if (a && b)
                    {
                        KitPiece corner = Pick(seed, cx, cy, cz, 10 + (int)(turn * 2f)) < BrokenShare ? KitPiece.CornerBroken : KitPiece.Corner;
                        pieces.Add(new KitPlacement(corner, origin, turn, Vector2.One, Vector2.Zero));
                    }
                }

                // A relic where the layout hangs a light over this cell's floor, out towards a corner.
                if (lights.Any(l => l.X >= ox && l.X < ox + Cell && l.Z >= oz && l.Z < oz + Cell && l.Y >= oy && l.Y < oy + Storey))
                {
                    // The four spots out towards the corners, from one the seed picks: the first on clear floor (not in a
                    // stair's flight, a balcony's void or a heap) takes the relic.
                    int first = (int)(Pick(seed, cx, cy, cz, 20) * 4);
                    for (int k = 0; k < 4; k++)
                    {
                        int corner = (first + k) % 4;
                        float sx = corner % 2 == 0 ? -1f : 1f, sz = corner < 2 ? -1f : 1f;
                        Vector3 at = origin + new Vector3(sx * RelicOut, 0f, sz * RelicOut);
                        if (!ClearFloor(volume, at, oy))
                        {
                            continue;
                        }

                        float turn = MathF.Atan2(-sx, -sz);
                        pieces.Add(new KitPlacement(KitPiece.Relic, at, turn, Vector2.One, Vector2.Zero));
                        pieces.Add(new KitPlacement(KitPiece.RelicGlow, at, turn, Vector2.One, Vector2.Zero));
                        relics.Add(at + new Vector3(0f, GlowHeight, 0f));
                        break;
                    }
                }
            }

            int width = module.Width, depth = module.Depth;
            Vector3 moduleCentre = new((module.X * Cell) + (width * Cell / 2f), (module.Y * Storey) + ModularDungeon.BaseY, (module.Z * Cell) + (depth * Cell / 2f));
            if (module.Shape.Height == 1 && Math.Min(width, depth) >= SphereCells)
            {
                pieces.Add(new KitPlacement(KitPiece.Sphere, moduleCentre, 0f, Vector2.One, Vector2.Zero));
            }

            foreach ((string _, int fx, int fz) in Flights.Where(f => f.Module == module.Shape.Name))
            {
                // The flight's foot corner, turned as the module was stamped (ModularDungeon.Turn), and its climb along the turned north.
                (float wx, float wz) = TurnPoint(fx, fz, module.Shape.Width * Cell, module.Shape.Depth * Cell, module.Turns);
                Vector3 foot = new((module.X * Cell) + wx, (module.Y * Storey) + ModularDungeon.BaseY, (module.Z * Cell) + wz);
                pieces.Add(new KitPlacement(KitPiece.Stair, foot, module.Turns * MathF.PI / 2f, Vector2.One, Vector2.Zero));
            }
        }

        foreach ((int x0, int x1, int y0, int y1, int z0, int z1) in cleared)
        {
            drawn.Fill(new DungeonCell(x0, y0, z0), new DungeonCell(x1, y1, z1), BlockId.Air);
        }

        return (pieces, drawn, relics);
    }

    /// <summary>Whether a relic's spot has floor under it and the room's headroom over it, a metre round.</summary>
    private static bool ClearFloor(DungeonVolume volume, Vector3 at, int oy)
    {
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                int x = (int)MathF.Floor(at.X) + dx, z = (int)MathF.Floor(at.Z) + dz;
                if (volume.At(x, oy, z) == BlockId.Air)
                {
                    return false;
                }

                for (int y = oy + 1; y <= oy + RelicHeadroom; y++)
                {
                    if (volume.At(x, y, z) != BlockId.Air)
                    {
                        return false;
                    }
                }
            }

        return true;
    }

    /// <summary>The air a relic needs over its floor, in voxels.</summary>
    private const int RelicHeadroom = 3;

    /// <summary>A point of a module's canvas (continuous metres) after the module's quarter turns, as ModularDungeon.Turn turns its cells.</summary>
    private static (float X, float Z) TurnPoint(float x, float z, float width, float depth, int turns) => (turns % 4) switch
    {
        1 => (z, width - x),
        2 => (width - x, depth - z),
        3 => (depth - z, x),
        _ => (x, z),
    };

    /// <summary>A placement's own draw, from the seed and where it is.</summary>
    private static double Pick(ulong seed, int x, int y, int z, int salt)
    {
        ulong h = seed ^ ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) ^ ((ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL) ^ ((ulong)(uint)z * 0x165667B19E3779F9UL) ^ ((ulong)(uint)salt * 0xD6E8FEB86659FD93UL);
        h ^= h >> 33;
        h *= 0xFF51AFD7ED558CCDUL;
        h ^= h >> 33;
        return (h >> 11) / (double)(1UL << 53);
    }

    /// <summary>
    /// Whether a piece collides. The floor's voxels stay under the floor piece and carry the floor, so the floor piece only
    /// draws: colliding it too would stack two floors a few centimetres apart in every column of the navigation. The vault
    /// draws only for the same reason (its top meets the floor of any storey above), as does the stair over its voxel
    /// flight; the sphere hangs over heads, the relic is a little thing in a corner (its plinth and caps would stack
    /// walkable layers over the floor), and its glow is light.
    /// </summary>
    internal static bool Collides(KitPiece piece) => piece is not (KitPiece.Floor or KitPiece.Vault or KitPiece.Stair or KitPiece.Sphere or KitPiece.Relic or KitPiece.RelicGlow);

    /// <summary>
    /// The piece whose shape a piece collides as: an alcove or roundel wall as the plain wall, a broken rib as the whole
    /// one, so the variety is seen but the routes meet the shapes they were checked against.
    /// </summary>
    internal static KitPiece Collider(KitPiece piece) => piece switch
    {
        KitPiece.WallAlcove or KitPiece.WallRoundel => KitPiece.Wall,
        KitPiece.CornerBroken => KitPiece.Corner,
        _ => piece,
    };

    /// <summary>Where a placed piece stands in the dungeon's session: its position (its cell's base centre, shifted in its own frame), turn and stretch.</summary>
    internal static (Vector3 At, Quaternion Turn, Vector3 Scale) Place(KitPlacement placement)
    {
        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, placement.Turn);
        Vector3 at = DungeonCollision.InSession(placement.Origin) + Vector3.Transform(new Vector3(placement.Shift.X, 0f, placement.Shift.Y), turn);
        return (at, turn, new Vector3(placement.Stretch.X, 1f, placement.Stretch.Y));
    }

    /// <summary>The manifest's name for a piece.</summary>
    internal static string Name(KitPiece piece) => piece switch
    {
        KitPiece.Floor => "floor",
        KitPiece.Vault => "vault",
        KitPiece.Wall => "wall",
        KitPiece.WallDoor => "wall-door",
        KitPiece.WallAlcove => "wall-alcove",
        KitPiece.WallRoundel => "wall-roundel",
        KitPiece.Sphere => "sphere",
        KitPiece.Relic => "relic",
        KitPiece.RelicGlow => "relic-glow",
        KitPiece.CornerBroken => "corner-broken",
        KitPiece.Stair => "stair",
        _ => "corner",
    };

    /// <summary>A floor or ceiling over a cell's voxels x0..x1, z0..z1: the piece's interior stretched and shifted onto them.</summary>
    private static KitPlacement Spread(KitPiece piece, Vector3 origin, int x0, int x1, int z0, int z1)
    {
        Vector2 stretch = new((x1 - x0 + 1) / PieceInterior, (z1 - z0 + 1) / PieceInterior);
        Vector2 shift = new(((x0 + x1 + 1) / 2f) - (Cell / 2f), ((z0 + z1 + 1) / 2f) - (Cell / 2f));
        return new KitPlacement(piece, origin, 0f, stretch, shift);
    }

    /// <summary>
    /// A wall on a face, turned from the piece's own south edge (-z) and stretched over a corner column at either end that
    /// no crossing wall takes (seen from the room: before is its left end, after its right).
    /// </summary>
    private static KitPlacement WallPiece(KitPiece piece, Vector3 origin, ModuleFace face, bool before, bool after)
    {
        float turn = Turn(face);
        float stretch = (PieceInterior + (before ? 1 : 0) + (after ? 1 : 0)) / PieceInterior;
        // In the piece's frame the room looks towards +z; its left (before) is -x.
        float shift = ((after ? 1 : 0) - (before ? 1 : 0)) / 2f;
        return new KitPlacement(piece, origin, turn, new Vector2(stretch, 1f), new Vector2(shift, 0f));
    }

    /// <summary>The turn that brings a piece's south edge (-z) to a face.</summary>
    internal static float Turn(ModuleFace face) => face switch
    {
        ModuleFace.South => 0f,
        ModuleFace.North => MathF.PI,
        ModuleFace.East => -MathF.PI / 2,
        _ => MathF.PI / 2,
    };

    /// <summary>
    /// The voxels a wall on a face stands in: its 1 m slab at the cell's edge over its span, from over the floor to the storey's
    /// top. Its floor-level row stays, so the floor runs on under the wall's curved recess and through its door.
    /// </summary>
    private static (int X0, int X1, int Y0, int Y1, int Z0, int Z1) WallSlab(int ox, int oy, int oz, ModuleFace face, bool before, bool after)
    {
        int top = oy + Storey - 1;
        oy += 1;
        // Before and after are seen from the room; along the face that is -x..+x for south, +x..-x for north, and so on.
        return face switch
        {
            ModuleFace.South => (ox + (before ? 0 : 1), ox + Cell - (after ? 1 : 2), oy, top, oz, oz),
            ModuleFace.North => (ox + (after ? 0 : 1), ox + Cell - (before ? 1 : 2), oy, top, oz + Cell - 1, oz + Cell - 1),
            ModuleFace.West => (ox, ox, oy, top, oz + (after ? 0 : 1), oz + Cell - (before ? 1 : 2)),
            _ => (ox + Cell - 1, ox + Cell - 1, oy, top, oz + (before ? 0 : 1), oz + Cell - (after ? 1 : 2)),
        };
    }

    /// <summary>Whether a wall stands whole on a cell's face (its middle span, the storey's height), and whether a door is cut in it.</summary>
    private static bool Wall(DungeonVolume volume, int ox, int oy, int oz, ModuleFace face, out bool door)
    {
        (int x0, int x1, int _, int _, int z0, int z1) = WallSlab(ox, oy, oz, face, false, false);
        int doorLow = (Cell / 2) - 1, doorHigh = Cell / 2;
        (int dx0, int dx1, int dz0, int dz1) = face is ModuleFace.South or ModuleFace.North
            ? (ox + doorLow, ox + doorHigh, z0, z1)
            : (x0, x1, oz + doorLow, oz + doorHigh);
        door = Share(volume, dx0, dx1, oy + 1, oy + 3, dz0, dz1) == 0d;
        int solid = 0, total = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = oy + 1; y < oy + Storey - 1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    bool inDoor = door && x >= dx0 && x <= dx1 && z >= dz0 && z <= dz1 && y <= oy + 3;
                    if (inDoor)
                    {
                        continue;
                    }

                    total++;
                    solid += Building(volume.At(x, y, z)) ? 1 : 0;
                }

        return total > 0 && solid >= WallShare * total;
    }

    /// <summary>The share of a box of voxels that is building (not air, not rock).</summary>
    private static double Share(DungeonVolume volume, int x0, int x1, int y0, int y1, int z0, int z1)
    {
        int solid = 0, total = 0;
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
                for (int z = z0; z <= z1; z++)
                {
                    total++;
                    solid += Building(volume.At(x, y, z)) ? 1 : 0;
                }

        return total == 0 ? 0d : (double)solid / total;
    }

    private static bool Building(BlockId block) => block != BlockId.Air && !DungeonSurfaces.IsRock(block) && block != BlockId.Bedrock;

    private static DungeonVolume Copy(DungeonVolume volume)
    {
        DungeonVolume copy = new(volume.ChunksX, volume.ChunksY, volume.ChunksZ, BlockId.Air);
        for (int x = 0; x < volume.SizeX; x++)
            for (int y = 0; y < volume.SizeY; y++)
                for (int z = 0; z < volume.SizeZ; z++)
                {
                    copy.Set(x, y, z, volume.At(x, y, z));
                }

        return copy;
    }
}
