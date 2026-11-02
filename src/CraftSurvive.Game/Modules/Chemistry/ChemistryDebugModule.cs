using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>Live-debug adapter over the chemistry field around the player (#10293): read it, light it, read a cell, and cast on it (#10529).</summary>
public sealed class ChemistryDebugModule : IDebugCommandModule
{
    private readonly Func<ChemistryModule> chemistrySource;
    private readonly Func<(double X, double Z)> playerColumn;
    private readonly Func<ChemistryCasting> castingSource;

    internal ChemistryDebugModule(Func<ChemistryModule> chemistry, Func<(double X, double Z)> playerColumn, Func<ChemistryCasting> casting)
    {
        chemistrySource = chemistry ?? throw new ArgumentNullException(nameof(chemistry));
        this.playerColumn = playerColumn ?? throw new ArgumentNullException(nameof(playerColumn));
        castingSource = casting ?? throw new ArgumentNullException(nameof(casting));
    }

    [DebugCommand("craft.chem.readout", Description = "Reads the chemistry field (#10293): chunks loaded and active, step cost, fires shown, and burnt ground written, waiting and refused against the terrain overlay's reserve.")]
    public string Readout() => chemistrySource().Readout();

    [DebugCommand("craft.chem.chunks", Description = "Lists the field's loaded chunks: each one's first cell, whether it is awake (stepped) or asleep, and its commonest materials.")]
    public string Chunks() => chemistrySource().ChunksReadout();

    [DebugCommand("craft.chem.changes", Description = "Diagnoses sleep: the first call keeps the field's quantities; the next lists, for each awake chunk, each quantity's largest change since then and where.")]
    public string Changes() => chemistrySource().ChangesReadout();

    [DebugCommand("craft.chem.inline", Description = "Comparison (#10384): 1 has the update wait for the field's steps, as before the worker; 0 (the default) steps on the worker.")]
    public string Inline(int on) => chemistrySource().SetInline(on != 0);

    [DebugCommand("craft.chem.weather", Description = "Reads the ambient the field took from the weather: air heat, the wind's drift in cells a second, rain, and the lying snow's cover.")]
    public string Weather() => chemistrySource().AmbientReadout();

    [DebugCommand("craft.chem.ignite", Description = "Assisted: heats the ground at the surface of world column x z, as a torch would. Whether it catches is the field's.")]
    public string Ignite(double x, double z) => chemistrySource().IgniteAt(x, z);

    [DebugCommand("craft.chem.ignite.near", Description = "Assisted: heats the ground at the surface this many metres east and south of the player.")]
    public string IgniteNear(double east, double south)
    {
        (double x, double z) = playerColumn();
        return chemistrySource().IgniteAt(x + east, z + south);
    }

    [DebugCommand("craft.chem.cell", Description = "Reads a world cell in the field: its material, whether it is open to the sky, and every quantity.")]
    public string Cell(long x, long y, long z) => chemistrySource().CellReadout(x, y, z);

    [DebugCommand("craft.chem.compose", Description = "The magic compiler (#10529): composes a hexagram, outer (upper) trigram over inner (lower), and says what it compiles to: craft.chem.compose heaven fire.")]
    public string Compose(string outer, string inner) => castingSource().Compose(outer, inner);

    [DebugCommand("craft.chem.cast", Description = "Casts the composed formula where the player aims, as the C key does.")]
    public string Cast() => castingSource().Cast();

    [DebugCommand("craft.chem.cast.at", Description = "Casts the composed formula at a world cell, as aiming at its top face would: craft.chem.cast.at x y z.")]
    public string CastAt(long x, long y, long z) => castingSource().CastAt(x, y, z);

    [DebugCommand("craft.chem.formula", Description = "The composed formula as compiled JSON: what a relic would carry, castable without the grammar.")]
    public string Formula() => castingSource().FormulaJson();

    [DebugCommand("craft.chem.formula.cast", Description = "Casts one of the formulas the game carries (content/game/chemistry/formulas.json) at a world cell: craft.chem.formula.cast torch x y z.")]
    public string CastFormula(string id, long x, long y, long z) => castingSource().CastFormula(id, (x, y, z));

    [DebugCommand("craft.chem.table", Description = "What each of the 64 hexagrams compiles to in the grammar, one line each.")]
    public string Table() => castingSource().Table();

    [DebugCommand("craft.chem.overlay", Description = "Shows one quantity of the field as cubes (heat, moisture, smoke, charge, gust, bind), or none.")]
    public string Overlay(string quantity) => castingSource().ShowOverlay(quantity);
}
