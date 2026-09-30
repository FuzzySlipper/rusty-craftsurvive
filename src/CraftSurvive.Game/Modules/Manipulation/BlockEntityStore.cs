using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// The owner of the block entities' save. It restores the index after the terrain has restored
/// the player's edits, and writes the index once per update when it changed - entities change on a
/// player's action, not every frame, so there is nothing to fold.
///
/// An entity is only restored into a cell that still holds a solid block: if the edits it stood in
/// were discarded with a save from another world, the door goes with them rather than standing in
/// open air.
/// </summary>
internal sealed class BlockEntityStore : IProductModule
{
    private readonly TerrainWorld terrain;
    private readonly BlockEntityIndex entities;
    private readonly ProductSaveSlot<BlockEntityRecord[]> slot;
    private long savedRevision;
    private int droppedAtRestore;
    private bool started;

    internal BlockEntityStore(IEngineContext engine, ProductStore store, TerrainWorld terrain, BlockEntityIndex entities)
    {
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
        slot = new ProductSaveSlot<BlockEntityRecord[]>(
            engine, store, SaveManifest.BlockEntities, new BlockEntityCodec(terrain.SaveIdentity));
    }

    public void Start()
    {
        if (started)
        {
            return;
        }

        Restore();
        started = true;
    }

    public void Update(ProductStep step)
    {
        if (started && entities.Revision != savedRevision)
        {
            Save();
        }
    }

    /// <summary>A fresh session over the saved world: the entities are read back as they were saved.</summary>
    public void Restart() => Restore();

    public void Dispose()
    {
        if (started && entities.Revision != savedRevision)
        {
            Save();
        }

        started = false;
    }

    internal string Readout() =>
        $"blockEntities restore={slot.RestoreOutcome} dropped={droppedAtRestore} saves={slot.Saves} failure={slot.LastFailure ?? "none"}";

    private void Restore()
    {
        BlockEntityRecord[] standing = [];
        droppedAtRestore = 0;
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: BlockEntityRecord[] saved })
        {
            standing = [.. saved.Where(record => terrain.MaterialAt(record.Cell) != TerrainConstants.EmptyMaterial)];
            droppedAtRestore = saved.Length - standing.Length;
        }

        entities.Restore(standing);
        savedRevision = entities.Revision;
    }

    private void Save()
    {
        slot.Save(entities.Snapshot());
        savedRevision = entities.Revision;
    }
}
