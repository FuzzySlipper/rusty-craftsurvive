using CraftSurvive.Game.Modules.Terrain;
using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// Rapid base building: place a planned shape, and take it back if it landed wrong.
///
/// A stamp applies in one transaction through the world's single revision-checked edit route, the
/// same one a charge uses, because a stamp is a decided volume rather than an aimed brush. There is
/// no per-update work here: a stamp resolves immediately, and the undo is the last stamp's own
/// cells cleared - which is the honest scope of an affordance the route can express, since
/// restoring what was there before would need one material per cell and a transaction carries one.
/// </summary>
public sealed class BuildModule : IDebugCommandModule
{
    private readonly TerrainWorld terrain;
    private VoxelAddress[] lastStamp = [];
    private long plates;
    private long walls;
    private long undone;
    private long refused;
    private string lastOutcome = "none";

    internal BuildModule(TerrainWorld terrain)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        this.terrain = terrain;
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
            lastOutcome = $"undid {lastStamp.Length} cells";
            lastStamp = [];
        }
        else
        {
            lastOutcome = $"undo refused: {TerrainWorld.FormatEditReadout(result)}";
        }

        return Readout();
    }

    [DebugCommand("craft.build.readout", Description = "Reports stamps placed, undos, refusals, and the last outcome.")]
    public string Readout() =>
        $"build plates={plates} walls={walls} undone={undone} refused={refused} last={lastOutcome}";

    private string Place(BuildStamp stamp, string shape)
    {
        if (!stamp.Placed)
        {
            refused++;
            lastOutcome = $"refused: {stamp.Cells.Count} cells is past the {BuildStamp.MaximumStampCells}-cell bound";
            return Readout();
        }

        TerrainWorldEditResult result = terrain.TryEditCells(
            stamp.Cells, TerrainEditKind.Set, stamp.Material, null);
        if (result is TerrainWorldEditApplied or TerrainWorldEditNoChanges)
        {
            lastStamp = [.. stamp.Cells];
            lastOutcome = $"{shape} placed: {stamp.Cells.Count} cells";
        }
        else
        {
            lastOutcome = $"{shape} refused: {TerrainWorld.FormatEditReadout(result)}";
        }

        return Readout();
    }
}
