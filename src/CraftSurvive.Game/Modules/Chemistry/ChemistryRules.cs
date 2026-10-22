using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using RustyChemistry.Content;
using RustyChemistry.Simulation;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// The world's blocks as the pack's materials, and which materials write back as which blocks (#10293). Resolved
/// once against the pack, so a block or material named wrongly fails at load. Engine-free.
/// </summary>
internal sealed class BlockChemistry
{
    private readonly ushort[] materials;

    internal BlockChemistry(ChemistrySettings settings, ChemistryPack pack)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(pack);
        Fallback = (ushort)pack.Material(settings.DefaultMaterial);
        materials = [.. Enumerable.Repeat(Fallback, Enum.GetValues<BlockId>().Max(block => (int)block) + 1)];
        foreach ((BlockId block, string material) in settings.Blocks)
        {
            materials[(int)block] = (ushort)pack.Material(material);
        }

        Water = (ushort)pack.Material(settings.WaterMaterial);
        Air = materials[(int)BlockId.Air];
        WriteBacks = settings.WriteBack.ToDictionary(rule => pack.Material(rule.Material), rule => rule);
        Burning = settings.Fires.Materials.Select(pack.Material).ToHashSet();
    }

    /// <summary>The material of a block the settings do not name.</summary>
    internal ushort Fallback { get; }

    /// <summary>The material open air below a water surface loads as.</summary>
    internal ushort Water { get; }

    internal ushort Air { get; }

    /// <summary>The materials that write back into the world, by material index.</summary>
    internal IReadOnlyDictionary<int, WriteBackSetting> WriteBacks { get; }

    /// <summary>The materials shown as fire.</summary>
    internal IReadOnlySet<int> Burning { get; }

    internal ushort MaterialOf(ushort block) => block < materials.Length ? materials[block] : Fallback;

    /// <summary>A cell's material: its block's, except that air below a water surface is water.</summary>
    internal ushort CellMaterial(ushort block, long y, double? waterTop)
    {
        ushort material = MaterialOf(block);
        return material == Air && waterTop is double top && y < top ? Water : material;
    }
}

/// <summary>What the field writes into the world, and how much it may (#10293). Engine-free.</summary>
internal static class WriteBack
{
    /// <summary>
    /// How many cells may be written now: no more than one write's worth, and none once the terrain overlay would
    /// keep fewer than the reserved entries free for the player's own edits.
    /// </summary>
    internal static int Allowed(int overlayEntries, int overlayCapacity, WriteBackBudgetSetting budget) =>
        Math.Max(0, Math.Min(budget.CellsPerWrite, overlayCapacity - budget.KeepFreeEntries - overlayEntries));

    /// <summary>The identity changes of a step that ask to be written back, in the order they happened.</summary>
    internal static IEnumerable<(Cell Cell, WriteBackSetting Rule)> Wanted(IEnumerable<MaterialChange> changes, BlockChemistry blocks)
    {
        foreach (MaterialChange change in changes)
        {
            if (blocks.WriteBacks.TryGetValue(change.To, out WriteBackSetting? rule))
            {
                yield return (change.Cell, rule);
            }
        }
    }

    /// <summary>
    /// Of the cells waiting, the ones to write now, grouped by the block each becomes: at most <paramref name="allowed"/>,
    /// and only where the world still holds a block the rule may replace (an edit since the burn wins).
    /// </summary>
    internal static Dictionary<BlockId, List<Cell>> Choose(IEnumerable<(Cell Cell, WriteBackSetting Rule)> waiting, Func<Cell, ushort> worldBlock, int allowed)
    {
        Dictionary<BlockId, List<Cell>> chosen = [];
        int count = 0;
        foreach ((Cell cell, WriteBackSetting rule) in waiting)
        {
            if (count >= allowed)
            {
                break;
            }

            if (!rule.Over.Contains((BlockId)worldBlock(cell)))
            {
                continue;
            }

            if (!chosen.TryGetValue(rule.Block, out List<Cell>? cells))
            {
                chosen[rule.Block] = cells = [];
            }

            cells.Add(cell);
            count++;
        }

        return chosen;
    }
}

/// <summary>The weather as the field's ambient (#10293): air heat, the wind's drift and how hard it rains. Engine-free.</summary>
internal readonly record struct ChemistryAmbient(float Heat, Vector3 Drift, float Rain)
{
    /// <summary>
    /// From the weather's channels where the field is: <paramref name="cold"/>, <paramref name="wind"/> and
    /// <paramref name="precipitation"/> are 0..1; <paramref name="flow"/> is the wind's unit direction over the
    /// ground (world x, z).
    /// </summary>
    internal static ChemistryAmbient From(double cold, double wind, double precipitation, Vector2 flow, AmbientSetting setting)
    {
        float heat = setting.WarmHeat + ((setting.ColdHeat - setting.WarmHeat) * (float)Math.Clamp(cold, 0, 1));
        Vector3 drift = new Vector3(flow.X, 0f, flow.Y) * (float)(Math.Clamp(wind, 0, 1) * setting.WindCellsPerSecond);
        return new ChemistryAmbient(heat, drift, (float)Math.Clamp(precipitation, 0, 1));
    }

    /// <summary>
    /// Whether this differs from <paramref name="applied"/> enough to apply: each change wakes the whole field, so
    /// weather that barely moved is left alone.
    /// </summary>
    internal bool Differs(ChemistryAmbient applied) =>
        Math.Abs(Heat - applied.Heat) >= 0.5f || Vector3.Distance(Drift, applied.Drift) >= 0.05f || Math.Abs(Rain - applied.Rain) >= 0.05f;
}

/// <summary>Which chunks the field keeps around the player (#10293). Engine-free.</summary>
internal static class FieldWindow
{
    private const int Size = ChemistryField.ChunkSize;

    /// <summary>
    /// The first cell of each chunk wanted around a chunk column, nearest first: in every column within
    /// <paramref name="reach"/>, the chunk holding the column's surface and the one above it, where smoke rises.
    /// </summary>
    internal static List<Cell> Around(int columnX, int columnZ, int reach, Func<int, int, long> surfaceY)
    {
        List<(int Distance, Cell Chunk)> wanted = [];
        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int x = columnX + dx, z = columnZ + dz;
                int chunkY = (int)Math.Floor(surfaceY((x * Size) + (Size / 2), (z * Size) + (Size / 2)) / (double)Size);
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dz));
                wanted.Add((distance, new Cell(x * Size, chunkY * Size, z * Size)));
                wanted.Add((distance, new Cell(x * Size, (chunkY + 1) * Size, z * Size)));
            }
        }

        return [.. wanted.OrderBy(entry => entry.Distance).Select(entry => entry.Chunk)];
    }

    /// <summary>
    /// The chunks wanted around the player, with the columns beside every burning column out to
    /// <paramref name="burningReach"/>, so a fire carries on past the window instead of stopping at its edge;
    /// nearest the player first.
    /// </summary>
    internal static List<Cell> Around(int columnX, int columnZ, int reach, int burningReach, IEnumerable<(int X, int Z)> burningColumns,
        Func<int, int, long> surfaceY)
    {
        List<Cell> wanted = Around(columnX, columnZ, reach, surfaceY);
        HashSet<Cell> seen = [.. wanted];
        foreach ((int x, int z) in burningColumns)
        {
            foreach (Cell chunk in Around(x, z, 1, surfaceY))
            {
                if (ColumnsApart(chunk, columnX, columnZ) <= burningReach && seen.Add(chunk))
                {
                    wanted.Add(chunk);
                }
            }
        }

        return [.. wanted.OrderBy(chunk => ColumnsApart(chunk, columnX, columnZ))];
    }

    /// <summary>The chunk columns holding a burning cell; only active chunks are read, since a sleeping one cannot be burning.</summary>
    internal static HashSet<(int X, int Z)> BurningColumns(ChemistryField field, IReadOnlySet<int> burning)
    {
        HashSet<(int X, int Z)> columns = [];
        ushort[] materials = new ushort[Size * Size * Size];
        foreach (Cell origin in field.LoadedChunks())
        {
            if (field.IsActive(origin) && !columns.Contains((Column(origin.X), Column(origin.Z))))
            {
                field.ReadChunkMaterials(origin, materials);
                if (materials.AsSpan().ContainsAny([.. burning.Select(material => (ushort)material)]))
                {
                    columns.Add((Column(origin.X), Column(origin.Z)));
                }
            }
        }

        return columns;
    }

    /// <summary>The chunk column a world position stands in.</summary>
    internal static (int X, int Z) ColumnOf(Vector3 world) =>
        ((int)Math.Floor(world.X / Size), (int)Math.Floor(world.Z / Size));

    /// <summary>How many chunk columns apart two chunks stand, the larger of the two axes.</summary>
    internal static int ColumnsApart(Cell chunk, int columnX, int columnZ) =>
        Math.Max(Math.Abs(Column(chunk.X) - columnX), Math.Abs(Column(chunk.Z) - columnZ));

    private static int Column(int cell) => (int)Math.Floor(cell / (double)Size);
}

/// <summary>Where the field's fires are shown (#10293). Engine-free.</summary>
internal static class BurningPlaces
{
    private const int Size = ChemistryField.ChunkSize;
    private const int CellsPerChunk = Size * Size * Size;

    /// <summary>
    /// One place per square of <paramref name="cluster"/> cells across that holds burning cells: on top of its highest
    /// burning cell, at the mean of its burning columns; the <paramref name="slots"/> nearest <paramref name="near"/>.
    /// Only active chunks are read, since a sleeping one cannot be burning.
    /// </summary>
    internal static List<Vector3> Pick(ChemistryField field, IReadOnlySet<int> burning, Vector3 near, int slots, int cluster)
    {
        Dictionary<(int X, int Z), (Vector3 Sum, int Count, int Top)> squares = [];
        ushort[] materials = new ushort[CellsPerChunk];
        foreach (Cell origin in field.LoadedChunks())
        {
            if (!field.IsActive(origin))
            {
                continue;
            }

            field.ReadChunkMaterials(origin, materials);
            for (int index = 0; index < CellsPerChunk; index++)
            {
                if (!burning.Contains(materials[index]))
                {
                    continue;
                }

                Cell cell = origin.Offset(index % Size, index / Size % Size, index / (Size * Size));
                (int, int) square = ((int)Math.Floor(cell.X / (double)cluster), (int)Math.Floor(cell.Z / (double)cluster));
                (Vector3 sum, int count, int top) = squares.GetValueOrDefault(square, (Vector3.Zero, 0, int.MinValue));
                squares[square] = (sum + new Vector3(cell.X + 0.5f, 0f, cell.Z + 0.5f), count + 1, Math.Max(top, cell.Y));
            }
        }

        return [.. squares.Values
            .Select(square => (square.Sum / square.Count) with { Y = square.Top + 1 })
            .OrderBy(place => Vector3.DistanceSquared(place, near))
            .Take(slots)];
    }
}
