using CraftSurvive.Game.Modules.Terrain;

namespace CraftSurvive.Game.Modules.Manipulation;

/// <summary>
/// What a block entity is. The voxel says a cell is solid; the entity says what it does. The values
/// are saved, so a kind may be appended but never renumbered.
/// </summary>
internal enum BlockEntityKind : byte
{
    /// <summary>Opens and closes.</summary>
    Door = 0,

    /// <summary>Holds things.</summary>
    Container = 1,

    /// <summary>Lights the room it is in.</summary>
    Light = 2,
}

/// <summary>
/// A placeable thing that is not its voxel. The cell says where it is, the kind says what it does,
/// and <paramref name="State"/> is the one bit of product meaning the entity carries - a door's
/// open flag, a container's fill, a light's lit flag.
/// </summary>
internal readonly record struct BlockEntity(int Id, BlockEntityKind Kind, VoxelAddress Cell, ushort State);

/// <summary>
/// The world's block entities, owned here and keyed by the cell they stand in.
///
/// The index exists because block entities have a lifecycle that voxels do not: a voxel is replaced
/// by an edit, but an entity must be *told* when its cell opens, closes or is blasted away. Three
/// rules, and they are the whole of it:
///
/// - **One per cell.** Placing onto an occupied cell replaces what stood there, because two things
///   claiming one cell is a state the rest of the product would have to defend against forever.
/// - **Break removes.** Editing a cell to empty takes its entity with it.
/// - **Blast sweeps.** A detonation destroys the entities inside the volume it clears, as a decided
///   set rather than one cell at a time, so a charge and its consequences resolve together.
///
/// Entities are addressed by cell and never by position, so nothing here depends on residency or on
/// how far away the player is: culling what is drawn is the renderer's business, and an entity that
/// is merely off-screen is still standing.
/// </summary>
internal sealed class BlockEntityIndex
{
    private readonly Dictionary<VoxelAddress, BlockEntity> byCell = [];
    private int nextId = 1;

    /// <summary>How many entities stand in the world.</summary>
    internal int Count => byCell.Count;

    /// <summary>Counts every change, so the owner of the save can tell whether there is anything to write.</summary>
    internal long Revision { get; private set; }

    /// <summary>Every entity, in a stable order for tests and readouts.</summary>
    internal IEnumerable<BlockEntity> All => byCell.Values.OrderBy(entity => entity.Id);

    /// <summary>
    /// Puts an entity in a cell, replacing whatever stood there. Replacement is deliberate: the
    /// caller editing a cell to a door has already decided the old block is gone.
    /// </summary>
    internal BlockEntity Place(BlockEntityKind kind, VoxelAddress cell, ushort state = 0)
    {
        BlockEntity entity = new(nextId++, kind, cell, state);
        byCell[cell] = entity;
        Revision++;
        return entity;
    }

    /// <summary>The entity standing in a cell, if any.</summary>
    internal bool TryFind(VoxelAddress cell, out BlockEntity entity) => byCell.TryGetValue(cell, out entity);

    /// <summary>Whether anything stands in a cell.</summary>
    internal bool Occupies(VoxelAddress cell) => byCell.ContainsKey(cell);

    /// <summary>
    /// Break: an edited-away cell loses its entity. Returns whether anything was removed, so a
    /// caller can report a door destroyed rather than silently losing it.
    /// </summary>
    internal bool Break(VoxelAddress cell)
    {
        if (!byCell.Remove(cell))
        {
            return false;
        }

        Revision++;
        return true;
    }

    /// <summary>Break a whole set of cells - a stamp undone, a distance-edited volume cleared.</summary>
    internal int BreakAll(IEnumerable<VoxelAddress> cells)
    {
        int removed = 0;
        foreach (VoxelAddress cell in cells)
        {
            if (Break(cell))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <summary>Blast: the entities inside a charge's decided volume are destroyed with it.</summary>
    internal int Sweep(IEnumerable<VoxelAddress> clearedCells) => BreakAll(clearedCells);

    /// <summary>Changes the one bit of product meaning an entity carries, without moving it.</summary>
    internal bool SetState(VoxelAddress cell, ushort state)
    {
        if (!byCell.TryGetValue(cell, out BlockEntity entity))
        {
            return false;
        }

        byCell[cell] = entity with { State = state };
        Revision++;
        return true;
    }

    /// <summary>Every entity as its stored record, in canonical cell order; identities are not saved.</summary>
    internal BlockEntityRecord[] Snapshot() =>
        [.. byCell.Values.OrderBy(entity => entity.Cell).Select(entity => new BlockEntityRecord(entity.Kind, entity.Cell, entity.State))];

    /// <summary>
    /// Replaces every entity with the saved records, minting fresh identities in record order.
    /// Identities are per session: nothing outside the index holds one across a reload.
    /// </summary>
    internal void Restore(IEnumerable<BlockEntityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        byCell.Clear();
        nextId = 1;
        foreach (BlockEntityRecord record in records)
        {
            byCell[record.Cell] = new BlockEntity(nextId++, record.Kind, record.Cell, record.State);
        }

        Revision++;
    }
}

/// <summary>What is saved of a block entity: what it is, where it stands, and its one bit of state.</summary>
internal readonly record struct BlockEntityRecord(BlockEntityKind Kind, VoxelAddress Cell, ushort State);
