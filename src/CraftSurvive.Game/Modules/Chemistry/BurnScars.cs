using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using Rusty.Engine.Persistence;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// A grass fire's scars healing (#10296; owner 2026-10-26: a scar grows back, it is not a permanent state). The chemistry
/// field writes burnt ground into the terrain as the scar block; this keeps when each column of it burnt, and every few
/// seconds grows back the scar cells that are due, near the player, to what the generator made there. Writing a cell back
/// to its generated block releases its terrain override, so healing frees the overlay entries burning took. Cells heal
/// patchily: each one is due somewhere between the earliest share of the regrowth time and the whole of it, by its hash.
/// The ledger is saved; a scar cell it does not know (a lost save) counts as burnt when first seen.
/// </summary>
internal sealed class BurnScars
{
    private readonly TerrainWorld terrain;
    private readonly ScarSetting setting;
    private readonly ushort scar;
    private readonly ProductSaveSlot<IReadOnlyList<ScarColumn>> slot;
    private readonly Dictionary<(int X, int Z), double> burnt = [];
    private double sinceHeal;
    private bool dirty;
    private long healed, refused;
    private string lastRefusal = "none";

    internal BurnScars(IEngineContext engine, ProductStore store, SaveIdentity identity, TerrainWorld terrain, ScarSetting setting)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(store);
        this.terrain = terrain ?? throw new ArgumentNullException(nameof(terrain));
        this.setting = setting ?? throw new ArgumentNullException(nameof(setting));
        scar = (ushort)setting.Block;
        slot = new ProductSaveSlot<IReadOnlyList<ScarColumn>>(engine, store, SaveManifest.ChemistryScars, new BurnScarsCodec(identity));
    }

    /// <summary>The scar block: the terrain cells that are burnt ground.</summary>
    internal BlockId Block => setting.Block;

    internal int Columns => burnt.Count;

    /// <summary>Reads the saved ledger; without one, the scars standing in the terrain are timed from when they are first seen.</summary>
    internal void Start()
    {
        burnt.Clear();
        if (slot.Restore() is { Outcome: SaveRestoreOutcome.Restored, State: IReadOnlyList<ScarColumn> saved })
        {
            foreach (ScarColumn column in saved) burnt[(column.ColumnX, column.ColumnZ)] = column.Hours;
        }

        dirty = false;
    }

    /// <summary>Records that these cells burnt at this hour: each one's column restarts its regrowth.</summary>
    internal void Burnt(IEnumerable<VoxelAddress> cells, double hours)
    {
        foreach (VoxelAddress cell in cells)
        {
            burnt[Column(cell)] = hours;
            dirty = true;
        }

        Forget();
    }

    /// <summary>Every few seconds, grows back the due scar cells near the player and saves a changed ledger.</summary>
    internal void Update(double elapsed, double hours, Vector3 near)
    {
        sinceHeal += elapsed;
        if (sinceHeal < setting.HealSeconds)
        {
            return;
        }

        sinceHeal = 0;
        Heal(hours, near);
        if (dirty)
        {
            slot.Save(Ledger());
            dirty = false;
        }
    }

    /// <summary>Saves a changed ledger now.</summary>
    internal void Flush()
    {
        if (dirty)
        {
            slot.Save(Ledger());
            dirty = false;
        }
    }

    internal string Readout() => string.Create(CultureInfo.InvariantCulture,
        $"scars columns={burnt.Count} healed={healed} refused={refused} lastRefusal={lastRefusal} restore={slot.RestoreOutcome} saves={slot.Saves}");

    private void Heal(double hours, Vector3 near)
    {
        double regrowHours = setting.RegrowDays * HoursPerDay;
        double reach = setting.HealReach * setting.HealReach;
        List<VoxelAddress> due = [];
        HashSet<(int X, int Z)> standing = [];
        foreach (VoxelAddress cell in terrain.EditedTo(scar))
        {
            (int, int) column = Column(cell);
            if (!burnt.TryGetValue(column, out double at))
            {
                burnt[column] = hours;
                dirty = true;
                standing.Add(column);
                continue;
            }

            bool close = Vector2.DistanceSquared(new Vector2(cell.X, cell.Z), new Vector2(near.X, near.Z)) <= reach && terrain.IsResident(cell);
            if (due.Count < setting.CellsPerHeal && close && ScarHealing.Due(cell.X, cell.Z, at, hours, regrowHours, setting.Earliest))
            {
                due.Add(cell);
            }
            else
            {
                standing.Add(column);
            }
        }

        foreach (IGrouping<ushort, VoxelAddress> back in due.GroupBy(terrain.GeneratedAt))
        {
            TerrainWorldEditResult result = terrain.TryEditCells([.. back], TerrainEditKind.Set, back.Key);
            if (result is TerrainWorldEditApplied)
            {
                healed += back.Count();
            }
            else
            {
                refused += back.Count();
                lastRefusal = TerrainWorldEditResult.Format(result);
                foreach (VoxelAddress cell in back) standing.Add(Column(cell));
            }
        }

        // A column with no scar left has healed whole.
        foreach ((int X, int Z) column in burnt.Keys.Where(column => !standing.Contains(column)).ToList())
        {
            burnt.Remove(column);
            dirty = true;
        }
    }

    /// <summary>Past the ledger's bound, the oldest columns are forgotten: their scars count as burnt when next seen.</summary>
    private void Forget()
    {
        if (burnt.Count <= ScarColumn.MostColumns)
        {
            return;
        }

        foreach ((int X, int Z) column in burnt.OrderBy(pair => pair.Value).Take(burnt.Count - ScarColumn.MostColumns).Select(pair => pair.Key).ToList())
        {
            burnt.Remove(column);
        }
    }

    private List<ScarColumn> Ledger() =>
        [.. burnt.Select(pair => new ScarColumn(pair.Key.X, pair.Key.Z, pair.Value)).OrderBy(column => column.ColumnZ).ThenBy(column => column.ColumnX)];

    private static (int X, int Z) Column(VoxelAddress cell) => ((int)(cell.X >> ColumnShift), (int)(cell.Z >> ColumnShift));

    /// <summary>A column is 16 cells across, a chemistry chunk's.</summary>
    private const int ColumnShift = 4;

    private const double HoursPerDay = 24;

}
