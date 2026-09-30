namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Sole mutable owner of terrain overrides. A zero stored material means an
/// accepted clear operation; absent entries use the deterministic recipe.
/// </summary>
internal sealed class TerrainOverlayState
{
    private readonly SortedDictionary<VoxelAddress, ushort> materials = new();

    /// <summary>How many overrides fall in each chunk, so "does the player touch this chunk" is one lookup.</summary>
    private readonly Dictionary<TerrainChunkAddress, int> perChunk = [];
    private readonly ulong seed;
    private ulong revision;
    private TerrainOverlaySnapshot? snapshot;
    private ulong snapshotRevision = ulong.MaxValue;

    internal TerrainOverlayState(ulong seed)
    {
        this.seed = seed;
    }

    internal int Count => materials.Count;

    /// <summary>Monotonically identifies product-owned occupancy inputs.</summary>
    internal ulong Revision => revision;

    /// <summary>The override at one cell, if the player has changed it.</summary>
    internal bool TryGetMaterial(VoxelAddress address, out ushort material) =>
        materials.TryGetValue(address, out material);

    /// <summary>Whether the player has changed any cell in the chunk.</summary>
    internal bool TouchesChunk(TerrainChunkAddress chunk) => perChunk.ContainsKey(chunk);

    /// <summary>
    /// Whether the overlay has room for these edits: every edit to a cell not already overridden
    /// takes an entry. An edit the overlay cannot hold must be refused before it reaches the
    /// Engine, or the scene and the save would disagree.
    /// </summary>
    internal bool Admits(IReadOnlyList<TerrainVoxelEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        int newEntries = edits.Select(edit => edit.Address).Distinct().Count(address => !materials.ContainsKey(address));
        return materials.Count + newEntries <= TerrainConstants.MaximumOverlayEntries;
    }

    /// <summary>
    /// The overlay as a sorted, validated value. It is rebuilt only when the overlay has changed
    /// since the last call, so asking for it every update costs nothing between edits.
    /// </summary>
    internal TerrainOverlaySnapshot Snapshot()
    {
        if (snapshot is null || snapshotRevision != revision)
        {
            snapshot = new(seed, materials.Select(pair => new TerrainOverlayEntry(pair.Key, pair.Value)).ToArray());
            snapshotRevision = revision;
        }

        return snapshot;
    }

    internal TerrainOverlayReceipt Apply(TerrainEditAccepted admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        TerrainVoxelEdit[] edits = admission.Edits.ToArray();
        int newEntries = edits.Count(edit => !materials.ContainsKey(edit.Address));
        if (materials.Count + newEntries > TerrainConstants.MaximumOverlayEntries)
        {
            throw new InvalidOperationException(
                $"Terrain overlays allow at most {TerrainConstants.MaximumOverlayEntries} retained entries.");
        }

        foreach (TerrainVoxelEdit edit in edits)
        {
            if (!materials.ContainsKey(edit.Address))
            {
                TerrainChunkAddress chunk = edit.Address.Chunk;
                perChunk[chunk] = perChunk.GetValueOrDefault(chunk) + 1;
            }

            materials[edit.Address] = edit.Material;
        }

        revision = checked(revision + 1UL);
        return new TerrainOverlayReceipt(edits);
    }

    internal void Restore(TerrainOverlaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Seed != seed)
        {
            throw new InvalidOperationException("Terrain overlay seed does not match the active terrain recipe.");
        }

        if (snapshot.Entries.Length > TerrainConstants.MaximumOverlayEntries)
        {
            throw new InvalidOperationException(
                $"Terrain overlays allow at most {TerrainConstants.MaximumOverlayEntries} retained entries.");
        }

        materials.Clear();
        perChunk.Clear();
        foreach (TerrainOverlayEntry entry in snapshot.Entries)
        {
            entry.Address.Validate();
            ValidateMaterial(entry.Material);
            if (!materials.TryAdd(entry.Address, entry.Material))
            {
                throw new InvalidOperationException("Terrain overlay entries must have unique addresses.");
            }

            TerrainChunkAddress chunk = entry.Address.Chunk;
            perChunk[chunk] = perChunk.GetValueOrDefault(chunk) + 1;
        }

        revision = checked(revision + 1UL);
    }

    private static void ValidateMaterial(ushort material)
    {
        if (material > TerrainConstants.MaximumMaterial)
        {
            throw new ArgumentOutOfRangeException(nameof(material), material,
                $"Terrain materials must not exceed {TerrainConstants.MaximumMaterial}.");
        }
    }
}

internal sealed class TerrainOverlaySnapshot
{
    private readonly TerrainOverlayEntry[] entries;

    internal TerrainOverlaySnapshot(ulong seed, TerrainOverlayEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Length > TerrainConstants.MaximumOverlayEntries)
        {
            throw new ArgumentOutOfRangeException(nameof(entries),
                $"Terrain overlays allow at most {TerrainConstants.MaximumOverlayEntries} retained entries.");
        }

        Seed = seed;
        this.entries = entries.OrderBy(entry => entry.Address).ToArray();
        ValidateCanonical(this.entries);
    }

    internal ulong Seed { get; }

    internal TerrainOverlayEntry[] Entries => entries.ToArray();

    /// <summary>
    /// Whether any edit falls inside this chunk. The cache may only serve a chunk that
    /// carries no player edits: a cached payload is the generator's output, so serving
    /// it for a chunk the player has changed would silently undo their change.
    /// </summary>
    internal bool TouchesChunk(TerrainChunkAddress chunk)
    {
        int lower = 0;
        int upper = entries.Length - 1;
        int first = entries.Length;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) / 2);
            if (entries[middle].Address.Chunk.CompareTo(chunk) >= 0)
            {
                first = middle;
                upper = middle - 1;
            }
            else
            {
                lower = middle + 1;
            }
        }

        return first < entries.Length && entries[first].Address.Chunk.Equals(chunk);
    }

    internal bool TryGetMaterial(VoxelAddress address, out ushort material)
    {
        int lower = 0;
        int upper = entries.Length - 1;
        while (lower <= upper)
        {
            int middle = lower + ((upper - lower) / 2);
            TerrainOverlayEntry entry = entries[middle];
            int comparison = entry.Address.CompareTo(address);
            if (comparison == 0)
            {
                material = entry.Material;
                return true;
            }

            if (comparison < 0)
            {
                lower = middle + 1;
            }
            else
            {
                upper = middle - 1;
            }
        }

        material = TerrainConstants.EmptyMaterial;
        return false;
    }

    private static void ValidateCanonical(TerrainOverlayEntry[] entries)
    {
        for (int index = 0; index < entries.Length; index++)
        {
            TerrainOverlayEntry entry = entries[index];
            entry.Address.Validate();
            if (entry.Material > TerrainConstants.MaximumMaterial)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), "Terrain overlay contains an unsupported material.");
            }

            if (index > 0 && entries[index - 1].Address.CompareTo(entry.Address) >= 0)
            {
                throw new ArgumentException("Terrain overlay entries must be sorted by unique voxel address.", nameof(entries));
            }
        }
    }
}

internal readonly record struct TerrainOverlayEntry(VoxelAddress Address, ushort Material);

internal readonly record struct TerrainOverlayReceipt(IReadOnlyList<TerrainVoxelEdit> AppliedEdits);
