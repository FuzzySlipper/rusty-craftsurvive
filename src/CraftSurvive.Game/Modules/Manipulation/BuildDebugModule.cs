using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>Live-debug adapters over <see cref="BuildModule"/>; it owns no state of its own.</summary>
public sealed class BuildDebugModule : IDebugCommandModule
{
    private readonly Func<BuildModule> buildSource;
    private BuildModule build => buildSource();
    private readonly Func<BlockEntityStore> entityStoreSource;
    private BlockEntityStore entityStore => entityStoreSource();

    internal BuildDebugModule(Func<BuildModule> build, Func<BlockEntityStore> entityStore)
    {
        this.buildSource = build ?? throw new ArgumentNullException(nameof(build));
        this.entityStoreSource = entityStore ?? throw new ArgumentNullException(nameof(entityStore));
    }

    [DebugCommand("craft.build.plate", Description = "Places a flat floor: a width-by-depth plate of one course at a corner, as one transaction.")]
    public string Plate(long x, long y, long z, long width, long depth, long material) => build.Plate(x, y, z, width, depth, material);

    [DebugCommand("craft.build.wall", Description = "Places a wall: length by height, one course thick, along X, as one transaction.")]
    public string Wall(long x, long y, long z, long length, long height, long material) => build.Wall(x, y, z, length, height, material);

    [DebugCommand("craft.build.dig", Description = "Digs a pit: width by depth, from the given course down through the given number of courses, as one transaction.")]
    public string Dig(long x, long y, long z, long width, long depth, long courses) => build.Dig(x, y, z, width, depth, courses);

    [DebugCommand("craft.build.undo", Description = "Clears the cells the last stamp placed. It does not restore what stood there before.")]
    public string Undo() => build.Undo();

    [DebugCommand("craft.build.door", Description = "Places a door: a voxel with an openable entity in the same cell.")]
    public string Door(long x, long y, long z, long state) => build.Door(x, y, z, state);

    [DebugCommand("craft.build.light", Description = "Places a light: a voxel with an emissive entity in the same cell.")]
    public string Light(long x, long y, long z, long lit) => build.Light(x, y, z, lit);

    [DebugCommand("craft.build.container", Description = "Places a container: a voxel with a storing entity in the same cell.")]
    public string Container(long x, long y, long z, long fill) => build.Container(x, y, z, fill);

    [DebugCommand("craft.build.entities", Description = "Lists the block entities standing in the world, and how their save went.")]
    public string Entities() => $"{build.Entities()}; {entityStore.Readout()}";

    [DebugCommand("craft.build.readout", Description = "Reports stamps placed, undos, refusals, and the last outcome.")]
    public string Readout() => build.Readout();
}
