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
    /// and only where the world still holds a block the rule may replace (an edit since the burn wins). The cell under each
    /// is written too where the world holds one of the rule's blocks beneath, within the same allowance: a cell and the one
    /// under it are written together, unless the allowance cannot hold two even alone, when the cell is written by itself.
    /// <paramref name="consumed"/> is how many of the waiting cells were dealt with (written or passed over); the rest wait
    /// for the next write (R10296-1).
    /// </summary>
    internal static Dictionary<BlockId, List<Cell>> Choose(IEnumerable<(Cell Cell, WriteBackSetting Rule)> waiting, Func<Cell, ushort> worldBlock, int allowed,
        out int consumed)
    {
        Dictionary<BlockId, List<Cell>> chosen = [];
        int count = 0;
        consumed = 0;
        foreach ((Cell cell, WriteBackSetting rule) in waiting)
        {
            if (count >= allowed)
            {
                break;
            }

            if (!rule.Over.Contains((BlockId)worldBlock(cell)))
            {
                consumed++;
                continue;
            }

            Cell under = cell.Offset(0, -1, 0);
            bool beneath = rule.Beneath.Contains((BlockId)worldBlock(under));
            if (beneath && count + 2 > allowed && count > 0)
            {
                break;
            }

            if (!chosen.TryGetValue(rule.Block, out List<Cell>? cells))
            {
                chosen[rule.Block] = cells = [];
            }

            cells.Add(cell);
            count++;
            if (beneath && count < allowed)
            {
                cells.Add(under);
                count++;
            }

            consumed++;
        }

        return chosen;
    }
}

/// <summary>
/// The weather as the field's ambient (#10293): air heat, the wind's drift, how hard it rains, and how much of the
/// ground the lying snow covers. Engine-free.
/// </summary>
internal readonly record struct ChemistryAmbient(float Heat, Vector3 Drift, float Rain, float Snow)
{
    /// <summary>
    /// From the weather's channels where the field is: <paramref name="cold"/>, <paramref name="wind"/> and
    /// <paramref name="precipitation"/> are 0..1; <paramref name="flow"/> is the wind's unit direction over the
    /// ground (world x, z); <paramref name="snowCover"/> is the snow cover's share of the ground there, 0..1. The
    /// snow cover's cells are far wider than the field, so one value stands for all of it, as the weather's does.
    /// </summary>
    internal static ChemistryAmbient From(double cold, double wind, double precipitation, Vector2 flow, double snowCover, AmbientSetting setting)
    {
        float heat = setting.WarmHeat + ((setting.ColdHeat - setting.WarmHeat) * (float)Math.Clamp(cold, 0, 1));
        Vector3 drift = new Vector3(flow.X, 0f, flow.Y) * (float)(Math.Clamp(wind, 0, 1) * setting.WindCellsPerSecond);
        return new ChemistryAmbient(heat, drift, (float)Math.Clamp(precipitation, 0, 1), (float)Math.Clamp(snowCover, 0, 1));
    }

    /// <summary>
    /// Whether this differs from <paramref name="applied"/> enough to apply: each change wakes the whole field, so
    /// weather that barely moved is left alone. Rain or snow stopping always applies, so a last trace of either
    /// does not go on wetting the ground.
    /// </summary>
    internal bool Differs(ChemistryAmbient applied) =>
        Math.Abs(Heat - applied.Heat) >= 0.5f || Vector3.Distance(Drift, applied.Drift) >= 0.05f || Wetting(Rain, applied.Rain)
        || Wetting(Snow, applied.Snow);

    private static bool Wetting(float now, float applied) => Math.Abs(now - applied) >= 0.05f || ((now == 0f) != (applied == 0f));
}

/// <summary>Which chunks the field keeps around the player (#10293). Engine-free.</summary>
internal static class FieldWindow
{
    private const int Size = ChemistryField.ChunkSize;

    /// <summary>
    /// The first cell of each chunk wanted around a chunk column, nearest first: in every column within
    /// <paramref name="reach"/>, the chunks holding the column's surface, from its lowest cell to its highest
    /// (<paramref name="band"/>, by chunk column), and the one above them, where smoke rises.
    /// </summary>
    internal static List<Cell> Around(int columnX, int columnZ, int reach, Func<int, int, SurfaceBand> band)
    {
        List<(int Distance, Cell Chunk)> wanted = [];
        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int x = columnX + dx, z = columnZ + dz;
                SurfaceBand surface = band(x, z);
                int distance = Math.Max(Math.Abs(dx), Math.Abs(dz));
                for (int chunkY = Column(surface.Low); chunkY <= Column(surface.High) + 1; chunkY++)
                {
                    wanted.Add((distance, new Cell(x * Size, chunkY * Size, z * Size)));
                }
            }
        }

        return [.. wanted.OrderBy(entry => entry.Distance).Select(entry => entry.Chunk)];
    }

    /// <summary>The lowest and highest surface cells of a chunk column, from the surface height of each of its cells.</summary>
    internal static SurfaceBand BandOf(int columnX, int columnZ, Func<long, long, long> surfaceY)
    {
        long low = long.MaxValue, high = long.MinValue;
        for (int z = 0; z < Size; z++)
        {
            for (int x = 0; x < Size; x++)
            {
                long y = surfaceY(((long)columnX * Size) + x, ((long)columnZ * Size) + z);
                low = Math.Min(low, y);
                high = Math.Max(high, y);
            }
        }

        return new SurfaceBand(low, high);
    }

    /// <summary>
    /// The chunks wanted around the player, with the columns beside every burning column out to
    /// <paramref name="burningReach"/>, so a fire carries on past the window instead of stopping at its edge;
    /// nearest the player first.
    /// </summary>
    internal static List<Cell> Around(int columnX, int columnZ, int reach, int burningReach, IEnumerable<(int X, int Z)> burningColumns,
        Func<int, int, SurfaceBand> band)
    {
        List<Cell> wanted = Around(columnX, columnZ, reach, band);
        HashSet<Cell> seen = [.. wanted];
        foreach ((int x, int z) in burningColumns)
        {
            foreach (Cell chunk in Around(x, z, 1, band))
            {
                if (ColumnsApart(chunk, columnX, columnZ) <= burningReach && seen.Add(chunk))
                {
                    wanted.Add(chunk);
                }
            }
        }

        return [.. wanted.OrderBy(chunk => ColumnsApart(chunk, columnX, columnZ))];
    }

    /// <summary>The chunk columns holding a burning cell.</summary>
    internal static HashSet<(int X, int Z)> BurningColumns(IEnumerable<BurningCell> burning) =>
        [.. burning.Select(burnt => (Column(burnt.Cell.X), Column(burnt.Cell.Z)))];

    /// <summary>The chunk column a world position stands in.</summary>
    internal static (int X, int Z) ColumnOf(Vector3 world) =>
        ((int)Math.Floor(world.X / Size), (int)Math.Floor(world.Z / Size));

    /// <summary>How many chunk columns apart two chunks stand, the larger of the two axes.</summary>
    internal static int ColumnsApart(Cell chunk, int columnX, int columnZ) =>
        Math.Max(Math.Abs(Column(chunk.X) - columnX), Math.Abs(Column(chunk.Z) - columnZ));

    private static int Column(long cell) => (int)Math.Floor(cell / (double)Size);
}

/// <summary>The lowest and highest surface cell heights of a chunk column.</summary>
internal readonly record struct SurfaceBand(long Low, long High);

/// <summary>When a grass fire's scar grows back, column by column (#10296): patchily, each column somewhere in the regrowth time.</summary>
internal static class ScarHealing
{
    /// <summary>
    /// Whether a scar cell is due to grow back: its column burnt at <paramref name="burntHours"/>, and its column's hash puts
    /// it between the <paramref name="earliest"/> share of the regrowth time and the whole of it. A column heals whole, the
    /// charred ground beneath with the surface, so a healed surface never blends with char under it.
    /// </summary>
    internal static bool Due(long x, long z, double burntHours, double hours, double regrowHours, double earliest)
    {
        double share = earliest + ((1 - earliest) * Unit(x, z));
        return hours - burntHours >= regrowHours * share;
    }

    /// <summary>The column's hash as a number from 0 to 1.</summary>
    internal static double Unit(long x, long z)
    {
        ulong h = ((ulong)x * 0x9E37_79B9_7F4A_7C15UL) ^ ((ulong)z * 0x1656_67B1_9E37_79F9UL);
        h = (h ^ (h >> 31)) * 0xBF58_476D_1CE4_E5B9UL;
        h ^= h >> 29;
        return (h >> 11) / (double)(1UL << 53);
    }
}

/// <summary>A burning cell as the fire's look reads it (#10296): where, how hot, and the most smoke in the air just over it.</summary>
internal readonly record struct BurningCell(Cell Cell, float Heat, float Smoke);

/// <summary>
/// Reads the field's fire in one pass (#10384): the burning cells of its active chunks (a sleeping chunk cannot be
/// burning), each with its heat and the smoke over it. Engine-free, so the field's worker reads it with the step.
/// </summary>
internal static class FireScan
{
    private const int Size = ChemistryField.ChunkSize;
    private const int CellsPerChunk = Size * Size * Size;

    /// <summary>A burning cell's smoke is read in the air this many cells over it, within its chunk.</summary>
    private const int SmokeCellsOver = 3;

    internal static List<BurningCell> Read(ChemistryField field, IReadOnlySet<int> burning, int heatQuantity, int smokeQuantity)
    {
        List<BurningCell> found = [];
        ushort[] materials = new ushort[CellsPerChunk];
        float[] heat = new float[CellsPerChunk], smoke = new float[CellsPerChunk];
        foreach (Cell origin in field.LoadedChunks())
        {
            if (!field.IsActive(origin))
            {
                continue;
            }

            field.ReadChunkMaterials(origin, materials);
            bool any = false;
            for (int index = 0; index < CellsPerChunk && !any; index++)
            {
                any = burning.Contains(materials[index]);
            }

            if (!any)
            {
                continue;
            }

            field.ReadChunk(origin, heatQuantity, heat);
            field.ReadChunk(origin, smokeQuantity, smoke);
            for (int index = 0; index < CellsPerChunk; index++)
            {
                if (!burning.Contains(materials[index]))
                {
                    continue;
                }

                int y = index / Size % Size;
                float over = 0f;
                for (int up = 1; up <= SmokeCellsOver && y + up < Size; up++)
                {
                    over = Math.Max(over, smoke[index + (up * Size)]);
                }

                found.Add(new BurningCell(origin.Offset(index % Size, y, index / (Size * Size)), heat[index], over));
            }
        }

        return found;
    }
}
