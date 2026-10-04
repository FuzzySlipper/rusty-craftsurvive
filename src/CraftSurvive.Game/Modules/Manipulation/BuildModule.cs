using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Rapid base building: place a planned shape, and take it back if it landed wrong.
///
/// A stamp applies in one transaction through the world's single edit route, the same one a charge
/// uses, because a stamp is a decided volume rather than an aimed brush. It fills only replaceable
/// cells (air, water), so the undo - the last stamp's placed cells cleared - takes back only what
/// the stamp filled; a filled water cell comes back as air. There is no per-update work here: a stamp resolves immediately.
/// </summary>
internal sealed class BuildModule : IProductModule
{
    private readonly TerrainWorld terrain;
    private readonly BlockEntityIndex entities;
    private VoxelAddress[] lastStamp = [];
    private long plates;
    private long walls;
    private long undone;
    private long entitiesPlaced;
    private long refused;
    private string lastOutcome = "none";

    /// <summary>Whether the player's body covers a cell: nothing is built into the player.</summary>
    private readonly Func<VoxelAddress, bool> occupiedByPlayer;

    internal BuildModule(TerrainWorld terrain, BlockEntityIndex entities, Func<VoxelAddress, bool> occupiedByPlayer)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(entities);
        this.terrain = terrain;
        this.entities = entities;
        this.occupiedByPlayer = occupiedByPlayer ?? throw new ArgumentNullException(nameof(occupiedByPlayer));
    }

    internal long Stamps => plates + walls;

    public void Start()
    {
    }

    /// <summary>Stamps resolve when placed, so an update has nothing to advance.</summary>
    public void Update(ProductStep time)
    {
    }

    /// <summary>A fresh session has nothing to undo.</summary>
    public void Restart() => lastStamp = [];

    public void Dispose() => lastStamp = [];

    /// <summary>Detailed diagnostics, retained for craft.build.readout.</summary>
    internal string LastOutcome => lastOutcome;

    internal BuildFeedback Feedback { get; private set; } = BuildFeedback.Refused("Nothing built yet.");

    internal long Undone => undone;

    /// <summary>How many new block entities this session placed, so a caller can tell a placement from a refusal.</summary>
    internal long EntitiesPlaced => entitiesPlaced;

    internal string Plate(long x, long y, long z, long width, long depth, long material)
    {
        BuildStamp stamp = BuildStamp.Plate(
            new VoxelAddress((int)x, (int)y, (int)z),
            (int)Math.Clamp(width, 1, 64),
            (int)Math.Clamp(depth, 1, 64),
            (ushort)Math.Clamp(material, 1, ushort.MaxValue));
        plates++;
        return Place(stamp, "plate");
    }

    /// <summary>A floor laid ahead of a player facing <paramref name="facing"/>, from the cell they aim at.</summary>
    internal string PlateAhead(VoxelAddress aimed, int width, int depth, (int X, int Z) facing, ushort material)
    {
        plates++;
        return Place(BuildStamp.PlateAhead(aimed, width, depth, facing, material), "plate");
    }

    /// <summary>A wall raised across the facing of a player facing <paramref name="facing"/>, on the cell they aim at.</summary>
    internal string WallAcross(VoxelAddress aimed, int length, int height, (int X, int Z) facing, ushort material)
    {
        walls++;
        return Place(BuildStamp.WallAcross(aimed, length, height, facing, material), "wall");
    }

    internal string Wall(long x, long y, long z, long length, long height, long material)
    {
        BuildStamp stamp = BuildStamp.Wall(
            new VoxelAddress((int)x, (int)y, (int)z),
            (int)Math.Clamp(length, 1, 64),
            (int)Math.Clamp(height, 1, 64),
            alongX: true,
            (ushort)Math.Clamp(material, 1, ushort.MaxValue));
        walls++;
        return Place(stamp, "wall");
    }

    /// <summary>
    /// Digs a pit: a width-by-depth box of cells cleared from the course at <paramref name="y"/>
    /// down through <paramref name="courses"/> courses, as one transaction, and whatever block
    /// entities stood in it go with it. The same bound as a stamp applies.
    /// </summary>
    internal string Dig(long x, long y, long z, long width, long depth, long courses)
    {
        int levels = (int)Math.Clamp(courses, 1, 64);
        VoxelAddress[] cells = [.. Enumerable.Range(0, levels).SelectMany(level => BuildStamp.Plate(
            new VoxelAddress(x, y - level, z),
            (int)Math.Clamp(width, 1, 64),
            (int)Math.Clamp(depth, 1, 64),
            TerrainConstants.StoneMaterial).Cells)];
        if (cells.Length > BuildStamp.MaximumStampCells)
        {
            refused++;
            lastOutcome = $"dig refused: {cells.Length} cells is past the {BuildStamp.MaximumStampCells}-cell bound";
            Feedback = BuildFeedback.Refused("That area is too large.");
            return Readout();
        }

        int entitiesBefore = entities.Count;
        TerrainWorldEditResult result = terrain.TryEditCells(cells, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null);
        Feedback = BuildFeedback.FromEdit(result, "Cleared the area.");
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            int broken = entitiesBefore - entities.Count;
            lastOutcome = $"dug {cells.Length} cells{(broken > 0 ? $", breaking {broken} entities" : string.Empty)}";
        }
        else
        {
            lastOutcome = $"dig refused: {TerrainWorldEditResult.Format(result)}";
        }

        return Readout();
    }

    internal string Undo()
    {
        if (lastStamp.Length == 0)
        {
            lastOutcome = "nothing to undo";
            Feedback = BuildFeedback.Refused("Nothing to undo.");
            return Readout();
        }

        int entitiesBefore = entities.Count;
        TerrainWorldEditResult result = terrain.TryEditCells(
            lastStamp, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null);
        Feedback = BuildFeedback.FromEdit(result, "Removed the last building shape.");
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            undone++;

            // Undoing a stamp opens its cells, so anything standing in them is taken with it.
            int broken = entitiesBefore - entities.Count;
            lastOutcome = broken == 0
                ? $"undid {lastStamp.Length} cells, no entities"
                : $"undid {lastStamp.Length} cells, broke {broken} entit{(broken == 1 ? "y" : "ies")}";
            lastStamp = [];
        }
        else
        {
            lastOutcome = $"undo refused: {TerrainWorldEditResult.Format(result)}";
        }

        return Readout();
    }

    internal string Door(long x, long y, long z, long state)
    {
        return Occupy(x, y, z, BlockEntityKind.Door, state, "door");
    }

    internal string Light(long x, long y, long z, long lit)
    {
        return Occupy(x, y, z, BlockEntityKind.Light, lit, "light");
    }

    internal string Container(long x, long y, long z, long fill)
    {
        return Occupy(x, y, z, BlockEntityKind.Container, fill, "container");
    }

    internal string Entities() =>
        entities.Count == 0
            ? "entities: none"
            : $"entities={entities.Count} [{string.Join(", ", entities.All.Select(e => $"{e.Kind}@{e.Cell.X},{e.Cell.Y},{e.Cell.Z}:{e.State}"))}]";

    internal string Readout() =>
        $"build plates={plates} walls={walls} undone={undone} refused={refused} last={lastOutcome}";

    /// <summary>
    /// A block entity is a voxel you can act on, so it is placed as both: the cell becomes solid
    /// through the same edit route, and the entity goes into the index keyed by that cell. If the
    /// edit is refused the entity is not placed - the voxel is what the world agreed to.
    /// </summary>
    private string Occupy(long x, long y, long z, BlockEntityKind kind, long state, string name)
    {
        VoxelAddress cell = new((int)x, (int)y, (int)z);
        if (!entities.TryFind(cell, out _) && !BuildStamp.IsReplaceable(terrain.MaterialAt(cell)))
        {
            refused++;
            lastOutcome = $"{name} refused: {cell.X},{cell.Y},{cell.Z} is not replaceable";
            Feedback = BuildFeedback.Refused("No room: something is already there.");
            return Readout();
        }

        // A light is a lamp, which glows; every other block entity stands in a stone cell.
        ushort material = kind == BlockEntityKind.Light ? (ushort)BlockId.Lamp : TerrainConstants.StoneMaterial;
        TerrainWorldEditResult result = terrain.TryEditCells(
            [cell], TerrainEditKind.Set, material, occupiedByPlayer);
        Feedback = BuildFeedback.FromEdit(result, $"Placed the {name}.");
        if (result is not (TerrainWorldEditApplied or TerrainWorldEditNoChanges))
        {
            lastOutcome = $"{name} refused: {TerrainWorldEditResult.Format(result)}";
            return Readout();
        }

        ushort value = (ushort)Math.Clamp(state, 0, ushort.MaxValue);

        // Same kind in the same cell means the player is changing what is there - opening a door,
        // filling a container - not asking for a new one. Replacing it would mint a new identity and
        // lose the old one's meaning, which is precisely the state a door's flag exists to carry. A
        // different kind is a replacement: the door is gone and a container stands in its place.
        if (entities.TryFind(cell, out BlockEntity existing) && existing.Kind == kind)
        {
            entities.SetState(cell, value);
            lastOutcome = $"{name} at {cell.X},{cell.Y},{cell.Z} id={existing.Id} state={value} (changed)";
            Feedback = new(true, $"Updated the {name}.");
            return Readout();
        }

        BlockEntity entity = entities.Place(kind, cell, value);
        entitiesPlaced++;
        lastOutcome = $"{name} placed at {cell.X},{cell.Y},{cell.Z} id={entity.Id} state={entity.State}";
        return Readout();
    }

    private string Place(BuildStamp planned, string shape)
    {
        if (!planned.Placed)
        {
            refused++;
            lastOutcome = $"refused: {planned.Cells.Count} cells is past the {BuildStamp.MaximumStampCells}-cell bound";
            Feedback = BuildFeedback.Refused("That building shape is too large.");
            return Readout();
        }

        BuildStamp stamp = planned.OnReplaceable(terrain.MaterialAt);
        if (stamp.Cells.Count == 0)
        {
            refused++;
            lastOutcome = $"{shape} refused: none of its {planned.Cells.Count} cells is replaceable";
            Feedback = BuildFeedback.Refused("No room: something is already there.");
            return Readout();
        }

        TerrainWorldEditResult result = terrain.TryEditCells(
            stamp.Cells, TerrainEditKind.Set, stamp.Material, occupiedByPlayer);
        Feedback = BuildFeedback.FromEdit(result, shape == "plate" ? "Placed the floor." : "Placed the wall.");
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            lastStamp = [.. stamp.Cells];
            lastOutcome = $"{shape} placed: {stamp.Cells.Count} of {planned.Cells.Count} cells";
        }
        else
        {
            lastOutcome = $"{shape} refused: {TerrainWorldEditResult.Format(result)}";
        }

        return Readout();
    }
}
