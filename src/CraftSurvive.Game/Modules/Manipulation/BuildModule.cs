using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Rapid base building: place a planned shape, and take it back if it landed wrong.
///
/// A stamp applies in one transaction through the world's single edit route, the same one a charge
/// uses, because a stamp is a decided volume rather than an aimed brush. It fills only replaceable
/// cells (air, water), so the undo - the last stamp's placed cells cleared - takes back only what
/// the stamp filled; a filled water cell comes back as air. There is no per-update work here: a stamp resolves immediately.
/// </summary>
public sealed class BuildModule : IDebugCommandModule
{
    private readonly TerrainWorld terrain;
    private readonly BlockEntityIndex entities;
    private VoxelAddress[] lastStamp = [];
    private long plates;
    private long walls;
    private long undone;
    private long refused;
    private string lastOutcome = "none";

    internal BuildModule(TerrainWorld terrain, BlockEntityIndex entities)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(entities);
        this.terrain = terrain;
        this.entities = entities;
    }

    internal long Stamps => plates + walls;

    internal long Undone => undone;

    [DebugCommand("craft.build.plate", Description = "Places a flat floor: a width-by-depth plate of one course at a corner, as one transaction.")]
    public string Plate(long x, long y, long z, long width, long depth, long material)
    {
        BuildStamp stamp = BuildStamp.Plate(
            new VoxelAddress((int)x, (int)y, (int)z),
            (int)Math.Clamp(width, 1, 64),
            (int)Math.Clamp(depth, 1, 64),
            (ushort)Math.Clamp(material, 1, ushort.MaxValue));
        plates++;
        return Place(stamp, "plate");
    }

    [DebugCommand("craft.build.wall", Description = "Places a wall: length by height, one course thick, along X, as one transaction.")]
    public string Wall(long x, long y, long z, long length, long height, long material)
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

    [DebugCommand("craft.build.undo", Description = "Clears the cells the last stamp placed. It does not restore what stood there before.")]
    public string Undo()
    {
        if (lastStamp.Length == 0)
        {
            lastOutcome = "nothing to undo";
            return Readout();
        }

        TerrainWorldEditResult result = terrain.TryEditCells(
            lastStamp, TerrainEditKind.Clear, TerrainConstants.EmptyMaterial, null);
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            undone++;

            // Undoing a stamp opens its cells, so anything standing in them is taken with it.
            int broken = entities.BreakAll(lastStamp);
            lastOutcome = broken == 0
                ? $"undid {lastStamp.Length} cells, no entities"
                : $"undid {lastStamp.Length} cells, broke {broken} entit{(broken == 1 ? "y" : "ies")}";
            lastStamp = [];
        }
        else
        {
            lastOutcome = $"undo refused: {TerrainWorld.FormatEditReadout(result)}";
        }

        return Readout();
    }

    [DebugCommand("craft.build.door", Description = "Places a door: a voxel with an openable entity in the same cell.")]
    public string Door(long x, long y, long z, long state)
    {
        return Occupy(x, y, z, BlockEntityKind.Door, state, "door");
    }

    [DebugCommand("craft.build.light", Description = "Places a light: a voxel with an emissive entity in the same cell.")]
    public string Light(long x, long y, long z, long lit)
    {
        return Occupy(x, y, z, BlockEntityKind.Light, lit, "light");
    }

    [DebugCommand("craft.build.container", Description = "Places a container: a voxel with a storing entity in the same cell.")]
    public string Container(long x, long y, long z, long fill)
    {
        return Occupy(x, y, z, BlockEntityKind.Container, fill, "container");
    }

    [DebugCommand("craft.build.entities", Description = "Lists the block entities standing in the world.")]
    public string Entities() =>
        entities.Count == 0
            ? "entities: none"
            : $"entities={entities.Count} [{string.Join(", ", entities.All.Select(e => $"{e.Kind}@{e.Cell.X},{e.Cell.Y},{e.Cell.Z}:{e.State}"))}]";

    [DebugCommand("craft.build.readout", Description = "Reports stamps placed, undos, refusals, and the last outcome.")]
    public string Readout() =>
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
            return Readout();
        }

        TerrainWorldEditResult result = terrain.TryEditCells(
            [cell], TerrainEditKind.Set, TerrainConstants.StoneMaterial, null);
        if (result is not (TerrainWorldEditApplied or TerrainWorldEditNoChanges))
        {
            lastOutcome = $"{name} refused: {TerrainWorld.FormatEditReadout(result)}";
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
            return Readout();
        }

        BlockEntity entity = entities.Place(kind, cell, value);
        lastOutcome = $"{name} placed at {cell.X},{cell.Y},{cell.Z} id={entity.Id} state={entity.State}";
        return Readout();
    }

    private string Place(BuildStamp planned, string shape)
    {
        if (!planned.Placed)
        {
            refused++;
            lastOutcome = $"refused: {planned.Cells.Count} cells is past the {BuildStamp.MaximumStampCells}-cell bound";
            return Readout();
        }

        BuildStamp stamp = planned.OnReplaceable(terrain.MaterialAt);
        if (stamp.Cells.Count == 0)
        {
            refused++;
            lastOutcome = $"{shape} refused: none of its {planned.Cells.Count} cells is replaceable";
            return Readout();
        }

        TerrainWorldEditResult result = terrain.TryEditCells(
            stamp.Cells, TerrainEditKind.Set, stamp.Material, null);
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            lastStamp = [.. stamp.Cells];
            lastOutcome = $"{shape} placed: {stamp.Cells.Count} of {planned.Cells.Count} cells";
        }
        else
        {
            lastOutcome = $"{shape} refused: {TerrainWorld.FormatEditReadout(result)}";
        }

        return Readout();
    }
}
