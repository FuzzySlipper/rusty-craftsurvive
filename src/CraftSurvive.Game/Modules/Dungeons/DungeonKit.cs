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

    /// <summary>The kit fitted to a dungeon, and its volume less the voxels the pieces stand in.</summary>
    internal static (IReadOnlyList<KitPlacement> Pieces, DungeonVolume Drawn) Fit(DungeonVolume volume, IReadOnlyList<PlacedModule> modules)
    {
        DungeonVolume drawn = Copy(volume);
        List<KitPlacement> pieces = [];
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
                    pieces.Add(WallPiece(door ? KitPiece.WallDoor : KitPiece.Wall, origin, face, before, after));
                    cleared.Add(WallSlab(ox, oy, oz, face, before, after));
                }

                foreach ((bool a, bool b, float turn) in new[] { (south, west, 0f), (west, north, MathF.PI / 2), (north, east, MathF.PI), (east, south, -MathF.PI / 2) })
                {
                    if (a && b)
                    {
                        pieces.Add(new KitPlacement(KitPiece.Corner, origin, turn, Vector2.One, Vector2.Zero));
                    }
                }
            }
        }

        foreach ((int x0, int x1, int y0, int y1, int z0, int z1) in cleared)
        {
            drawn.Fill(new DungeonCell(x0, y0, z0), new DungeonCell(x1, y1, z1), BlockId.Air);
        }

        return (pieces, drawn);
    }

    /// <summary>
    /// Whether a piece collides. The floor's voxels stay under the floor piece and carry the floor, so the floor piece only
    /// draws: colliding it too would stack two floors a few centimetres apart in every column of the navigation. The vault
    /// draws only for the same reason (its top meets the floor of any storey above).
    /// </summary>
    internal static bool Collides(KitPiece piece) => piece is not (KitPiece.Floor or KitPiece.Vault);

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
