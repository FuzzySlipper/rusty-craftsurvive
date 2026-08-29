namespace CraftSurvive.Game.Modules.Terrain;

/// <summary>
/// Sole mutable owner of terrain overrides. A zero stored material means an
/// accepted clear operation; absent entries use the deterministic recipe.
/// </summary>
internal sealed class TerrainOverlayState
{
    private readonly SortedDictionary<VoxelAddress, ushort> materials = new();
    private readonly ulong seed;

    internal TerrainOverlayState(ulong seed)
    {
        this.seed = seed;
    }

    internal int Count => materials.Count;

    internal TerrainOverlaySnapshot Snapshot() => new(seed,
        materials.Select(pair => new TerrainOverlayEntry(pair.Key, pair.Value)).ToArray());

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
            materials[edit.Address] = edit.Material;
        }

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
        foreach (TerrainOverlayEntry entry in snapshot.Entries)
        {
            entry.Address.Validate();
            ValidateMaterial(entry.Material);
            if (!materials.TryAdd(entry.Address, entry.Material))
            {
                throw new InvalidOperationException("Terrain overlay entries must have unique addresses.");
            }
        }
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
