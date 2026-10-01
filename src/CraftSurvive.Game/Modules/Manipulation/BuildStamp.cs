using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// A shape to place, as the cells it occupies and the material it places there.
///
/// This is the "rapid base building" half of the manipulation slice, and it is deliberately the
/// same shape as a charge: a decided set of cells handed to the world's one revision-checked edit
/// route. A stamp is not aimed - the player chose a plan, not a crosshair - so it goes through
/// <c>TryEditCells</c> rather than the brush, exactly as an excavation does.
/// </summary>
internal readonly record struct BuildStamp(IReadOnlyList<VoxelAddress> Cells, ushort Material)
{
    /// <summary>
    /// The most cells one stamp may place. A sanity bound, well inside the edit route's own
    /// transaction limit; a base is built from several stamps rather than one enormous one.
    /// </summary>
    internal const int MaximumStampCells = 512;

    /// <summary>Whether this stamp is small enough to place as one transaction.</summary>
    internal bool Placed => Cells.Count > 0 && Cells.Count <= MaximumStampCells;

    /// <summary>
    /// The part of the stamp that can be placed on the world as it stands: only cells whose block is
    /// replaceable (air, water) take the stamp's material, so a plate laid across a slope fills the
    /// gaps and leaves the hill alone.
    /// </summary>
    internal BuildStamp OnReplaceable(Func<VoxelAddress, ushort> materialAt)
    {
        ArgumentNullException.ThrowIfNull(materialAt);
        return this with { Cells = Cells.Where(cell => IsReplaceable(materialAt(cell))).ToArray() };
    }

    /// <summary>Whether a block may be built over. An unknown slot is not.</summary>
    internal static bool IsReplaceable(ushort material) =>
        BlockRegistry.TryGetBySlot(material, out BlockDefinition block) && block.Replaceable;

    /// <summary>A flat plate: the floor of a room, one course thick.</summary>
    internal static BuildStamp Plate(VoxelAddress corner, int width, int depth, ushort material)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "A stamp plate needs a positive width.");
        }

        if (depth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), "A stamp plate needs a positive depth.");
        }

        List<VoxelAddress> cells = new(width * depth);
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                cells.Add(new VoxelAddress(corner.X + x, corner.Y, corner.Z + z));
            }
        }

        return new BuildStamp(cells, material);
    }

    /// <summary>
    /// The cardinal direction a player faces, as a unit step along the nearer axis of their planar
    /// facing. A stamp built from the UI is laid relative to it, so it goes where the player looks.
    /// </summary>
    internal static (int X, int Z) Cardinal(float forwardX, float forwardZ) =>
        MathF.Abs(forwardX) >= MathF.Abs(forwardZ)
            ? (forwardX >= 0f ? 1 : -1, 0)
            : (0, forwardZ >= 0f ? 1 : -1);

    /// <summary>
    /// A floor laid ahead of the player: it starts at the aimed cell, runs <paramref name="depth"/>
    /// cells away from them and <paramref name="width"/> cells across, centred on the aimed cell.
    /// </summary>
    internal static BuildStamp PlateAhead(VoxelAddress aimed, int width, int depth, (int X, int Z) facing, ushort material)
    {
        List<VoxelAddress> cells = new(Math.Max(width, 0) * Math.Max(depth, 0));
        foreach (VoxelAddress cell in Plate(new VoxelAddress(0, 0, 0), width, depth, material).Cells)
        {
            long across = cell.X - ((width - 1) / 2);
            long ahead = cell.Z;
            cells.Add(Oriented(aimed, across, 0, ahead, facing));
        }

        return new BuildStamp(cells, material);
    }

    /// <summary>
    /// A wall raised across the player's facing: <paramref name="length"/> cells across, centred on
    /// the aimed cell, <paramref name="height"/> courses tall.
    /// </summary>
    internal static BuildStamp WallAcross(VoxelAddress aimed, int length, int height, (int X, int Z) facing, ushort material)
    {
        List<VoxelAddress> cells = new(Math.Max(length, 0) * Math.Max(height, 0));
        foreach (VoxelAddress cell in Wall(new VoxelAddress(0, 0, 0), length, height, alongX: true, material).Cells)
        {
            cells.Add(Oriented(aimed, cell.X - ((length - 1) / 2), cell.Y, 0, facing));
        }

        return new BuildStamp(cells, material);
    }

    /// <summary>A cell offset from an origin by steps across, up and ahead of a facing.</summary>
    private static VoxelAddress Oriented(VoxelAddress origin, long across, long up, long ahead, (int X, int Z) facing) =>
        new(origin.X + (ahead * facing.X) - (across * facing.Z),
            origin.Y + up,
            origin.Z + (ahead * facing.Z) + (across * facing.X));

    /// <summary>A straight wall: one course thick, <paramref name="height"/> courses tall.</summary>
    internal static BuildStamp Wall(VoxelAddress corner, int length, int height, bool alongX, ushort material)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "A stamp wall needs a positive length.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "A stamp wall needs a positive height.");
        }

        List<VoxelAddress> cells = new(length * height);
        for (int step = 0; step < length; step++)
        {
            for (int course = 0; course < height; course++)
            {
                cells.Add(alongX
                    ? new VoxelAddress(corner.X + step, corner.Y + course, corner.Z)
                    : new VoxelAddress(corner.X, corner.Y + course, corner.Z + step));
            }
        }

        return new BuildStamp(cells, material);
    }
}
